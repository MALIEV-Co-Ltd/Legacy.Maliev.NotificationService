using System.Net;

namespace Legacy.Maliev.NotificationService.Domain;

/// <summary>Provider-independent result mapped back to the legacy HTTP status response.</summary>
/// <param name="StatusCode">HTTP-compatible status code returned to the legacy caller.</param>
/// <param name="ProviderMessageId">Provider message identifier, when available.</param>
public sealed record NotificationSendResult(HttpStatusCode StatusCode, string? ProviderMessageId = null);
