using Legacy.Maliev.NotificationService.Application.Interfaces;
using Legacy.Maliev.NotificationService.Application.Services;
using Legacy.Maliev.NotificationService.Data;
using Maliev.Aspire.ServiceDefaults.IAM;
using Maliev.Aspire.ServiceDefaults.LegacyAuth;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Legacy.Maliev.NotificationService.Api.Controllers;

namespace Legacy.Maliev.NotificationService.Api;

/// <summary>Default-off audited feature registration; never provisions or applies schema.</summary>
internal static class DeliveryIntentRegistration
{
    internal static bool AddDeliveryIntents(this WebApplicationBuilder builder)
    {
        var enabled = builder.Configuration.GetValue<bool>("Notifications:DeliveryIntentsEnabled");
        builder.Services.AddControllers(options => options.Conventions.Add(new DeliveryIntentVisibilityConvention(enabled)));
        if (!enabled) return false;
        var connection = builder.Configuration.GetConnectionString("NotificationDeliveryIntentDbContext");
        if (string.IsNullOrWhiteSpace(connection)) throw new DeliveryIntentUnavailableException();
        var section = builder.Configuration.GetSection("Notifications:IntentKeys");
        Dictionary<string, byte[]> keys;
        try { keys = section.GetSection("Keys").GetChildren().ToDictionary(child => child.Key, child => Convert.FromBase64String(child.Value ?? string.Empty)); }
        catch (FormatException) { throw new DeliveryIntentUnavailableException(); }
        builder.Services.AddSingleton(new NotificationIntentBinding(section["ActiveKeyId"] ?? string.Empty, keys));
        builder.Services.AddDbContextFactory<DeliveryIntentDbContext>(options => options.UseNpgsql(connection,
            provider => { provider.EnableRetryOnFailure(3); provider.MigrationsHistoryTable("__EFMigrationsHistory", "public"); }));
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddScoped<IDeliveryIntentStore, PostgresDeliveryIntentStore>();
        builder.Services.AddScoped<DeliveryIntentAdmissionService>();
        builder.Services.AddScoped<DeliveryIntentExecutionService>();
        builder.Services.AddHttpClient<IDeliveryIntentSubmission, BrevoDeliveryIntentSubmission>(BrevoDeliveryIntentSubmission.ClientName, client =>
        {
            client.BaseAddress = new Uri("https://api.brevo.com/v3/"); client.Timeout = TimeSpan.FromSeconds(65);
        })
            .RedactLoggedHeaders(["api-key", "Authorization"])
            // This fixed-origin provider client must have no shared retry/delegating policy.
            .ConfigureAdditionalHttpMessageHandlers((handlers, _) => handlers.Clear())
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        var configured = builder.Configuration["Services:IAMService:BaseUrl"];
        var auth = builder.Configuration["Services:Auth:BaseUrl"] ?? builder.Configuration["Services:Auth"];
        if (!CanonicalHttpsOrigin(configured) || !CanonicalHttpsOrigin(auth))
            throw new DeliveryIntentUnavailableException();
        builder.AddLegacyAuthServiceTokenExchange();
        builder.Services.AddHttpClient(LegacyServiceAccessTokenProvider.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        // Do not inject a new authority into the unchanged V1 permission handler.
        builder.Services.AddScoped<IamServiceClient>();
        builder.Services.AddHttpClient("IAMService", client =>
        { client.BaseAddress = new Uri(configured!); client.Timeout = TimeSpan.FromSeconds(10); })
            .RedactLoggedHeaders(["X-Maliev-IAM-Live-Check-Key"])
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
            .AddLegacyServiceAuthentication();
        return true;
    }

    private static bool CanonicalHttpsOrigin(string? value) => !string.IsNullOrWhiteSpace(value) && value == value.Trim() &&
        Uri.TryCreate(value, UriKind.Absolute, out var origin) && origin.Scheme == "https" && origin.Host.Length > 0 &&
        origin.UserInfo.Length == 0 && origin.Query.Length == 0 && origin.Fragment.Length == 0 && origin.AbsolutePath == "/";

    private sealed class DeliveryIntentVisibilityConvention(bool enabled) : IControllerModelConvention
    {
        public void Apply(ControllerModel controller)
        {
            if (controller.ControllerType != typeof(DeliveryIntentsController)) return;
            controller.ApiExplorer.IsVisible = enabled;
            foreach (var action in controller.Actions) action.ApiExplorer.IsVisible = enabled;
        }
    }

    internal static async Task RequireDeliveryIntentReadinessAsync(this WebApplication app)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, app.Lifetime.ApplicationStopping);
        using var scope = app.Services.CreateScope();
        try
        {
            await scope.ServiceProvider.GetRequiredService<IDeliveryIntentStore>().ReadAsync(
                app.Configuration["Jwt:Issuer"] ?? string.Empty, "service:legacy-accounting", Guid.Empty, caller.Token);
        }
        catch (Exception) { throw new InvalidOperationException("Notification intent physical readiness is unavailable."); }
    }
}
