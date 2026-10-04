using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Legacy.Maliev.NotificationService.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.IdentityModel.Tokens;

namespace Legacy.Maliev.NotificationService.Tests.Controllers;

// Exercises runtime controllers, RS256 signed-permission admission, validation and Brevo serialization.
// Only provider HTTP is controlled; no provider, controller or authentication replacement is registered.
public sealed class LegacyEmailHttpAcceptanceTests
{
    [Fact]
    public async Task LegacyBrevoPrimaryHandler_DoesNotFollowRedirectsWithRuntimeProviderCredential()
    {
        await using var factory = new LegacyEmailFactory { ControlledProvider = false };
        using var client = factory.CreateClient();
        _ = factory.Services.GetRequiredService<IBrevoNotificationTransport>();
        var primary = Assert.Single(factory.PrimaryHandlers.Handlers,
            item => item.Key.EndsWith(nameof(IBrevoNotificationTransport), StringComparison.Ordinal)).Value;
        var redirects = primary switch
        {
            SocketsHttpHandler sockets => sockets.AllowAutoRedirect,
            HttpClientHandler handler => handler.AllowAutoRedirect,
            _ => throw new InvalidOperationException("The real provider primary handler was not observed."),
        };

        Assert.False(redirects, "The provider API key must not follow a redirect to another origin.");
        Assert.Empty(factory.Payloads);
    }

    [Fact]
    public async Task OpenApi_RetainsControllerAndPayloadDescriptions_WithoutSendingMail()
    {
        await using var factory = new LegacyEmailFactory { HostEnvironment = "Development" };
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/emails/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var document = await response.Content.ReadFromJsonAsync<JsonElement>();
        var operation = document.GetProperty("paths").GetProperty("/Emails/info").GetProperty("post");
        Assert.True(operation.TryGetProperty("summary", out var summary), "Legacy XML operation summaries must reach the OpenAPI document.");
        Assert.Equal("Sends an informational email.", summary.GetString());
        var recipient = Assert.Single(operation.GetProperty("parameters").EnumerateArray(),
            parameter => parameter.GetProperty("name").GetString() == "to");
        Assert.Equal("The recipient's email address.", recipient.GetProperty("description").GetString());
        var schemas = document.GetProperty("components").GetProperty("schemas");
        Assert.Equal("JSON request for one provider-independent email notification.",
            schemas.GetProperty("SendEmailNotificationRequest").GetProperty("description").GetString());
        Assert.Equal("Base64-encoded attachment included in a JSON notification request.",
            schemas.GetProperty("SendEmailNotificationAttachment").GetProperty("description").GetString());
        Assert.Empty(factory.Payloads);
    }

