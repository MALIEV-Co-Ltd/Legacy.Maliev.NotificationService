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
using Microsoft.IdentityModel.Tokens;

namespace Legacy.Maliev.NotificationService.Tests.Controllers;

// Real TestServer request streams and form binding; no Kestrel/edge capacity claim.
// No request-size or form limits are raised. Only the external provider handler is controlled.
public sealed class LegacyEmailAttachmentLimitHttpTests
{
    private const long FinalSourceLimit = 100L * 1024 * 1024;

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task StreamedMultipart_AggregateBoundaryRejectsBeforeProviderOrPreservesExactBytes(int delta)
    {
        await using var factory = new EmailFactory();
        using var client = factory.Client();
        using var form = new MultipartFormDataContent();
        var lengths = new[] { FinalSourceLimit / 2, FinalSourceLimit / 2 + delta };
        var streams = lengths.Select(length => new GeneratedZeroStream(length)).ToArray();
        form.Add(new StreamContent(streams[0]), "files", "first.bin");
        form.Add(new StreamContent(streams[1]), "files", "second.bin");
        using var response = await client.PostAsync(Query("support", true), form);

        Assert.Equal(lengths, streams.Select(stream => stream.BytesRead));
        if (delta > 0)
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var errors = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("Combined file size is too large.", errors.GetProperty("files")[0].GetString());
            Assert.Equal(0, factory.ProviderCalls);
            Assert.Null(factory.Wire);
            return;
        }

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(1, factory.ProviderCalls);
        var wire = Assert.IsType<BoundedWireVerifier>(factory.Wire);
        wire.AssertZeroAttachments(lengths);
        var payload = wire.Payload();
        AssertEnvelope(payload, "Support", "ข้อความ synthetic");
        Assert.Equal(new[] { "first.bin", "second.bin" },
            payload.GetProperty("attachment").EnumerateArray().Select(item => item.GetProperty("name").GetString()));
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("text/html")]
    public async Task RawUtf8Plaintext_RetainsBodyAndAcknowledgmentWithoutAttachments(string mediaType)
    {
        await using var factory = new EmailFactory();
        using var client = factory.Client();
        const string body = "เรียนลูกค้า\nsynthetic <body> & + %\n";
        using var content = new StringContent(body, Encoding.UTF8, mediaType);
        using var response = await client.PostAsync(Query("support-plaintext", false), content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(1, factory.ProviderCalls);
        var wire = Assert.IsType<BoundedWireVerifier>(factory.Wire);
        wire.AssertZeroAttachments([]);
        var payload = wire.Payload();
        AssertEnvelope(payload, "Support", body);
        Assert.False(payload.TryGetProperty("attachment", out _));
    }

    [Fact]
    public async Task MultipartPlaintext_DoesNotReinterpretFormFieldsAsRawBody()
    {
        await using var factory = new EmailFactory();
        using var client = factory.Client();
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("synthetic form body"), "body");
        form.Add(new StreamContent(new GeneratedZeroStream(3)), "files", "fixture.bin");
        using var response = await client.PostAsync(Query("support-plaintext", false), form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Email body is required.", errors.GetProperty("body")[0].GetString());
        Assert.Equal(0, factory.ProviderCalls);
    }

    private static string Query(string route, bool includeBody) => "/Emails/" + route
        + "?to=recipient@example.invalid&subject=synthetic&replyTo=reply@example.invalid"
        + "&cc=cc2@example.invalid&cc=cc1@example.invalid&bcc=bcc@example.invalid"
        + (includeBody ? "&body=" + Uri.EscapeDataString("ข้อความ synthetic") : "");

    private static void AssertEnvelope(JsonElement payload, string sender, string body)
    {
        Assert.Equal(sender + "@example.invalid", payload.GetProperty("sender").GetProperty("email").GetString());
        Assert.Equal("recipient@example.invalid", payload.GetProperty("to")[0].GetProperty("email").GetString());
        Assert.Equal("synthetic", payload.GetProperty("subject").GetString());
        Assert.Equal(body, payload.GetProperty("htmlContent").GetString());
        Assert.Equal("reply@example.invalid", payload.GetProperty("replyTo").GetProperty("email").GetString());
        Assert.Equal(new[] { "cc2@example.invalid", "cc1@example.invalid" },
            payload.GetProperty("cc").EnumerateArray().Select(item => item.GetProperty("email").GetString()));
        Assert.Equal("bcc@example.invalid", payload.GetProperty("bcc")[0].GetProperty("email").GetString());
        Assert.True(Guid.TryParse(payload.GetProperty("headers").GetProperty("idempotencyKey").GetString(), out _));
    }

    private sealed class EmailFactory : WebApplicationFactory<NotificationProgram>
    {
        private readonly RSA signingKey = RSA.Create(2048);
        public int ProviderCalls { get; private set; }
        public BoundedWireVerifier? Wire { get; private set; }

        public HttpClient Client()
        {
            var client = CreateClient();
            client.Timeout = TimeSpan.FromMinutes(2);
            var now = DateTime.UtcNow;
            var token = new JwtSecurityToken("https://email-limit.invalid", "email-limit",
                [new Claim("sub", "service:email-limit"), new Claim("identity_kind", "service"),
                    new Claim("permissions", "legacy.notifications.send")],
                now.AddMinutes(-1), now.AddMinutes(5),
                new SigningCredentials(new RsaSecurityKey(signingKey), SecurityAlgorithms.RsaSha256));
            client.DefaultRequestHeaders.Authorization = new("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
            return client;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("Jwt:PublicKey", Convert.ToBase64String(Encoding.UTF8.GetBytes(signingKey.ExportSubjectPublicKeyInfoPem())));
            builder.UseSetting("Jwt:Issuer", "https://email-limit.invalid");
            builder.UseSetting("Jwt:Audience", "email-limit");
            builder.UseSetting("Notifications:DeliveryIntentsEnabled", "false");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                var values = new Dictionary<string, string?>
                {
                    ["Brevo:ApiKey"] = "synthetic-email-limit-only",
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
            builder.ConfigureServices(services => services.AddHttpClient<IBrevoNotificationTransport, BrevoNotificationTransport>()
                .ConfigurePrimaryHttpMessageHandler(() => new ControlledHandler(async (request, token) =>
                {
                    Assert.Equal("https://api.brevo.com/v3/smtp/email", request.RequestUri!.AbsoluteUri);
                    Assert.Equal("synthetic-email-limit-only", Assert.Single(request.Headers.GetValues("api-key")));
                    ProviderCalls++;
                    Wire = new BoundedWireVerifier();
                    await request.Content!.CopyToAsync(Wire, token);
                    return new HttpResponseMessage(HttpStatusCode.Created)
                    { Content = JsonContent.Create(new { messageId = "controlled-limit-ack" }) };
                })));
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

    // Emits actual bytes in caller-sized chunks, without allocating an attachment-sized client buffer.
    private sealed class GeneratedZeroStream(long length) : Stream
    {
        public long BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            var count = (int)Math.Min(buffer.Length, length - BytesRead);
            buffer[..count].Clear();
            BytesRead += count;
            return count;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // Checks every serialized base64 byte and padding, retaining only the bounded JSON envelope.
    private sealed class BoundedWireVerifier : Stream
    {
        private readonly MemoryStream envelope = new();
        private readonly List<(long Letters, int Padding)> attachments = [];
        private readonly StringBuilder token = new();
        private string lastString = "";
        private bool quoted, escaped, nextContent, content;
        private long letters;
        private int padding;
        public JsonElement Payload()
        {
            Assert.False(quoted);
            using var document = JsonDocument.Parse(envelope.ToArray());
            return document.RootElement.Clone();
        }
        public void AssertZeroAttachments(long[] lengths)
        {
            Assert.Equal(lengths.Length, attachments.Count);
            for (var index = 0; index < lengths.Length; index++)
            {
                var expectedPadding = (int)((3 - lengths[index] % 3) % 3);
                Assert.Equal(expectedPadding, attachments[index].Padding);
                Assert.Equal((lengths[index] + 2) / 3 * 4 - expectedPadding, attachments[index].Letters);
            }
        }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            foreach (var value in buffer)
            {
                if (quoted && content && value != (byte)'"')
                {
                    if (value == (byte)'A' && padding == 0) letters++;
                    else if (value == (byte)'=' && padding < 2) padding++;
                    else throw new InvalidOperationException("Unexpected base64 byte or padding order.");
                    continue;
                }
                if (envelope.Length >= 4096) throw new InvalidOperationException("Provider envelope exceeded bounded fixture capacity.");
                envelope.WriteByte(value);
                if (quoted)
                {
                    if (escaped) { escaped = false; token.Append((char)value); }
                    else if (value == (byte)'\\') { escaped = true; token.Append((char)value); }
                    else if (value == (byte)'"')
                    {
                        quoted = false;
                        if (content) attachments.Add((letters, padding));
                        lastString = token.ToString();
                    }
                    else token.Append((char)value);
                }
                else if (value == (byte)':') nextContent = lastString == "content";
                else if (value == (byte)'"')
                {
                    quoted = true;
                    content = nextContent;
                    nextContent = false;
                    token.Clear();
                    letters = 0;
                    padding = 0;
                }
            }
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) envelope.Dispose();
            base.Dispose(disposing);
        }
    }
}
