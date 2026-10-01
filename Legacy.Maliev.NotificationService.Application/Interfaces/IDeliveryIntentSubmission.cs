using Legacy.Maliev.NotificationService.Domain;

namespace Legacy.Maliev.NotificationService.Application.Interfaces;

/// <summary>Dedicated single provider attempt, never the legacy retry provider.</summary>
public interface IDeliveryIntentSubmission
{
    /// <summary>Submits once after a durable acknowledged fence; acceptance is not delivery.</summary>
    Task<string> SubmitAsync(DeliveryIntentRecord fence, NotificationSendRequest payload, CancellationToken cancellationToken);
}
