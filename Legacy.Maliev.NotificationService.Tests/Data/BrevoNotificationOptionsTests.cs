using Legacy.Maliev.NotificationService.Data;
using Legacy.Maliev.NotificationService.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Legacy.Maliev.NotificationService.Tests.Data;

public sealed class BrevoNotificationOptionsTests
{
    [Fact]
    public void BrevoOptions_BindSenderDictionaryByLegacyChannelName()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Brevo:ApiKey"] = "test-key",
                ["Brevo:Senders:Info:Address"] = "info@example.com",
                ["Brevo:Senders:Info:DisplayName"] = "Info",
                ["Brevo:Senders:Manufacturing:Address"] = "manufacturing@example.com",
                ["Brevo:Senders:Manufacturing:DisplayName"] = "Manufacturing",
                ["Brevo:Senders:NoReply:Address"] = "no-reply@example.com",
                ["Brevo:Senders:NoReply:DisplayName"] = "No Reply",
                ["Brevo:Senders:Support:Address"] = "support@example.com",
                ["Brevo:Senders:Support:DisplayName"] = "Support",
            })
            .Build();

        var services = new ServiceCollection();
        services
            .AddOptions<BrevoNotificationOptions>()
            .Bind(configuration.GetSection(BrevoNotificationOptions.SectionName))
            .ValidateDataAnnotations();

        var options = services.BuildServiceProvider().GetRequiredService<IOptions<BrevoNotificationOptions>>().Value;

        Assert.Equal("test-key", options.ApiKey);
        Assert.Equal("info@example.com", options.Senders[EmailChannel.Info].Address);
        Assert.Equal("Support", options.Senders[EmailChannel.Support].DisplayName);
        Assert.Equal(2, options.MaxRetryAttempts);
        Assert.Equal(200, options.RetryDelayMilliseconds);
        Assert.Equal(5000, options.MaxRetryDelayMilliseconds);
        Assert.Equal(10000, options.AttemptTimeoutMilliseconds);
    }
}
