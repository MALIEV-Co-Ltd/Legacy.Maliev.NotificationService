using System.Net;
using Legacy.Maliev.NotificationService.Domain;

namespace Legacy.Maliev.NotificationService.Data;

/// <summary>Sends one fully mapped notification through the Brevo transport boundary.</summary>
public interface IBrevoNotificationTransport
{
    /// <summary>Sends one request.</summary>
    Task<BrevoTransportResult> SendAsync(
        BrevoTransportRequest request,
        CancellationToken cancellationToken);
}

/// <summary>Provider-ready Brevo request with a stable idempotency key.</summary>
public sealed record BrevoTransportRequest(
    EmailChannel Channel,
    BrevoSenderOptions Sender,
    NotificationSendRequest Notification,
    string IdempotencyKey);

/// <summary>Successful Brevo transport response.</summary>
public sealed record BrevoTransportResult(string? MessageId);

/// <summary>Represents a non-success response returned by Brevo.</summary>
public sealed class BrevoTransportException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="BrevoTransportException"/> class.</summary>
    public BrevoTransportException(HttpStatusCode statusCode, TimeSpan? retryAfter = null)
        : base($"Brevo returned HTTP {(int)statusCode}.")
    {
        this.StatusCode = statusCode;
        this.RetryAfter = retryAfter;
    }

    /// <summary>Gets the provider status code.</summary>
    public HttpStatusCode StatusCode { get; }

    /// <summary>Gets the provider-requested delay before another attempt.</summary>
    public TimeSpan? RetryAfter { get; }
}
