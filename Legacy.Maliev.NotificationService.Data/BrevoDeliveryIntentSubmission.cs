using Legacy.Maliev.NotificationService.Application.Interfaces;
using Legacy.Maliev.NotificationService.Domain;
using Microsoft.Extensions.Options;

namespace Legacy.Maliev.NotificationService.Data;

/// <summary>Single-attempt V2 adapter, separate from the unchanged V1 retry provider.</summary>
public sealed class BrevoDeliveryIntentSubmission(HttpClient client, IOptions<BrevoNotificationOptions> options,
    TimeProvider timeProvider) : IDeliveryIntentSubmission
{
    /// <summary>Dedicated retry-free HTTP client identity.</summary>
    public const string ClientName = "NotificationIntentSingleAttempt";

    /// <inheritdoc />
    public async Task<string> SubmitAsync(DeliveryIntentRecord fence, NotificationSendRequest payload, CancellationToken token)
    {
        if (fence.State != DeliveryIntentState.Submitting || !options.Value.Senders.TryGetValue(fence.Identity.Channel, out var sender))
            throw new DeliveryIntentUnavailableException();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(options.Value.AttemptTimeoutMilliseconds), timeProvider);
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        var result = await new BrevoNotificationTransport(client, options, timeProvider).SendAsync(
            new(fence.Identity.Channel, sender, payload, fence.Identity.IntentId.ToString("D")), attempt.Token);
        if (string.IsNullOrWhiteSpace(result.MessageId) || result.MessageId.Length > 256)
            throw new DeliveryIntentUnavailableException();
        return result.MessageId;
    }
}
