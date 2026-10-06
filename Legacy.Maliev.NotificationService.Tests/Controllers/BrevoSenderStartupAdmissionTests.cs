using System.Net;
using System.Security.Cryptography;
using System.Text;
using Legacy.Maliev.NotificationService.Data;
using Legacy.Maliev.NotificationService.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Legacy.Maliev.NotificationService.Tests.Controllers;

public sealed class BrevoSenderStartupAdmissionTests
{
    private const string IdentityFailure = "Brevo sender identities must have valid addresses and display names.";

    [Theory]
    [InlineData("Info", "Address", "")]
    [InlineData("Info", "Address", "PRIVATE-SENDER-FIXTURE")]
    [InlineData("Info", "DisplayName", "  ")]
    [InlineData("Manufacturing", "Address", "")]
    [InlineData("Manufacturing", "Address", "PRIVATE-SENDER-FIXTURE")]
    [InlineData("Manufacturing", "DisplayName", "  ")]
    [InlineData("NoReply", "Address", "")]
    [InlineData("NoReply", "Address", "PRIVATE-SENDER-FIXTURE")]
    [InlineData("NoReply", "DisplayName", "  ")]
    [InlineData("Support", "Address", "")]
    [InlineData("Support", "Address", "PRIVATE-SENDER-FIXTURE")]
    [InlineData("Support", "DisplayName", "  ")]
    public async Task NormalProductionHost_RejectsInvalidConfiguredSenderBeforeServing(string channel, string field, string value)
    {
        await using var factory = new StartupFactory(channel, field, value);
        var error = Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
        Assert.Contains(IdentityFailure, error.Failures);
        Assert.DoesNotContain("PRIVATE-SENDER-FIXTURE", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, factory.ProviderCalls);
    }

    [Theory]
    [InlineData("missing-channel")]
    [InlineData("null-entry")]
    [InlineData("null-dictionary")]
    public async Task NormalProductionHost_RejectsIncompleteSenderConfigurationWithoutUnhandledNull(string mode)
    {
        await using var factory = new StartupFactory(mode: mode);
        var error = Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
        if (mode == "missing-channel")
            Assert.Contains("Brevo senders must include Info, Manufacturing, NoReply and Support.", error.Failures);
        else
            Assert.Contains(IdentityFailure, error.Failures);
        Assert.Equal(0, factory.ProviderCalls);
    }

    [Fact]
    public async Task NormalProductionHost_AdmitsAllFourValidSendersAndRetainsAuthentication()
    {
        await using var factory = new StartupFactory();
        using var client = factory.CreateClient();
        var senders = factory.Services.GetRequiredService<IOptions<BrevoNotificationOptions>>().Value.Senders;
        foreach (var channel in Enum.GetValues<EmailChannel>())
        {
            Assert.Equal(channel + "@example.invalid", senders[channel].Address);
            Assert.Equal("Synthetic " + channel, senders[channel].DisplayName);
        }
        using var response = await client.PostAsync("/Emails/info?to=recipient@example.invalid&subject=synthetic&body=synthetic", null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, factory.ProviderCalls);
    }

    private sealed class StartupFactory(string? channel = null, string? field = null, string? value = null, string? mode = null)
        : WebApplicationFactory<NotificationProgram>
    {
        private readonly RSA signingKey = RSA.Create(2048);
        public int ProviderCalls { get; private set; }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("Jwt:PublicKey", Convert.ToBase64String(Encoding.UTF8.GetBytes(signingKey.ExportSubjectPublicKeyInfoPem())));
            builder.UseSetting("Jwt:Issuer", "https://sender-startup.invalid");
            builder.UseSetting("Jwt:Audience", "sender-startup");
            builder.UseSetting("Notifications:DeliveryIntentsEnabled", "false");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                var values = new Dictionary<string, string?>
                {
                    ["Brevo:ApiKey"] = "synthetic-sender-startup-only",
                    ["Notifications:DeliveryIntentsEnabled"] = "false",
                };
                foreach (var name in Enum.GetNames<EmailChannel>())
                {
                    values[$"Brevo:Senders:{name}:Address"] = name + "@example.invalid";
                    values[$"Brevo:Senders:{name}:DisplayName"] = "Synthetic " + name;
                }
                if (channel is not null) values[$"Brevo:Senders:{channel}:{field}"] = value;
                configuration.AddInMemoryCollection(values);
            });
            builder.ConfigureServices(services =>
            {
                if (mode == "missing-channel") services.PostConfigure<BrevoNotificationOptions>(options => options.Senders.Remove(EmailChannel.Support));
                if (mode == "null-entry") services.PostConfigure<BrevoNotificationOptions>(options => options.Senders[EmailChannel.Info] = null!);
                if (mode == "null-dictionary") services.AddSingleton<IOptionsFactory<BrevoNotificationOptions>>(provider =>
                    new NullDictionaryFactory(provider.GetServices<IValidateOptions<BrevoNotificationOptions>>()));
                services.AddHttpClient<IBrevoNotificationTransport, BrevoNotificationTransport>()
                    .ConfigurePrimaryHttpMessageHandler(() => new NoProviderHandler(() => ProviderCalls++));
            });
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            // WebApplicationFactory retains successful hosts. Own and dispose a failed-start host here.
            var host = builder.Build();
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                host.StartAsync(timeout.Token).GetAwaiter().GetResult();
                return host;
            }
            catch
            {
                host.Dispose();
                throw;
            }
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) signingKey.Dispose();
        }
    }

    // Legal init-only construction; real OptionsFactory validation uses Program's registered validators.
    private sealed class NullDictionaryFactory(IEnumerable<IValidateOptions<BrevoNotificationOptions>> validators)
        : OptionsFactory<BrevoNotificationOptions>([], [], validators)
    {
        protected override BrevoNotificationOptions CreateInstance(string name) => new()
        {
            ApiKey = "synthetic-sender-startup-only",
            Senders = null!,
        };
    }

    private sealed class NoProviderHandler(Action recordCall) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            recordCall();
            throw new InvalidOperationException("A startup fixture must not invoke the provider.");
        }
    }
}
