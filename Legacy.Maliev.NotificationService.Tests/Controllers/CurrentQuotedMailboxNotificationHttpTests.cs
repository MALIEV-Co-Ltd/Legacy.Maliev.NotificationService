using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Legacy.Maliev.Intranet.Bff.Employees;
using Legacy.Maliev.NotificationService.Api.Authorization;
using Legacy.Maliev.NotificationService.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;


namespace Legacy.Maliev.NotificationService.Tests.Controllers;

// Actual Program/RS256/MVC/application/provider; only remote HTTP is controlled.
public sealed class CurrentQuotedMailboxNotificationHttpTests
{
    private static readonly string SyntheticProviderKey = new('x', 32);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualRecoveryBffProxy_QuotedRecipientReachesNotificationAndProvider(bool confirmation)
    {
        const string quoted = "\"recovery@office\"@example.invalid";
        const string callback = "https://intranet.example.invalid/recovery?token=synthetic&email=quoted";
        using var factory = new MailboxFactory();
        using var client = factory.Client();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var proxy = new EmployeeRecoveryNotificationProxy(client);
        using var response = confirmation
            ? await proxy.SendEmailConfirmationAsync(quoted, callback, deadline.Token)
            : await proxy.SendPasswordResetAsync(quoted, callback, deadline.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = Assert.Single(factory.Payloads);
        Assert.Equal(quoted, payload.GetProperty("to")[0].GetProperty("email").GetString());
        Assert.Equal("NoReply@example.invalid", payload.GetProperty("sender").GetProperty("email").GetString());
        Assert.Equal(confirmation ? "Confirm your MALIEV Intranet email" : "MALIEV Intranet password reset",
            payload.GetProperty("subject").GetString());
        Assert.Contains(System.Net.WebUtility.HtmlEncode(callback), payload.GetProperty("htmlContent").GetString(), StringComparison.Ordinal);
        foreach (var omitted in new[] { "replyTo", "cc", "bcc", "attachment" })
        {
            Assert.False(payload.TryGetProperty(omitted, out _));
        }
    }

    [Theory]
    [InlineData("Info", "to")]
    [InlineData("Info", "replyTo")]
    [InlineData("Manufacturing", "to")]
    [InlineData("Manufacturing", "replyTo")]
    [InlineData("NoReply", "to")]
    [InlineData("NoReply", "replyTo")]
    [InlineData("Support", "to")]
    [InlineData("Support", "replyTo")]
    public async Task QuotedLocalPartWithEmbeddedAt_ReachesProviderUnchanged(string channel, string field)
    {
        const string quoted = "\"recovery@office\"@example.invalid";
        using var factory = new MailboxFactory();
        using var client = factory.Client();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var response = await SendAsync(client, field == "to" ? quoted : "recipient@example.invalid",
            field == "replyTo" ? quoted : null, deadline.Token, channel);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = Assert.Single(factory.Payloads);
        Assert.Equal(channel + "@example.invalid", payload.GetProperty("sender").GetProperty("email").GetString());
        if (field == "to")
        {
            Assert.Equal(quoted, payload.GetProperty("to")[0].GetProperty("email").GetString());
            Assert.False(payload.TryGetProperty("replyTo", out _));
        }
        else
        {
            Assert.Equal(quoted, payload.GetProperty("replyTo").GetProperty("email").GetString());
        }
    }

    [Theory]
    [InlineData("to", "not-a-mailbox")]
    [InlineData("to", "Display <recipient@example.invalid>")]
    [InlineData("to", " recipient@example.invalid ")]
    [InlineData("replyTo", "not-a-mailbox")]
    [InlineData("replyTo", "Display <recipient@example.invalid>")]
    [InlineData("replyTo", " recipient@example.invalid ")]
    public async Task InvalidOrNormalizedMailbox_RejectsOpaqueBeforeProvider(string field, string value)
    {
        using var factory = new MailboxFactory();
        using var client = factory.Client();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var response = await SendAsync(client, field == "to" ? value : "recipient@example.invalid",
            field == "replyTo" ? value : null, deadline.Token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(factory.Payloads);
        var text = await response.Content.ReadAsStringAsync(deadline.Token);
        Assert.DoesNotContain(value, text, StringComparison.Ordinal);
        Assert.DoesNotContain("recipient@example.invalid", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingRequiredRecipient_Remains400WithoutProvider()
    {
        using var factory = new MailboxFactory();
        using var client = factory.Client();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var response = await SendAsync(client, null, null, deadline.Token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(factory.Payloads);
    }

    [Theory]
    [InlineData("anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("denied", HttpStatusCode.Forbidden)]
    [InlineData("wrong-key", HttpStatusCode.Unauthorized)]
    public async Task QuotedMailbox_StillRequiresValidSignatureAndSendPermission(string identity, HttpStatusCode expected)
    {
        using var factory = new MailboxFactory();
        using var client = factory.Client(identity);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var response = await SendAsync(client, "\"recovery@office\"@example.invalid", null, deadline.Token);
        Assert.Equal(expected, response.StatusCode);
        Assert.Empty(factory.Payloads);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string? to, string? replyTo, CancellationToken token, string channel = "NoReply")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/notifications/v1/email/" + channel)
        {
            Content = JsonContent.Create(new { to, subject = "Synthetic recovery", body = "<p>Recovery ทดสอบ</p>", replyTo, cc = (object?)null, bcc = (object?)null, attachments = (object?)null }),
        };
        return await client.SendAsync(request, token);
    }
    private sealed class MailboxFactory : WebApplicationFactory<NotificationProgram>
    {
        private readonly RSA signingKey = RSA.Create(2048);
        public List<JsonElement> Payloads { get; } = [];
        public HttpClient Client(string identity = "allowed")
        {
            var client = CreateClient();
            if (identity == "anonymous") return client;
            using var wrongKey = identity == "wrong-key" ? RSA.Create(2048) : null;
            var claims = new List<Claim> { new("sub", "synthetic-web") };
            if (identity != "denied") claims.Add(new("permissions", NotificationPermissions.Send));
            var now = DateTime.UtcNow;
            var jwt = new JwtSecurityToken("https://issuer.example.invalid", "https://notification.example.invalid", claims, now.AddMinutes(-1), now.AddMinutes(5),
                new SigningCredentials(new RsaSecurityKey(wrongKey ?? signingKey), SecurityAlgorithms.RsaSha256));
            client.DefaultRequestHeaders.Authorization = new("Bearer", new JwtSecurityTokenHandler().WriteToken(jwt));
            return client;
        }
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("Jwt:PublicKey", Convert.ToBase64String(Encoding.UTF8.GetBytes(signingKey.ExportSubjectPublicKeyInfoPem())));
            builder.UseSetting("Jwt:Issuer", "https://issuer.example.invalid");
            builder.UseSetting("Jwt:Audience", "https://notification.example.invalid");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                var values = new Dictionary<string, string?> { ["Brevo:ApiKey"] = SyntheticProviderKey, ["Brevo:MaxRetryAttempts"] = "0", ["Notifications:DeliveryIntentsEnabled"] = "false" };
                foreach (var channel in new[] { "Info", "Manufacturing", "NoReply", "Support" })
                {
                    values[$"Brevo:Senders:{channel}:Address"] = channel + "@example.invalid";
                    values[$"Brevo:Senders:{channel}:DisplayName"] = "Synthetic " + channel;
                }
                config.AddInMemoryCollection(values);
            });
            builder.ConfigureServices(services => services.AddHttpClient<IBrevoNotificationTransport, BrevoNotificationTransport>()
                .ConfigurePrimaryHttpMessageHandler(() => new ProviderHandler(Payloads)));
        }
        protected override void Dispose(bool disposing)
        {
            try
            {
                base.Dispose(disposing);
            }
            finally
            {
                if (disposing) signingKey.Dispose();
            }
        }
    }

    private sealed class ProviderHandler(List<JsonElement> payloads) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.Equal("https://api.brevo.com/v3/smtp/email", request.RequestUri!.AbsoluteUri);
            Assert.Equal(SyntheticProviderKey, Assert.Single(request.Headers.GetValues("api-key")));
            payloads.Add(await request.Content!.ReadFromJsonAsync<JsonElement>(token));
            return new(HttpStatusCode.Created) { Content = JsonContent.Create(new { messageId = "synthetic-provider-id" }) };
        }
    }
}
