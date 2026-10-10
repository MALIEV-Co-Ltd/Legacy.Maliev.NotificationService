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

public sealed class LegacyEncodedAttachmentNameHttpTests
{
    private const string EncodedName = "=?utf-8?Q?=E0=B8=87=E0=B8=B2=E0=B8=99_drawing.txt?=";
    private const string DecodedName = "งาน drawing.txt";
    private static readonly string SyntheticProviderKey = new('x', 32);
    private static readonly byte[] FileBytes = [0, 255, 1, 13, 10, 42];

    public static IEnumerable<object[]> LegacyCases()
    {
        foreach (var channel in new[] { "Info", "Manufacturing", "NoReply", "Support" })
        {
            foreach (var format in new[] { "Q", "B", "plain", "star" })
            {
                yield return [channel, format];
            }
        }
    }

    [Theory]
    [MemberData(nameof(LegacyCases))]
    public async Task LegacyMultipart_NameMatchesOriginalAttachmentAndRetainsExactBytes(string channel, string format)
    {
        using var factory = new AttachmentNameFactory();
        using var client = factory.Client();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        if (format == "Q")
        {
            using var originalName = new System.Net.Mail.Attachment(Stream.Null, EncodedName);
            Assert.Equal(DecodedName, originalName.Name);
        }

        var expected = format == "plain" ? "drawing one.txt" : DecodedName;
        var filename = format switch
        {
            "Q" => "filename=\"" + EncodedName + "\"",
            "B" => "filename=\"=?utf-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(DecodedName)) + "?=\"",
            "plain" => "filename=\"drawing one.txt\"",
            "star" => "filename=\"fallback.txt\"; filename*=utf-8''" + Uri.EscapeDataString(DecodedName),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
        // Raw headers prevent a client helper from decoding Q before the actual MVC multipart binder sees it.
        using var bytes = new MemoryStream();
        bytes.Write(Encoding.ASCII.GetBytes("--literal-boundary\r\nContent-Disposition: form-data; name=\"files\"; " + filename + "\r\nContent-Type: application/octet-stream\r\n\r\n"));
        bytes.Write(FileBytes);
        bytes.Write(Encoding.ASCII.GetBytes("\r\n--literal-boundary--\r\n"));
        using var content = new ByteArrayContent(bytes.ToArray());
        content.Headers.ContentType = new("multipart/form-data");
        content.Headers.ContentType.Parameters.Add(new("boundary", "literal-boundary"));
        using var response = await client.PostAsync("/Emails/" + channel + "?to=recipient%40example.invalid&subject=Synthetic&body=Body", content, deadline.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync(deadline.Token));
        VerifyPayload(Assert.Single(factory.Payloads), channel, expected);
    }

    [Theory]
    [InlineData("Info")]
    [InlineData("Manufacturing")]
    [InlineData("NoReply")]
    [InlineData("Support")]
    public async Task ModernJson_FilenameRemainsLiteralWithoutLegacyMimeDecoding(string channel)
    {
        using var factory = new AttachmentNameFactory();
        using var client = factory.Client();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var response = await client.PostAsJsonAsync("/notifications/v1/email/" + channel, new
        {
            to = "recipient@example.invalid",
            subject = "Synthetic",
            body = "Body",
            attachments = new[] { new { fileName = EncodedName, contentType = "application/octet-stream", content = FileBytes } },
        }, deadline.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        VerifyPayload(Assert.Single(factory.Payloads), channel, EncodedName);
    }

    private static void VerifyPayload(JsonElement payload, string channel, string expectedName)
    {
        Assert.Equal(channel + "@example.invalid", payload.GetProperty("sender").GetProperty("email").GetString());
        Assert.Equal("recipient@example.invalid", payload.GetProperty("to")[0].GetProperty("email").GetString());
        Assert.Equal("Synthetic", payload.GetProperty("subject").GetString());
        Assert.Equal("Body", payload.GetProperty("htmlContent").GetString());
        var attachment = Assert.Single(payload.GetProperty("attachment").EnumerateArray());
        Assert.Equal(expectedName, attachment.GetProperty("name").GetString());
        Assert.Equal(FileBytes, Convert.FromBase64String(attachment.GetProperty("content").GetString()!));
        Assert.False(payload.TryGetProperty("cc", out _));
        Assert.False(payload.TryGetProperty("bcc", out _));
        Assert.False(payload.TryGetProperty("replyTo", out _));
        Assert.True(Guid.TryParse(payload.GetProperty("headers").GetProperty("idempotencyKey").GetString(), out _));
    }

    private sealed class AttachmentNameFactory : WebApplicationFactory<NotificationProgram>
    {
        private readonly RSA signingKey = RSA.Create(2048);
        public List<JsonElement> Payloads { get; } = [];

        public HttpClient Client()
        {
            var client = CreateClient();
            var now = DateTime.UtcNow;
            var jwt = new JwtSecurityToken("https://issuer.example.invalid", "https://notification.example.invalid",
                [new Claim("sub", "synthetic-web"), new Claim("permissions", NotificationPermissions.Send)],
                now.AddMinutes(-1), now.AddMinutes(5), new SigningCredentials(new RsaSecurityKey(signingKey), SecurityAlgorithms.RsaSha256));
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
