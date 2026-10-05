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

    public static IEnumerable<object[]> ProviderAcknowledgmentCases()
    {
        foreach (var route in new[] { "info", "support-plaintext" })
            foreach (var (json, providerStatus, expected) in new[]
            {
                ("{}", 201, 400), ("null", 201, 400), ("{\"messageId\":null}", 201, 400),
                ("{\"messageId\":\"\"}", 201, 200), ("{\"messageId\":\" \"}", 201, 200),
                ("{\"messageId\":7}", 201, 502), ("{malformed-provider-synthetic", 201, 502),
                ("provider-rejection-synthetic", 401, 502),
            })
                yield return [route, json, providerStatus, expected];
    }

    [Theory]
    [MemberData(nameof(ProviderAcknowledgmentCases))]
    public async Task ActualProviderAcknowledgment_PreservesSourceNullRuleAndOpaqueFailureBoundary(
        string route, string json, int providerStatus, int expected)
    {
        await using var factory = new LegacyEmailFactory
        {
            ProviderReply = (_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)providerStatus)
            { Content = new StringContent(json, Encoding.UTF8, "application/json") }),
        };
        using var client = factory.Client();
        using var body = LegacyBody(route, "ข้อความ synthetic");
        using var response = await client.PostAsync(Query(route, route.EndsWith("-plaintext", StringComparison.Ordinal) ? null : "ข้อความ synthetic"), body);
        Assert.Equal(expected, (int)response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
        Assert.Single(factory.Payloads);
    }

    [Theory]
    [InlineData("info", "to")]
    [InlineData("info", "replyTo")]
    [InlineData("info", "cc")]
    [InlineData("info", "bcc")]
    [InlineData("support-plaintext", "to")]
    [InlineData("support-plaintext", "replyTo")]
    [InlineData("support-plaintext", "cc")]
    [InlineData("support-plaintext", "bcc")]
    public async Task ActualRecipientValidation_RejectsMalformedAddressBeforeTransport(string route, string field)
    {
        await using var factory = new LegacyEmailFactory();
        using var client = factory.Client();
        var query = Query(route, route.EndsWith("-plaintext", StringComparison.Ordinal) ? null : "body", missing: field)
            + "&" + field + "=not-an-address";
        using var body = LegacyBody(route, "body");
        using var response = await client.PostAsync(query, body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
        Assert.Empty(factory.Payloads);
    }

    [Theory]
    [InlineData("info")]
    [InlineData("manufacturing")]
    [InlineData("noreply")]
    [InlineData("support")]
    public async Task ActualOptionalRecipients_OmitsBlankFieldsAndPreservesNonblankOrder(string route)
    {
        await using var factory = new LegacyEmailFactory();
        using var client = factory.Client();
        var query = "/Emails/" + route + "?to=recipient@example.invalid&subject=synthetic&body=body&replyTo=%20"
            + "&cc=&cc=cc2@example.invalid&cc=%20&cc=cc1@example.invalid&bcc=&bcc=%20";
        using var body = LegacyBody(route, "body");
        using var response = await client.PostAsync(query, body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = Assert.Single(factory.Payloads);
        Assert.False(payload.TryGetProperty("replyTo", out _));
        Assert.False(payload.TryGetProperty("bcc", out _));
        Assert.False(payload.TryGetProperty("attachment", out _));
        Assert.Equal(["cc2@example.invalid", "cc1@example.invalid"],
            payload.GetProperty("cc").EnumerateArray().Select(value => value.GetProperty("email").GetString()).ToArray());
    }

    [Theory]
    [InlineData("info")]
    [InlineData("support-plaintext")]
    public async Task ActualTransientProviderRetries_KeepOneWireIdempotencyKeyAndUnchangedPayload(string route)
    {
        var attempts = 0;
        await using var factory = new LegacyEmailFactory
        {
            MaxRetryAttempts = 2,
            ProviderReply = (_, _) => Task.FromResult(++attempts switch
            {
                1 => new HttpResponseMessage(HttpStatusCode.TooManyRequests),
                2 => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
                _ => new HttpResponseMessage(HttpStatusCode.Created) { Content = JsonContent.Create(new { messageId = "recovered-synthetic" }) },
            }),
        };
        using var client = factory.Client();
        using var body = LegacyBody(route, "ข้อความ synthetic");
        using var response = await client.PostAsync(Query(route, route.EndsWith("-plaintext", StringComparison.Ordinal) ? null : "ข้อความ synthetic"), body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, attempts);
        Assert.Equal(3, factory.Payloads.Count);
        var key = factory.Payloads[0].GetProperty("headers").GetProperty("idempotencyKey").GetString();
        Assert.True(Guid.TryParse(key, out _));
        Assert.All(factory.Payloads, payload => Assert.Equal(factory.Payloads[0].GetRawText(), payload.GetRawText()));
    }

    [Theory]
    [InlineData("info")]
    [InlineData("support-plaintext")]
    public async Task ActualCallerAbort_PropagatesToProviderAndDoesNotStartRetry(string route)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var factory = new LegacyEmailFactory
        {
            MaxRetryAttempts = 2,
            ProviderReply = async (_, token) =>
            {
                entered.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { canceled.TrySetResult(); throw; }
                throw new InvalidOperationException("Provider wait must be canceled.");
            },
        };
        using var client = factory.Client();
        using var cancellation = new CancellationTokenSource();
        using var body = LegacyBody(route, "body");
        var send = client.PostAsync(Query(route, route.EndsWith("-plaintext", StringComparison.Ordinal) ? null : "body"), body, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Single(factory.Payloads);
        }
        finally
        {
            cancellation.Cancel();
            try { using var completed = await send.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch { /* Cleanup must not replace the original assertion failure. */ }
        }
    }

    private static HttpContent LegacyBody(string route, string body)
    {
        if (route.EndsWith("-plaintext", StringComparison.Ordinal)) return new StringContent(body, Encoding.UTF8, "text/plain");
        var form = new MultipartFormDataContent();
        form.Add(new StringContent("synthetic"), "fixture");
        return form;
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

    private sealed class LegacyEmailFactory : WebApplicationFactory<NotificationProgram>
    {
        private const string Issuer = "https://email-fixture.invalid";
        private const string Audience = "email-fixture";
        private readonly RSA signingKey = RSA.Create(2048);
        public List<JsonElement> Payloads { get; } = [];
        public bool AllowPermission { get; init; } = true;
        public string HostEnvironment { get; init; } = "Production";
        public bool ControlledProvider { get; init; } = true;
        public int MaxRetryAttempts { get; init; }
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? ProviderReply { get; init; }
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
                    ["Brevo:MaxRetryAttempts"] = MaxRetryAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["Brevo:RetryDelayMilliseconds"] = "0",
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
                    if (ProviderReply is not null) return await ProviderReply(request, token);
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
