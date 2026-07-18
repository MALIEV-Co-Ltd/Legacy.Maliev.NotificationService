using System.Net;
using Legacy.Maliev.NotificationService.Application.Interfaces;
using Legacy.Maliev.NotificationService.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Legacy.Maliev.NotificationService.Data;

/// <summary>Sends legacy notifications through Brevo transactional email.</summary>
public sealed class BrevoNotificationProvider(
    IBrevoNotificationTransport transport,
    IOptions<BrevoNotificationOptions> options,
    TimeProvider timeProvider,
    ILogger<BrevoNotificationProvider> logger) : INotificationProvider
{
    private readonly BrevoNotificationOptions options = options.Value;

    /// <inheritdoc />
    public async Task<NotificationSendResult> SendAsync(
        EmailChannel channel,
        NotificationSendRequest request,
        CancellationToken cancellationToken)
    {
        if (!this.options.Senders.TryGetValue(channel, out var sender))
        {
            return new NotificationSendResult(HttpStatusCode.BadRequest);
        }

        var transportRequest = new BrevoTransportRequest(
            channel,
            sender,
            request,
            request.IdempotencyKey ?? Guid.NewGuid().ToString("D"));
        var startedAt = timeProvider.GetTimestamp();
        for (var attempt = 0; ; attempt++)
        {
            using var attemptTimeout = new CancellationTokenSource(
                TimeSpan.FromMilliseconds(this.options.AttemptTimeoutMilliseconds),
                timeProvider);
            using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                attemptTimeout.Token);
            try
            {
                var result = await transport.SendAsync(transportRequest, attemptCancellation.Token);
                logger.LogInformation(
                    "Brevo delivery completed for channel {Channel} after {AttemptCount} attempt(s) in {ElapsedMilliseconds} ms.",
                    channel,
                    attempt + 1,
                    timeProvider.GetElapsedTime(startedAt).TotalMilliseconds);
                return new NotificationSendResult(
                    result.MessageId is null ? HttpStatusCode.BadRequest : HttpStatusCode.OK,
                    result.MessageId);
            }
            catch (BrevoTransportException exception) when (
                attempt < this.options.MaxRetryAttempts && IsTransient(exception.StatusCode))
            {
                logger.LogWarning(
                    "Brevo transient failure for channel {Channel} with status {StatusCode}; retrying attempt {Attempt}.",
                    channel,
                    (int)exception.StatusCode,
                    attempt + 2);
                await DelayBeforeRetryAsync(
                    this.options,
                    exception.RetryAfter,
                    timeProvider,
                    cancellationToken);
            }
            catch (HttpRequestException) when (attempt < this.options.MaxRetryAttempts)
            {
                logger.LogWarning(
                    "Brevo transport failure for channel {Channel}; retrying attempt {Attempt}.",
                    channel,
                    attempt + 2);
                await DelayBeforeRetryAsync(this.options, null, timeProvider, cancellationToken);
            }
            catch (HttpRequestException)
            {
                throw new BrevoTransportException(HttpStatusCode.BadGateway);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (
                attemptTimeout.IsCancellationRequested && attempt < this.options.MaxRetryAttempts)
            {
                logger.LogWarning(
                    "Brevo attempt timed out for channel {Channel}; retrying attempt {Attempt}.",
                    channel,
                    attempt + 2);
                await DelayBeforeRetryAsync(this.options, null, timeProvider, cancellationToken);
            }
            catch (OperationCanceledException exception) when (attemptTimeout.IsCancellationRequested)
            {
                throw new TimeoutException("Brevo notification delivery timed out.", exception);
            }
        }
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
        (int)statusCode >= 500;

    private static Task DelayBeforeRetryAsync(
        BrevoNotificationOptions options,
        TimeSpan? providerDelay,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var configuredDelay = TimeSpan.FromMilliseconds(options.RetryDelayMilliseconds);
        var maximumDelay = TimeSpan.FromMilliseconds(options.MaxRetryDelayMilliseconds);
        var delay = providerDelay is { } requestedDelay && requestedDelay > configuredDelay
            ? requestedDelay
            : configuredDelay;
        if (delay > maximumDelay)
        {
            delay = maximumDelay;
        }

        return Task.Delay(
            delay,
            timeProvider,
            cancellationToken);
    }
}
