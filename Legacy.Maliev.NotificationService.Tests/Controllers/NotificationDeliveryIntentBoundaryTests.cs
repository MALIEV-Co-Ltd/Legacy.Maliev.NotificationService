using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Legacy.Maliev.NotificationService.Data;
using Legacy.Maliev.NotificationService.Tests.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace Legacy.Maliev.NotificationService.Tests.Controllers;

// Proposed invoice-intent safety requirements, not claims about Brevo's guarantees.
// Only provider HTTP is controlled; Production JWT/controller/application/transport are real.
public sealed class NotificationDeliveryIntentBoundaryTests(DeliveryIntentPostgresFixture postgres) : IClassFixture<DeliveryIntentPostgresFixture>
{
    private const string OperationId = "fcdac787-947f-43fc-8022-f7be910b77e3";

    [Fact]
    public async Task V2Intent_CompletedSameKeyReplay_DoesNotSubmitProviderAgain()
    {
        await using var factory = await IntentV2Factory.CreateAsync(postgres);
        using var client = factory.Client();
        using var admit = await factory.AdmitAsync(client);
        Assert.Equal(HttpStatusCode.OK, admit.StatusCode);
        using var first = await factory.ExecuteAsync(client);
        using var second = await factory.ExecuteAsync(client);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal("controlled-provider-1", (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("providerMessageId").GetString());
        Assert.Equal(1, factory.ProviderCalls);
    }

    [Fact]
    public async Task V2Intent_SameKeyChangedBody_ConflictsBeforeProvider()
    {
        await using var factory = await IntentV2Factory.CreateAsync(postgres);
        using var client = factory.Client();
        using var admit = await factory.AdmitAsync(client);
        Assert.Equal(HttpStatusCode.OK, admit.StatusCode);
        using var first = await factory.ExecuteAsync(client);
        using var changed = await factory.ExecuteAsync(client, "changed synthetic invoice");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        Assert.Equal(1, factory.ProviderCalls);
    }

    [Fact]
    public async Task V2Intent_ProviderMayAcceptThenLoseResponse_DoesNotBlindResubmit()
    {
        await using var factory = await IntentV2Factory.CreateAsync(postgres);
        factory.LoseProviderAcknowledgment = true;
        using var client = factory.Client();
        using var admit = await factory.AdmitAsync(client);
        Assert.Equal(HttpStatusCode.OK, admit.StatusCode);
        using var response = await factory.ExecuteAsync(client);

        // Approved V2 contract reports durable uncertainty202, not the old-route dependency502.
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var retry = await factory.ExecuteAsync(client);
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        using var read = await client.GetAsync(IntentV2Factory.Path);
        Assert.Equal("outcomeUnknown", (await read.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString());
        Assert.Equal(1, factory.ProviderCalls);
    }

    [Theory]
    [InlineData(false, 401)]
    [InlineData(true, 403)]
    public async Task CurrentInvoicePath_MissingIdentityOrGrant_HasNoProviderEffects(bool authenticated, int status)
    {
        await using var factory = new NotificationFactory();
        using var client = authenticated ? factory.AuthenticatedClient(withPermission: false) : factory.CreateClient();
        using var response = await SendAsync(client);

        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(0, factory.Provider.Submissions);
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string body = "synthetic invoice")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/notifications/v1/email/Info")
        {
            Content = JsonContent.Create(new { to = "recipient@example.invalid", subject = "Invoice fixture", body }),
        };
        request.Headers.Add("Idempotency-Key", OperationId);
        return SendAndDisposeAsync(client, request);
    }

    private static async Task<HttpResponseMessage> SendAndDisposeAsync(HttpClient client, HttpRequestMessage request)
    {
        using (request)
        {
            return await client.SendAsync(request);
        }
    }

    private sealed class NotificationFactory(bool loseFirstResponse = false) : WebApplicationFactory<NotificationProgram>
    {
        private readonly RSA signingKey = RSA.Create(2048);

        public ProviderSentinel Provider { get; } = new(loseFirstResponse);

        public HttpClient AuthenticatedClient(bool withPermission = true)
        {
            var client = CreateClient();
            var claims = new List<Claim> { new(JwtRegisteredClaimNames.Sub, "service:accounting-fixture") };
            if (withPermission)
            {
                claims.Add(new Claim("permissions", "legacy.notifications.send"));
            }

            var now = DateTime.UtcNow;
            var token = new JwtSecurityToken("https://fixture.invalid", "notification-fixture", claims,
                now.AddMinutes(-1), now.AddMinutes(5),
                new SigningCredentials(new RsaSecurityKey(signingKey), SecurityAlgorithms.RsaSha256));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
            return client;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("Jwt:PublicKey", Convert.ToBase64String(Encoding.UTF8.GetBytes(signingKey.ExportSubjectPublicKeyInfoPem())));
            builder.UseSetting("Jwt:Issuer", "https://fixture.invalid");
            builder.UseSetting("Jwt:Audience", "notification-fixture");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                var values = new Dictionary<string, string?>
                {
                    ["Jwt:PublicKey"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(signingKey.ExportSubjectPublicKeyInfoPem())),
                    ["Jwt:Issuer"] = "https://fixture.invalid",
                    ["Jwt:Audience"] = "notification-fixture",
                    ["Brevo:ApiKey"] = "synthetic-fixture-only",
                    ["Brevo:MaxRetryAttempts"] = "1",
                    ["Brevo:RetryDelayMilliseconds"] = "0",
                };
                foreach (var channel in new[] { "Info", "Manufacturing", "NoReply", "Support" })
                {
                    values[$"Brevo:Senders:{channel}:Address"] = "sender@example.invalid";
                    values[$"Brevo:Senders:{channel}:DisplayName"] = "Synthetic fixture";
                }

                configuration.AddInMemoryCollection(values);
            });
            builder.ConfigureServices(services => services.AddHttpClient<IBrevoNotificationTransport, BrevoNotificationTransport>()
                .ConfigurePrimaryHttpMessageHandler(() => Provider));
        }
    }

    private sealed class ProviderSentinel(bool loseFirstResponse) : HttpMessageHandler
    {
        public int Submissions { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.brevo.com/v3/smtp/email", request.RequestUri!.AbsoluteUri);
            using var body = await JsonDocument.ParseAsync(await request.Content!.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            Assert.Equal(OperationId, body.RootElement.GetProperty("headers").GetProperty("idempotencyKey").GetString());
            Submissions++;
            if (loseFirstResponse && Submissions == 1)
            {
                throw new HttpRequestException("Controlled lost acknowledgment after possible provider acceptance.");
            }

            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = JsonContent.Create(new { messageId = "fixture-message-1" }),
            };
        }
    }
}
