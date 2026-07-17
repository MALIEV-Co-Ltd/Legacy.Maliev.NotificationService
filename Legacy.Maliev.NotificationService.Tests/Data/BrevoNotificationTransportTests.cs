using System.Net;
using System.Text;
using Legacy.Maliev.NotificationService.Data;
using Legacy.Maliev.NotificationService.Domain;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Legacy.Maliev.NotificationService.Tests.Data;

public sealed class BrevoNotificationTransportTests
{
    [Fact]
    public async Task SendAsync_MapsTheCompleteCompatibilityRequestAndSecurityHeaders()
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent("{\"messageId\":\"provider-id\"}", Encoding.UTF8, "application/json"),
        });
        var transport = CreateTransport(handler);

        var result = await transport.SendAsync(
            new BrevoTransportRequest(
                EmailChannel.Manufacturing,
                new BrevoSenderOptions { Address = "manufacturing@example.com", DisplayName = "Manufacturing" },
                new NotificationSendRequest
                {
                    To = "customer@example.com",
                    Subject = "Subject",
                    Body = "<p>Body</p>",
                    ReplyTo = "reply@example.com",
                    Cc = ["copy@example.com"],
                    Bcc = ["blind@example.com"],
                    Attachments = [new NotificationAttachment("drawing.txt", "text/plain", [1, 2, 3])],
                },
                "stable-idempotency-key"),
            CancellationToken.None);

        Assert.Equal("provider-id", result.MessageId);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("https://api.brevo.com/v3/smtp/email", handler.RequestUri?.AbsoluteUri);
        Assert.Equal("test-api-key", handler.ApiKey);
        Assert.False(handler.HasLegacyIdempotencyHeader);
        Assert.Contains("\"sender\":{\"name\":\"Manufacturing\",\"email\":\"manufacturing@example.com\"}", handler.Body, StringComparison.Ordinal);
        Assert.Contains("\"to\":[{\"email\":\"customer@example.com\"}]", handler.Body, StringComparison.Ordinal);
        Assert.Contains("\"replyTo\":{\"email\":\"reply@example.com\"}", handler.Body, StringComparison.Ordinal);
        Assert.Contains("\"cc\":[{\"email\":\"copy@example.com\"}]", handler.Body, StringComparison.Ordinal);
        Assert.Contains("\"bcc\":[{\"email\":\"blind@example.com\"}]", handler.Body, StringComparison.Ordinal);
        Assert.Contains("\"attachment\":[{\"content\":\"AQID\",\"name\":\"drawing.txt\"}]", handler.Body, StringComparison.Ordinal);
        Assert.Contains("\"headers\":{\"idempotencyKey\":\"stable-idempotency-key\"}", handler.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendAsync_WhenBrevoReturnsFailure_ExposesOnlyStatusAndRetryDelay()
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(2));
        var transport = CreateTransport(new RecordingHandler(response));

        var exception = await Assert.ThrowsAsync<BrevoTransportException>(() => transport.SendAsync(
            new BrevoTransportRequest(
                EmailChannel.Info,
                new BrevoSenderOptions { Address = "info@example.com", DisplayName = "Info" },
                new NotificationSendRequest { To = "customer@example.com", Subject = "Subject", Body = "Body" },
                "idempotency-key"),
            CancellationToken.None));

        Assert.Equal(HttpStatusCode.TooManyRequests, exception.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(2), exception.RetryAfter);
        Assert.DoesNotContain("customer@example.com", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendAsync_WhenRetryAfterUsesDate_ComputesDelayFromInjectedClock()
    {
        var timeProvider = new FakeTimeProvider();
        var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
            timeProvider.GetUtcNow().AddSeconds(3));
        var transport = CreateTransport(new RecordingHandler(response), timeProvider);

        var exception = await Assert.ThrowsAsync<BrevoTransportException>(() => transport.SendAsync(
            new BrevoTransportRequest(
                EmailChannel.Info,
                new BrevoSenderOptions { Address = "info@example.com", DisplayName = "Info" },
                new NotificationSendRequest { To = "customer@example.com", Subject = "Subject", Body = "Body" },
                "idempotency-key"),
            CancellationToken.None));

        Assert.Equal(TimeSpan.FromSeconds(3), exception.RetryAfter);
    }

    private static BrevoNotificationTransport CreateTransport(
        HttpMessageHandler handler,
        TimeProvider? timeProvider = null)
    {
        return new BrevoNotificationTransport(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.brevo.com/v3/") },
            Options.Create(new BrevoNotificationOptions { ApiKey = "test-api-key" }),
            timeProvider ?? TimeProvider.System);
    }

    private sealed class RecordingHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }

        public Uri? RequestUri { get; private set; }

        public string? ApiKey { get; private set; }

        public bool HasLegacyIdempotencyHeader { get; private set; }

        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            this.Method = request.Method;
            this.RequestUri = request.RequestUri;
            this.ApiKey = Assert.Single(request.Headers.GetValues("api-key"));
            this.HasLegacyIdempotencyHeader = request.Headers.Contains("X-Sib-Idempotency-Key");
            this.Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return response;
        }
    }
}
