using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Legacy.Maliev.Intranet.Auth;
using Legacy.Maliev.Intranet.Bff.Orders;
using Legacy.Maliev.NotificationService.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Legacy.Maliev.NotificationService.Tests.Controllers;

// Whole pinned current BFF consumer, real service-token provider/handler and Notification runtime.
// External service-login/provider HTTP is controlled. This is not live AuthService or order-saga acceptance.
public sealed class IntranetOrderNotificationJoinedHttpTests
{
    [Fact]
    public async Task CurrentConsumer_UsesCachedServiceIdentityAndPreservesProviderWireAndJsonAcknowledgment()
    {
        await using var factory = new JoinedFactory();
        using var tokens = factory.Tokens();
        using var client = factory.ConsumerClient(tokens);
        var consumer = new OrderNotificationProxy(client);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var response = await consumer.SendCreatedAsync("recipient@example.invalid", 84, default);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var acknowledgment = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("joined-provider-ack", acknowledgment.GetProperty("providerMessageId").GetString());
        }

        Assert.Equal(1, factory.AuthCalls);
        Assert.Equal(2, factory.Payloads.Count);
        Assert.All(factory.Payloads, payload =>
        {
            Assert.Equal("NoReply@example.invalid", payload.GetProperty("sender").GetProperty("email").GetString());
            Assert.Equal("Synthetic NoReply", payload.GetProperty("sender").GetProperty("name").GetString());
            Assert.Equal("recipient@example.invalid", payload.GetProperty("to")[0].GetProperty("email").GetString());
            Assert.Equal("Manufacturing Order #84", payload.GetProperty("subject").GetString());
            Assert.Equal("<p>Hello,</p><p>Your order has been created successfully. We will review it and provide a detailed quotation as soon as possible.</p><p>This message was automatically generated. Please do not reply.</p>",
                payload.GetProperty("htmlContent").GetString());
            Assert.Equal("mail-tracking@maliev.com", Assert.Single(payload.GetProperty("bcc").EnumerateArray()).GetProperty("email").GetString());
            foreach (var name in new[] { "replyTo", "cc", "attachment" }) Assert.False(payload.TryGetProperty(name, out _));
            Assert.True(Guid.TryParse(payload.GetProperty("headers").GetProperty("idempotencyKey").GetString(), out _));
        });
        Assert.NotEqual(factory.Payloads[0].GetProperty("headers").GetProperty("idempotencyKey").GetString(),
            factory.Payloads[1].GetProperty("headers").GetProperty("idempotencyKey").GetString());
    }

    [Fact]
    public async Task CurrentConsumer_InvalidRecipientIsRejectedWithoutProviderSubmission()
    {
        await using var factory = new JoinedFactory();
        using var tokens = factory.Tokens();
        using var client = factory.ConsumerClient(tokens);
        using var response = await new OrderNotificationProxy(client).SendCreatedAsync("private-invalid-recipient", 84, default);
        await AssertOpaqueAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal(1, factory.AuthCalls);
        Assert.Empty(factory.Payloads);
    }

    [Fact]
    public async Task CurrentConsumer_ProviderRejectionRemainsOpaqueAndDoesNotResubmit()
    {
        await using var factory = new JoinedFactory { ProviderStatus = HttpStatusCode.Unauthorized };
        using var tokens = factory.Tokens();
        using var client = factory.ConsumerClient(tokens);
        using var response = await new OrderNotificationProxy(client).SendCreatedAsync("recipient@example.invalid", 84, default);
        await AssertOpaqueAsync(response, HttpStatusCode.BadGateway);
        Assert.Single(factory.Payloads);
        Assert.Equal(1, factory.AuthCalls);
    }

    [Fact]
    public async Task CurrentConsumer_PermissionRejectionInvalidatesCachedTokenBeforeAnotherAttempt()
    {
        await using var factory = new JoinedFactory { AllowPermission = false };
        using var tokens = factory.Tokens();
        using var client = factory.ConsumerClient(tokens);
        var consumer = new OrderNotificationProxy(client);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var response = await consumer.SendCreatedAsync("recipient@example.invalid", 84, default);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        Assert.Equal(2, factory.AuthCalls);
        Assert.Empty(factory.Payloads);
    }

    [Fact]
    public async Task CurrentConsumer_CallerAbortReachesProviderWithoutAnotherSubmission()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var factory = new JoinedFactory
        {
            ProviderReply = async token =>
            {
                entered.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { canceled.TrySetResult(); throw; }
                throw new InvalidOperationException("Provider wait must be canceled.");
            },
        };
        using var tokens = factory.Tokens();
        using var client = factory.ConsumerClient(tokens);
        using var cancellation = new CancellationTokenSource();
        var send = new OrderNotificationProxy(client).SendCreatedAsync("recipient@example.invalid", 84, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send.WaitAsync(TimeSpan.FromSeconds(10)));
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Single(factory.Payloads);
        }
        finally { await cancellation.CancelAsync(); }
    }

    private static async Task AssertOpaqueAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.Equal(expected, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        foreach (var marker in new[] { "recipient@example.invalid", "private-invalid-recipient", "provider-private-only",
            "synthetic-service-secret-only", "synthetic-brevo-join-only", "browser-session-private-only" })
            Assert.DoesNotContain(marker, text, StringComparison.Ordinal);
    }

    private sealed class JoinedFactory : WebApplicationFactory<NotificationProgram>
    {
        private readonly RSA signingKey = RSA.Create(2048);
        public int AuthCalls { get; private set; }
        public bool AllowPermission { get; init; } = true;
        public HttpStatusCode ProviderStatus { get; init; } = HttpStatusCode.Created;
        public Func<CancellationToken, Task<HttpResponseMessage>>? ProviderReply { get; init; }
        public List<JsonElement> Payloads { get; } = [];

        public ServiceAccessTokenProvider Tokens() => new(Services.GetRequiredService<IHttpClientFactory>(),
            Options.Create(new ServiceAuthenticationOptions { ClientId = "legacy-intranet", ClientSecret = "synthetic-service-secret-only" }),
            TimeProvider.System, NullLogger<ServiceAccessTokenProvider>.Instance);

        public HttpClient ConsumerClient(ServiceAccessTokenProvider tokens)
        {
            var handler = new LegacyServiceAuthenticationHandler(tokens) { InnerHandler = Server.CreateHandler() };
            var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost"), Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.Authorization = new("Bearer", "browser-session-private-only");
            return client;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("Jwt:PublicKey", Convert.ToBase64String(Encoding.UTF8.GetBytes(signingKey.ExportSubjectPublicKeyInfoPem())));
            builder.UseSetting("Jwt:Issuer", "https://order-join.invalid");
            builder.UseSetting("Jwt:Audience", "order-join");
            builder.UseSetting("Notifications:DeliveryIntentsEnabled", "false");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                var values = new Dictionary<string, string?>
                {
                    ["Brevo:ApiKey"] = "synthetic-brevo-join-only",
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
                services.AddHttpClient("service-auth", client => client.BaseAddress = new Uri("https://auth-join.invalid"))
                    .ConfigurePrimaryHttpMessageHandler(() => new ControlledHandler(async (request, token) =>
                    {
                        Assert.Equal("https://auth-join.invalid/auth/v1/service/login", request.RequestUri!.AbsoluteUri);
                        Assert.Null(request.Headers.Authorization);
                        var payload = await request.Content!.ReadFromJsonAsync<JsonElement>(token);
                        Assert.Equal("legacy-intranet", payload.GetProperty("clientId").GetString());
                        Assert.Equal("synthetic-service-secret-only", payload.GetProperty("clientSecret").GetString());
                        AuthCalls++;
                        var claims = new List<Claim> { new("sub", "service:legacy-intranet"), new("identity_kind", "service") };
                        if (AllowPermission) claims.Add(new("permissions", "legacy.notifications.send"));
                        var now = DateTime.UtcNow;
                        var jwt = new JwtSecurityToken("https://order-join.invalid", "order-join", claims,
                            now.AddMinutes(-1), now.AddMinutes(5),
                            new SigningCredentials(new RsaSecurityKey(signingKey), SecurityAlgorithms.RsaSha256));
                        return new HttpResponseMessage(HttpStatusCode.OK)
                        { Content = JsonContent.Create(new { accessToken = new JwtSecurityTokenHandler().WriteToken(jwt), expiresIn = 300 }) };
                    }));
                services.AddHttpClient<IBrevoNotificationTransport, BrevoNotificationTransport>()
                    .ConfigurePrimaryHttpMessageHandler(() => new ControlledHandler(async (request, token) =>
                    {
                        Assert.Equal("https://api.brevo.com/v3/smtp/email", request.RequestUri!.AbsoluteUri);
                        Assert.Equal("synthetic-brevo-join-only", Assert.Single(request.Headers.GetValues("api-key")));
                        Assert.Null(request.Headers.Authorization);
                        var payload = await request.Content!.ReadFromJsonAsync<JsonElement>(token);
                        Assert.DoesNotContain("synthetic-service-secret-only", payload.GetRawText(), StringComparison.Ordinal);
                        Assert.DoesNotContain("browser-session-private-only", payload.GetRawText(), StringComparison.Ordinal);
                        Payloads.Add(payload);
                        if (ProviderReply is not null) return await ProviderReply(token);
                        return new HttpResponseMessage(ProviderStatus)
                        { Content = ProviderStatus == HttpStatusCode.Created
                            ? JsonContent.Create(new { messageId = "joined-provider-ack" })
                            : new StringContent("provider-private-only") };
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
}