    [Theory]
    [InlineData("info", "Info")]
    [InlineData("manufacturing", "Manufacturing")]
    [InlineData("noreply", "NoReply")]
    [InlineData("support", "Support")]
    public async Task MultipartQueryAndFiles_ReachConfiguredSenderWithExactUtf8Attachment(string route, string sender)
    {
        await using var factory = new LegacyEmailFactory();
        using var client = factory.Client();
        using var form = new MultipartFormDataContent();
        var bytes = Encoding.UTF8.GetBytes("ใบเสนอราคา synthetic\n");
        form.Add(new ByteArrayContent(bytes), "files", "fixture.txt");
        using var response = await client.PostAsync(Query(route, body: "<p>ทดสอบ & synthetic</p>"), form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        var payload = Assert.Single(factory.Payloads);
        Assert.Equal(sender + "@example.invalid", payload.GetProperty("sender").GetProperty("email").GetString());
        Assert.Equal("recipient@example.invalid", payload.GetProperty("to")[0].GetProperty("email").GetString());
        Assert.Equal("หัวข้อ & synthetic", payload.GetProperty("subject").GetString());
        Assert.Equal("<p>ทดสอบ & synthetic</p>", payload.GetProperty("htmlContent").GetString());
        Assert.Equal("reply@example.invalid", payload.GetProperty("replyTo").GetProperty("email").GetString());
        Assert.Equal(new[] { "cc1@example.invalid", "cc2@example.invalid" },
            payload.GetProperty("cc").EnumerateArray().Select(item => item.GetProperty("email").GetString()));
        Assert.Equal("bcc@example.invalid", payload.GetProperty("bcc")[0].GetProperty("email").GetString());
        var attachment = Assert.Single(payload.GetProperty("attachment").EnumerateArray());
        Assert.Equal("fixture.txt", attachment.GetProperty("name").GetString());
        Assert.Equal(bytes, Convert.FromBase64String(attachment.GetProperty("content").GetString()!));
        Assert.True(Guid.TryParse(payload.GetProperty("headers").GetProperty("idempotencyKey").GetString(), out _));
    }

    [Theory]
    [InlineData("info", "Info")]
    [InlineData("manufacturing", "Manufacturing")]
    [InlineData("noreply", "NoReply")]
    [InlineData("support", "Support")]
    public async Task PlaintextRoute_AcceptsRawUtf8BodyThroughHttpBinding(string route, string sender)
    {
        await using var factory = new LegacyEmailFactory();
        using var client = factory.Client();
        const string body = "เรียนลูกค้า\nsynthetic <body> & + %\n";
        // Source Web consumers post text/html to the historically named plaintext routes.
        foreach (var mediaType in new[] { "text/plain", "text/html" })
        {
            factory.Payloads.Clear();
            using var content = new StringContent(body, Encoding.UTF8, mediaType);
            using var response = await client.PostAsync(Query(route + "-plaintext/"), content);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var payload = Assert.Single(factory.Payloads);
            Assert.Equal(body, payload.GetProperty("htmlContent").GetString());
            Assert.Equal(sender + "@example.invalid", payload.GetProperty("sender").GetProperty("email").GetString());
            Assert.False(payload.TryGetProperty("attachment", out _));
        }
    }

    [Theory]
    [InlineData(false, 401)]
    [InlineData(true, 403)]
    public async Task AnonymousOrDeniedPermission_ProducesNoProviderRequest(bool authenticated, int expected)
    {
        await using var factory = new LegacyEmailFactory { AllowPermission = false };
        using var client = authenticated ? factory.Client() : factory.CreateClient();
        using var form = new MultipartFormDataContent();
        using var response = await client.PostAsync(Query("info", "synthetic"), form);

        Assert.Equal(expected, (int)response.StatusCode);
        Assert.Empty(factory.Payloads);
    }

    [Theory]
    [InlineData("to")]
    [InlineData("subject")]
    [InlineData("body")]
    public async Task MissingRequiredQueryValue_ReturnsLegacyFieldErrorWithoutProviderRequest(string missing)
    {
        await using var factory = new LegacyEmailFactory();
        using var client = factory.Client();
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("synthetic"), "fixture");
        using var response = await client.PostAsync(Query("info", "synthetic", missing), form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(errors.TryGetProperty(missing, out _), errors.ToString());
        Assert.Empty(factory.Payloads);
    }

    [Fact]
    public async Task EmptyAttachment_IsRejectedBeforeProviderRequest()
    {
        await using var factory = new LegacyEmailFactory();
        using var client = factory.Client();
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent([]), "files", "empty.txt");
        using var response = await client.PostAsync(Query("manufacturing", "synthetic"), form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(errors.TryGetProperty("files", out _), errors.ToString());
        Assert.Empty(factory.Payloads);
    }

    [Theory]
    [InlineData("to", "nonempty")]
    [InlineData("subject", "nonempty")]
    [InlineData("body", "")]
    public async Task MissingRequiredPlaintextValue_ReturnsFieldErrorWithoutProviderRequest(string missing, string body)
    {
        await using var factory = new LegacyEmailFactory();
        using var client = factory.Client();
        using var content = new StringContent(body, Encoding.UTF8, "text/plain");
        using var response = await client.PostAsync(Query("info-plaintext", missing: missing), content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(errors.TryGetProperty(missing, out _), errors.ToString());
        Assert.Empty(factory.Payloads);
    }

    [Fact]
    public async Task MultipleAttachments_PreserveOrderAndExactBinaryBytes()
    {
        await using var factory = new LegacyEmailFactory();
        using var client = factory.Client();
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent([0, 1, 2, 255]), "files", "first.bin");
        form.Add(new ByteArrayContent([255, 0, 128]), "files", "second.bin");
        using var response = await client.PostAsync(Query("support", "synthetic"), form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = Assert.Single(factory.Payloads);
        var attachments = payload.GetProperty("attachment").EnumerateArray().ToArray();
        Assert.Equal(2, attachments.Length);
        Assert.Equal("first.bin", attachments[0].GetProperty("name").GetString());
        Assert.Equal("AAEC/w==", attachments[0].GetProperty("content").GetString());
        Assert.Equal("second.bin", attachments[1].GetProperty("name").GetString());
        Assert.Equal("/wCA", attachments[1].GetProperty("content").GetString());
    }

    private static string Query(string route, string? body = null, string? missing = null)
    {
        var values = new List<KeyValuePair<string, string>>
        {
            new("to", "recipient@example.invalid"), new("subject", "หัวข้อ & synthetic"),
            new("replyTo", "reply@example.invalid"), new("cc", "cc1@example.invalid"),
            new("cc", "cc2@example.invalid"), new("bcc", "bcc@example.invalid"),
        };
        if (body is not null) values.Add(new("body", body));
        return "/Emails/" + route + "?" + string.Join('&', values.Where(item => item.Key != missing)
            .Select(item => item.Key + "=" + Uri.EscapeDataString(item.Value)));
    }

    private sealed class LegacyEmailFactory : WebApplicationFactory<Program>
    {
        private const string Issuer = "https://email-fixture.invalid";
        private const string Audience = "email-fixture";
        private readonly RSA signingKey = RSA.Create(2048);
        public List<JsonElement> Payloads { get; } = [];
        public bool AllowPermission { get; init; } = true;
        public string HostEnvironment { get; init; } = "Production";
        public bool ControlledProvider { get; init; } = true;
        public PrimaryHandlerObserver PrimaryHandlers { get; } = new();

        public HttpClient Client()
        {
            var client = CreateClient();
            var now = DateTime.UtcNow;
            var claims = new List<Claim>
            {
                new("sub", "service:legacy-email-fixture"), new("identity_kind", "service"),
            };
            if (AllowPermission) claims.Add(new("permissions", "legacy.notifications.send"));
            var token = new JwtSecurityToken(Issuer, Audience,
                claims,
                now.AddMinutes(-1), now.AddMinutes(5),
                new SigningCredentials(new RsaSecurityKey(signingKey), SecurityAlgorithms.RsaSha256));
            client.DefaultRequestHeaders.Authorization = new("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
            return client;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(HostEnvironment);
            builder.UseSetting("Jwt:PublicKey", Convert.ToBase64String(Encoding.UTF8.GetBytes(signingKey.ExportSubjectPublicKeyInfoPem())));
            builder.UseSetting("Jwt:Issuer", Issuer);
            builder.UseSetting("Jwt:Audience", Audience);
            builder.UseSetting("Notifications:DeliveryIntentsEnabled", "false");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                var values = new Dictionary<string, string?>
                {
                    ["Brevo:ApiKey"] = "synthetic-email-fixture-only",
                    ["Brevo:MaxRetryAttempts"] = "0",
                    ["Notifications:DeliveryIntentsEnabled"] = "false",
                };
                foreach (var channel in new[] { "Info", "Manufacturing", "NoReply", "Support" })
                {
                    values[$"Brevo:Senders:{channel}:Address"] = channel + "@example.invalid";
                    values[$"Brevo:Senders:{channel}:DisplayName"] = "Synthetic " + channel;
                }
                configuration.AddInMemoryCollection(values);
            });
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IHttpMessageHandlerBuilderFilter>(PrimaryHandlers);
                if (ControlledProvider) services.AddHttpClient<IBrevoNotificationTransport, BrevoNotificationTransport>().ConfigurePrimaryHttpMessageHandler(() => new ControlledHandler(async (request, token) =>
                {
                    Assert.Equal("https://api.brevo.com/v3/smtp/email", request.RequestUri!.AbsoluteUri);
                    Assert.Equal("synthetic-email-fixture-only", Assert.Single(request.Headers.GetValues("api-key")));
                    Payloads.Add(await request.Content!.ReadFromJsonAsync<JsonElement>(token));
                    return new(HttpStatusCode.Created) { Content = JsonContent.Create(new { messageId = "controlled-email-1" }) };
                }));
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) signingKey.Dispose();
        }
    }

    private sealed class ControlledHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class PrimaryHandlerObserver : IHttpMessageHandlerBuilderFilter
    {
        public Dictionary<string, HttpMessageHandler> Handlers { get; } = [];

        public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) => builder =>
        {
            next(builder);
            Handlers[builder.Name!] = builder.PrimaryHandler;
        };
    }
}
