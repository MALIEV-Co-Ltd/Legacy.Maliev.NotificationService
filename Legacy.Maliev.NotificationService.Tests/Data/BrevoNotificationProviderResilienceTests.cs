using System.Net;
using Legacy.Maliev.NotificationService.Data;
using Legacy.Maliev.NotificationService.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Legacy.Maliev.NotificationService.Tests.Data;

public sealed class BrevoNotificationProviderResilienceTests
{
    [Fact]
    public async Task SendAsync_WhenFirstAttemptIsTransient_RetriesWithSameIdempotencyKey()
    {
        var transport = new ScriptedBrevoTransport(
            new BrevoTransportException(HttpStatusCode.ServiceUnavailable),
            new BrevoTransportResult("message-id"));
        var provider = CreateProvider(transport, maxRetryAttempts: 1);

        var result = await provider.SendAsync(
            EmailChannel.Info,
            CreateRequest(),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal("message-id", result.ProviderMessageId);
        Assert.Equal(2, transport.Requests.Count);
        Assert.False(string.IsNullOrWhiteSpace(transport.Requests[0].IdempotencyKey));
        Assert.Equal(transport.Requests[0].IdempotencyKey, transport.Requests[1].IdempotencyKey);
    }

    [Fact]
    public async Task SendAsync_WhenTransportFails_RetriesTheBoundedNumberOfTimes()
    {
        var transport = new ScriptedBrevoTransport(
            new HttpRequestException("connection reset"),
            new HttpRequestException("connection reset"));
        var provider = CreateProvider(transport, maxRetryAttempts: 1);

        var exception = await Assert.ThrowsAsync<BrevoTransportException>(() => provider.SendAsync(
            EmailChannel.Info,
            CreateRequest(),
            CancellationToken.None));

        Assert.Equal(HttpStatusCode.BadGateway, exception.StatusCode);
        Assert.Equal(2, transport.Requests.Count);
    }

    [Fact]
    public async Task SendAsync_WhenProviderRejectsRequest_DoesNotRetry()
    {
        var transport = new ScriptedBrevoTransport(
            new BrevoTransportException(HttpStatusCode.BadRequest));
        var provider = CreateProvider(transport, maxRetryAttempts: 2);

        await Assert.ThrowsAsync<BrevoTransportException>(() => provider.SendAsync(
            EmailChannel.Info,
            CreateRequest(),
            CancellationToken.None));

        Assert.Single(transport.Requests);
    }

    [Fact]
    public async Task SendAsync_WhenProviderRequestsRetryDelay_HonorsItWithinTheConfiguredBound()
    {
        var timeProvider = new FakeTimeProvider();
        var transport = new ScriptedBrevoTransport(
            new BrevoTransportException(HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(2)),
            new BrevoTransportResult("message-id"));
        var provider = CreateProvider(transport, maxRetryAttempts: 1, timeProvider);

        var sendTask = provider.SendAsync(EmailChannel.Info, CreateRequest(), CancellationToken.None);
        await WaitUntilAsync(() => transport.Requests.Count == 1);
        timeProvider.Advance(TimeSpan.FromMilliseconds(1999));
        Assert.False(sendTask.IsCompleted);
        timeProvider.Advance(TimeSpan.FromMilliseconds(1));

        var result = await sendTask;
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal(2, transport.Requests.Count);
    }

    [Fact]
    public async Task SendAsync_WhenCallerCancels_PropagatesCancellationWithoutRetry()
    {
        var transport = new BlockingBrevoTransport();
        var provider = CreateProvider(transport, maxRetryAttempts: 2);
        using var cancellation = new CancellationTokenSource();

        var sendTask = provider.SendAsync(EmailChannel.Info, CreateRequest(), cancellation.Token);
        await WaitUntilAsync(() => transport.Attempts == 1);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sendTask);
        Assert.Equal(1, transport.Attempts);
    }

    [Fact]
    public async Task SendAsync_WhenAttemptExceedsTimeout_ThrowsTimeoutWithoutWaitingForTransport()
    {
        var transport = new BlockingBrevoTransport();
        var provider = CreateProvider(
            transport,
            maxRetryAttempts: 0,
            attemptTimeoutMilliseconds: 50);

        var sendTask = provider.SendAsync(
            EmailChannel.Info,
            CreateRequest(),
            CancellationToken.None);
        await Assert.ThrowsAsync<TimeoutException>(() => sendTask);
        Assert.Equal(1, transport.Attempts);
    }

    private static BrevoNotificationProvider CreateProvider(
        IBrevoNotificationTransport transport,
        int maxRetryAttempts,
        TimeProvider? timeProvider = null,
        int attemptTimeoutMilliseconds = 5000)
    {
        return new BrevoNotificationProvider(
            transport,
            Options.Create(new BrevoNotificationOptions
            {
                ApiKey = "test-key",
                MaxRetryAttempts = maxRetryAttempts,
                RetryDelayMilliseconds = 0,
                MaxRetryDelayMilliseconds = 5000,
                AttemptTimeoutMilliseconds = attemptTimeoutMilliseconds,
                Senders =
                {
                    [EmailChannel.Info] = new BrevoSenderOptions
                    {
                        Address = "info@example.com",
                        DisplayName = "Info",
                    },
                },
            }),
            timeProvider ?? TimeProvider.System,
            NullLogger<BrevoNotificationProvider>.Instance);
    }

    private static NotificationSendRequest CreateRequest()
    {
        return new NotificationSendRequest
        {
            To = "customer@example.com",
            Subject = "Subject",
            Body = "Body",
        };
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++)
        {
            await Task.Yield();
        }

        Assert.True(condition(), "The asynchronous operation did not reach the expected state.");
    }

    private sealed class ScriptedBrevoTransport(params object[] outcomes) : IBrevoNotificationTransport
    {
        private readonly Queue<object> outcomes = new(outcomes);

        public List<BrevoTransportRequest> Requests { get; } = [];

        public Task<BrevoTransportResult> SendAsync(
            BrevoTransportRequest request,
            CancellationToken cancellationToken)
        {
            this.Requests.Add(request);
            var outcome = this.outcomes.Dequeue();
            return outcome switch
            {
                Exception exception => Task.FromException<BrevoTransportResult>(exception),
                BrevoTransportResult result => Task.FromResult(result),
                _ => throw new InvalidOperationException("Unsupported scripted outcome."),
            };
        }
    }

    private sealed class BlockingBrevoTransport : IBrevoNotificationTransport
    {
        public int Attempts { get; private set; }

        public async Task<BrevoTransportResult> SendAsync(
            BrevoTransportRequest request,
            CancellationToken cancellationToken)
        {
            this.Attempts++;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The blocking transport must be cancelled.");
        }
    }
}
