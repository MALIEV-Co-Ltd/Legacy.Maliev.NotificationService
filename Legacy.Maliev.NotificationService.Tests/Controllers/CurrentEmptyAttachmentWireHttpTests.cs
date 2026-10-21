using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Legacy.Maliev.NotificationService.Api.Authorization;
using Legacy.Maliev.NotificationService.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace Legacy.Maliev.NotificationService.Tests.Controllers;

// Actual Program/Production/RS256 and real application/provider/transport; only external HTTP is controlled.
public sealed class CurrentEmptyAttachmentWireHttpTests
{
    private static readonly string SyntheticProviderKey = new('x', 32);

    [Theory]
    [InlineData("Info", "omitted")]
    [InlineData("Info", "null")]
    [InlineData("Info", "empty")]
    [InlineData("Info", "populated")]
    [InlineData("Manufacturing", "omitted")]
    [InlineData("Manufacturing", "null")]
    [InlineData("Manufacturing", "empty")]
    [InlineData("Manufacturing", "populated")]
    [InlineData("NoReply", "omitted")]
    [InlineData("NoReply", "null")]
    [InlineData("NoReply", "empty")]
    [InlineData("NoReply", "populated")]
    [InlineData("Support", "omitted")]
    [InlineData("Support", "null")]
    [InlineData("Support", "empty")]
    [InlineData("Support", "populated")]
    public async Task JsonAttachmentPresence_MapsOriginalNoAttachmentDisposition(string channel, string presence)
    {
        using var factory = new AttachmentFactory();
        using var client = factory.Client();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var body = new Dictionary<string, object?>
        {
            ["to"] = "recipient@example.invalid",
            ["subject"] = "Synthetic attachment disposition",
            ["body"] = "<p>Body ทดสอบ</p>",
        };
        if (presence != "omitted")
        {
            body["attachments"] = presence switch
            {
                "null" => null,
                "empty" => Array.Empty<object>(),
                "populated" => new[] { new { fileName = "drawing.txt", contentType = "text/plain", content = new byte[] { 1, 2, 3 } } },
                _ => throw new ArgumentOutOfRangeException(nameof(presence)),
            };
        }
        using var response = await client.PostAsJsonAsync("/notifications/v1/email/" + channel, body, deadline.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var receipt = await response.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
        Assert.Equal("synthetic-provider-id", receipt.GetProperty("providerMessageId").GetString());
        var payload = Assert.Single(factory.Payloads);
        Assert.Equal(channel + "@example.invalid", payload.GetProperty("sender").GetProperty("email").GetString());
        Assert.Equal("recipient@example.invalid", payload.GetProperty("to")[0].GetProperty("email").GetString());
        Assert.Equal("<p>Body ทดสอบ</p>", payload.GetProperty("htmlContent").GetString());
        if (presence == "populated")
        {
            var attachment = Assert.Single(payload.GetProperty("attachment").EnumerateArray());
            Assert.Equal("drawing.txt", attachment.GetProperty("name").GetString());
            Assert.Equal("AQID", attachment.GetProperty("content").GetString());
        }
        else
        {
            Assert.False(payload.TryGetProperty("attachment", out _));
        }
    }

    [Theory]
    [InlineData("Info")]
    [InlineData("Manufacturing")]
    [InlineData("NoReply")]
    [InlineData("Support")]
    public async Task LegacyNoFiles_StillOmitsAttachmentsAndReturnsBare200(string channel)
    {
        using var factory = new AttachmentFactory();
        using var client = factory.Client();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent("Synthetic form marker"), "unused");
        using var response = await client.PostAsync("/Emails/" + channel + "?to=recipient%40example.invalid&subject=Synthetic&body=Body", content, deadline.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync(deadline.Token));
        var payload = Assert.Single(factory.Payloads);
        Assert.Equal(channel + "@example.invalid", payload.GetProperty("sender").GetProperty("email").GetString());
        Assert.False(payload.TryGetProperty("attachment", out _));
    }

    private sealed class AttachmentFactory : WebApplicationFactory<NotificationProgram>
    {
        private readonly RSA signingKey = RSA.Create(2048);
        public List<JsonElement> Payloads { get; } = [];
        public HttpClient Client()
        {
            var client = CreateClient();
            var claims = new List<Claim> { new("sub", "synthetic-web"), new("permissions", NotificationPermissions.Send) };
            var now = DateTime.UtcNow;
            var jwt = new JwtSecurityToken("https://issuer.example.invalid", "https://notification.example.invalid", claims, now.AddMinutes(-1), now.AddMinutes(5),
                new SigningCredentials(new RsaSecurityKey(signingKey), SecurityAlgorithms.RsaSha256));
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
