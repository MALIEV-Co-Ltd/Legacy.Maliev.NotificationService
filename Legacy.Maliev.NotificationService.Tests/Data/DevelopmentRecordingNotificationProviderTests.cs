using System.Net;
using System.Security.Cryptography;
using System.Text;
using Legacy.Maliev.NotificationService.Data;
using Legacy.Maliev.NotificationService.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Time.Testing;

namespace Legacy.Maliev.NotificationService.Tests.Data;

public sealed class DevelopmentRecordingNotificationProviderTests
{
    [Fact]
    public async Task SendAsync_RecordsOnlyDeliveryMetadataAndReturnsStableSuccess()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 7, 15, 12, 30, 0, TimeSpan.Zero));
        var provider = new DevelopmentRecordingNotificationProvider(clock);

        var result = await provider.SendAsync(
            EmailChannel.NoReply,
            new NotificationSendRequest
            {
                To = "local.changed@maliev.test",
                Subject = "Confirm your new MALIEV email address",
                Body = "secret single-use token",
                Attachments = [new NotificationAttachment("secret.txt", "text/plain", [1, 2, 3])],
            },
            default);

        var recorded = Assert.Single(provider.Snapshot());
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal(recorded.MessageId, result.ProviderMessageId);
        Assert.Equal(EmailChannel.NoReply, recorded.Channel);
        Assert.Equal("local.changed@maliev.test", recorded.To);
        Assert.Equal("Confirm your new MALIEV email address", recorded.Subject);
        Assert.Equal(clock.GetUtcNow(), recorded.RecordedAt);
        Assert.DoesNotContain(
            typeof(DevelopmentRecordedNotification).GetProperties(),
            property => property.Name is "Body" or "Attachments" or "ReplyTo" or "Cc" or "Bcc");
    }

    [Fact]
    public async Task SendAsync_WhenCancelled_DoesNotRecordDelivery()
    {
        var provider = new DevelopmentRecordingNotificationProvider();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => provider.SendAsync(
            EmailChannel.Info,
            new NotificationSendRequest
            {
                To = "local.customer@maliev.test",
                Subject = "Subject",
                Body = "Body",
            },
            cancellation.Token));
        Assert.Empty(provider.Snapshot());
    }

    [Fact]
    public void Program_GatesRecordingProviderAndDiagnosticsToExplicitDevelopmentMode()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root,
            "Legacy.Maliev.NotificationService.Api",
            "Program.cs"));

        Assert.Contains("builder.Environment.IsDevelopment()", source, StringComparison.Ordinal);
        Assert.Contains("Notifications:UseDevelopmentRecordingProvider", source, StringComparison.Ordinal);
        Assert.Contains("/notifications/development/recorded", source, StringComparison.Ordinal);
        Assert.Contains("ExcludeFromDescription", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Development", HttpStatusCode.OK)]
    [InlineData("Production", HttpStatusCode.NotFound)]
    public async Task DiagnosticsEndpoint_IsAvailableOnlyInDevelopment(
        string environment,
        HttpStatusCode expectedStatus)
    {
        using var rsa = RSA.Create(2048);
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.UseSetting("Notifications:UseDevelopmentRecordingProvider", "true");
            builder.UseSetting(
                "Jwt:PublicKey",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(rsa.ExportSubjectPublicKeyInfoPem())));
            builder.UseSetting("Brevo:ApiKey", "test-placeholder");
        });
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/notifications/development/recorded", CancellationToken.None);

        Assert.Equal(expectedStatus, response.StatusCode);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "Legacy.Maliev.NotificationService.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
