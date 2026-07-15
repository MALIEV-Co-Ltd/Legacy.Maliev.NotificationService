using System.Text.Json.Serialization;
using Legacy.Maliev.NotificationService.Application.Interfaces;
using Legacy.Maliev.NotificationService.Application.Services;
using Legacy.Maliev.NotificationService.Data;
using Legacy.Maliev.NotificationService.Domain;
using Maliev.Aspire.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddDefaultApiVersioning();
builder.AddStandardCors();
builder.AddJwtAuthentication();
builder.AddStandardMiddleware(options => options.EnableRequestLogging = true);
builder.AddStandardOpenApi(
    title: "Legacy MALIEV Notification Service API",
    description: "Temporary .NET 10 compatibility service preserving the legacy email notification API contract.");

var useDevelopmentRecordingProvider = builder.Environment.IsDevelopment()
    && builder.Configuration.GetValue<bool>("Notifications:UseDevelopmentRecordingProvider");

if (useDevelopmentRecordingProvider)
{
    builder.Services.AddSingleton<DevelopmentRecordingNotificationProvider>();
    builder.Services.AddSingleton<INotificationProvider>(services =>
        services.GetRequiredService<DevelopmentRecordingNotificationProvider>());
}
else
{
    builder.Services
        .AddOptions<BrevoNotificationOptions>()
        .Bind(builder.Configuration.GetSection(BrevoNotificationOptions.SectionName))
        .ValidateDataAnnotations()
        .Validate(options => HasAllSenders(options.Senders), "Brevo senders must include Info, Manufacturing, NoReply and Support.")
        .ValidateOnStart();
    builder.Services.AddScoped<INotificationProvider, BrevoNotificationProvider>();
}

builder.Services.AddControllers().AddJsonOptions(options =>
    options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull);
builder.Services.AddScoped<INotificationService, NotificationApplicationService>();

var app = builder.Build();

app.UseStandardMiddleware();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.MapDefaultEndpoints("emails");
app.MapControllers();
app.MapApiDocumentation(servicePrefix: "emails");
if (useDevelopmentRecordingProvider)
{
    app.MapGet(
            "/notifications/development/recorded",
            (DevelopmentRecordingNotificationProvider provider) => Results.Ok(provider.Snapshot()))
        .AllowAnonymous()
        .ExcludeFromDescription();
}

await app.RunAsync();

static bool HasAllSenders(IReadOnlyDictionary<EmailChannel, BrevoSenderOptions> senders)
{
    return senders.ContainsKey(EmailChannel.Info) &&
        senders.ContainsKey(EmailChannel.Manufacturing) &&
        senders.ContainsKey(EmailChannel.NoReply) &&
        senders.ContainsKey(EmailChannel.Support);
}

/// <summary>Legacy Notification Service entry point.</summary>
public partial class Program;
