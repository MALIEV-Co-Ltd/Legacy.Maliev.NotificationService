using Legacy.Maliev.NotificationService.Application.Interfaces;
using Legacy.Maliev.NotificationService.Domain;

namespace Legacy.Maliev.NotificationService.Application.Services;

/// <summary>Admits the immutable payload binding without owning provider submission.</summary>
public sealed class DeliveryIntentAdmissionService(IDeliveryIntentStore store, NotificationIntentBinding binding)
{
    /// <summary>Admits a verified service scope and a producer-framed payload digest.</summary>
    public async Task<DeliveryIntentRecord> AdmitAsync(DeliveryIntentIdentity identity, string payloadVersion, string payloadDigest, CancellationToken cancellationToken)
    {
        NotificationIntentBinding.ValidateIdentity(identity);
        var existing = await store.ReadAsync(identity.Issuer, identity.ServiceSubject, identity.IntentId, cancellationToken);
        if (existing is not null) return Verify(existing, identity, payloadVersion, payloadDigest);
        var fingerprint = binding.ComputeBinding(identity, payloadVersion, payloadDigest, binding.ActiveKeyId);
        try
        {
            return await store.AdmitAsync(identity, binding.ActiveKeyId, fingerprint, cancellationToken);
        }
        catch (DeliveryIntentConflictException)
        {
            // A concurrent admission may have retained a different active key. Never rehash that row.
            existing = await store.ReadAsync(identity.Issuer, identity.ServiceSubject, identity.IntentId, cancellationToken);
            if (existing is null) throw;
            return Verify(existing, identity, payloadVersion, payloadDigest);
        }
    }

    private DeliveryIntentRecord Verify(DeliveryIntentRecord existing, DeliveryIntentIdentity identity, string payloadVersion, string payloadDigest)
    {
        if (existing.Identity != identity || existing.BindingVersion != NotificationIntentBinding.BindingVersion)
            throw new DeliveryIntentConflictException();
        var expected = binding.ComputeBinding(identity, payloadVersion, payloadDigest, existing.KeyId);
        if (!NotificationIntentBinding.BindingEquals(existing.Binding, expected)) throw new DeliveryIntentConflictException();
        return existing;
    }
}
