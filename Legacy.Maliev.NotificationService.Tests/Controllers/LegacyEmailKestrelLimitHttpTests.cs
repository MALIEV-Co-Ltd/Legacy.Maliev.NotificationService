using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Legacy.Maliev.NotificationService.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Legacy.Maliev.NotificationService.Tests.Controllers;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EmailKestrelCollection
{
    public const string Name = "Email real Kestrel body limit";
}

[Collection(EmailKestrelCollection.Name)]
public sealed class LegacyEmailKestrelLimitHttpTests
{
    private const long SourceRequestLimit = 100L * 1024 * 1024;

    [Fact]
    public async Task NormalProgram_UsesKestrelWithSourceRequestLimit()
    {
        await using var factory = new EmailFactory();
        using var client = factory.Client();
        Assert.Contains("Kestrel", factory.Services.GetRequiredService<IServer>().GetType().FullName!);
        Assert.Equal(SourceRequestLimit, factory.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value.Limits.MaxRequestBodySize);
        Assert.True(client.BaseAddress!.IsLoopback);
        Assert.Equal(0, factory.ProviderCalls);
    }

    [Theory]
    [InlineData(32L * 1024 * 1024)]
    [InlineData(SourceRequestLimit)]
    public async Task RawBody_AboveFormerDefaultThroughExactSourceLimit_ReachesActualProvider(long length)
    {
        await using var factory = new EmailFactory();
        using var client = factory.Client();
        using var stream = new GeneratedBodyStream(length);
        using var content = new StreamContent(stream);
        content.Headers.ContentLength = length;
        content.Headers.ContentType = new("text/plain");
        using var response = await client.PostAsync(Query("support-plaintext"), content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(length, stream.BytesRead);
        Assert.Equal(1, factory.ProviderCalls);
        Assert.Equal(length, factory.BodyLength);
        Assert.False(factory.HasAttachments);
    }

    [Fact]
    public async Task Multipart_AboveFormerDefault_PreservesExactAttachmentBytes()
    {
        const long length = 32L * 1024 * 1024;
        await using var factory = new EmailFactory();
        using var client = factory.Client();
        using var stream = new GeneratedBodyStream(length);
        using var form = new MultipartFormDataContent();
        var file = new StreamContent(stream);
        file.Headers.ContentLength = length;
        form.Add(file, "files", "synthetic.bin");
        using var response = await client.PostAsync(Query("support") + "&body=synthetic", form);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(length, stream.BytesRead);
        Assert.Equal(1, factory.ProviderCalls);
        Assert.True(factory.HasAttachments);
        Assert.Equal(length, factory.AttachmentLength);
    }

    [Fact]
    public async Task Multipart_DeclaredAboveSourceRequestLimit_RejectsWithoutProvider()
    {
        await using var factory = new EmailFactory();
        using var client = factory.Client();
        using var stream = new GeneratedBodyStream(SourceRequestLimit + 1);
        using var form = new MultipartFormDataContent();
        var file = new StreamContent(stream);
        file.Headers.ContentLength = SourceRequestLimit + 1;
        form.Add(file, "files", "oversized.bin");
        using var request = new HttpRequestMessage(HttpMethod.Post, Query("support") + "&body=synthetic") { Content = form };
        request.Headers.ExpectContinue = true;
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, factory.ProviderCalls);
    }

    private static string Query(string route) => "/Emails/" + route + "?to=recipient@example.invalid&subject=synthetic";

    private sealed class EmailFactory : WebApplicationFactory<NotificationProgram>
    {
        private readonly RSA signingKey = RSA.Create(2048);
        public int ProviderCalls { get; private set; }
        public long BodyLength { get; private set; }
        public long AttachmentLength { get; private set; }
        public bool HasAttachments { get; private set; }

        public HttpClient Client()
        {
            UseKestrel(0);
            var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            client.Timeout = TimeSpan.FromMinutes(2);
            var now = DateTime.UtcNow;
            var token = new JwtSecurityToken("https://email-kestrel.invalid", "email-kestrel",
                [new Claim("sub", "service:email-kestrel"), new Claim("identity_kind", "service"),
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
            builder.UseSetting("Jwt:Issuer", "https://email-kestrel.invalid");
            builder.UseSetting("Jwt:Audience", "email-kestrel");
            builder.UseSetting("Notifications:DeliveryIntentsEnabled", "false");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                var values = new Dictionary<string, string?>
                {
                    ["Brevo:ApiKey"] = "synthetic-kestrel-only",
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
            // Only the external Brevo HTTP boundary is controlled. Host limits/auth/controllers stay real.
            builder.ConfigureServices(services => services.AddHttpClient<IBrevoNotificationTransport, BrevoNotificationTransport>()
                .ConfigurePrimaryHttpMessageHandler(() => new ControlledHandler(async (request, token) =>
                {
                    Assert.Equal("https://api.brevo.com/v3/smtp/email", request.RequestUri!.AbsoluteUri);
                    Assert.Equal("synthetic-kestrel-only", Assert.Single(request.Headers.GetValues("api-key")));
                    using var payload = await JsonDocument.ParseAsync(await request.Content!.ReadAsStreamAsync(token), cancellationToken: token);
                    var body = payload.RootElement.GetProperty("htmlContent").GetString()!;
                    BodyLength = body.Length;
                    Assert.True(body == "synthetic" || body.All(c => c == 'x'));
                    HasAttachments = payload.RootElement.TryGetProperty("attachment", out var attachments);
                    if (HasAttachments)
                    {
                        var attachment = Assert.Single(attachments.EnumerateArray());
                        Assert.Equal("synthetic.bin", attachment.GetProperty("name").GetString());
                        var bytes = Convert.FromBase64String(attachment.GetProperty("content").GetString()!);
                        AttachmentLength = bytes.LongLength;
                        Assert.True(bytes.All(b => b == (byte)'x'));
                    }
                    ProviderCalls++;
                    return new HttpResponseMessage(HttpStatusCode.Created)
                    { Content = JsonContent.Create(new { messageId = "controlled-kestrel-ack" }) };
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

    private sealed class GeneratedBodyStream(long length) : Stream
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
            buffer[..count].Fill((byte)'x');
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
}
