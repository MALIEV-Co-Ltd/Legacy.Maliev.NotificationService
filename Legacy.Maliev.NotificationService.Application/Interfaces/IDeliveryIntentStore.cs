using Legacy.Maliev.NotificationService.Domain;

namespace Legacy.Maliev.NotificationService.Application.Interfaces;

/// <summary>Durable notification authority; caches cannot implement this contract.</summary>
public interface IDeliveryIntentStore
{
    /// <summary>Admits an immutable tuple, or returns its identical existing authority.</summary>
    Task<DeliveryIntentRecord> AdmitAsync(DeliveryIntentIdentity identity, string keyId, string binding, CancellationToken cancellationToken);

    /// <summary>Reads only within the authenticated service-owned key scope.</summary>
    Task<DeliveryIntentRecord?> ReadAsync(string issuer, string serviceSubject, Guid intentId, CancellationToken cancellationToken);

    /// <summary>Claims the sole provider attempt after acknowledged durable COMMIT and disposal.</summary>
    Task<DeliveryIntentRecord?> TryFenceAsync(DeliveryIntentRecord observed, CancellationToken cancellationToken);

    /// <summary>Records a provider acceptance under the exact submitting generation.</summary>
    Task<DeliveryIntentRecord?> TryAcceptAsync(DeliveryIntentRecord fence, string providerMessageId, CancellationToken cancellationToken);

    /// <summary>Preserves submitting uncertainty without overwriting accepted or terminal peers.</summary>
    Task<DeliveryIntentRecord?> TryMarkUnknownAsync(DeliveryIntentRecord fence, CancellationToken cancellationToken);
}

/// <summary>Immutable identity or fingerprint mismatch; contains no payload data.</summary>
public sealed class DeliveryIntentConflictException : Exception
{
    /// <summary>Creates a fixed-message collision.</summary>
    public DeliveryIntentConflictException(Exception? innerException = null) : base("The notification intent conflicts with existing authority.", innerException) { }
}

/// <summary>Authority is unavailable or acknowledgment is uncertain; never retry the provider.</summary>
public sealed class DeliveryIntentUnavailableException : Exception
{
    /// <summary>Retains the internal cause without exposing provider or database details.</summary>
    public DeliveryIntentUnavailableException(Exception? innerException = null)
        : base("Notification intent authority is unavailable or uncertain.", innerException) { }
}
