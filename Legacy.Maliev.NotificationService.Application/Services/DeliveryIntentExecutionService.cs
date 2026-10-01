using Legacy.Maliev.NotificationService.Application.Interfaces;
using Legacy.Maliev.NotificationService.Domain;
using Microsoft.Extensions.Logging;

namespace Legacy.Maliev.NotificationService.Application.Services;

/// <summary>Owns one acknowledged durable attempt, with conservative result readback.</summary>
public sealed class DeliveryIntentExecutionService(IDeliveryIntentStore store, NotificationIntentBinding binding,
    IDeliveryIntentSubmission submission, ILogger<DeliveryIntentExecutionService> logger)
{
    /// <summary>Verifies the retained immutable binding before claiming any provider attempt.</summary>
    public async Task<DeliveryIntentRecord> ExecuteAsync(DeliveryIntentRecord observed, NotificationSendRequest payload, CancellationToken token)
    {
        var digest = binding.PayloadDigest(observed.Identity.Channel, payload);
        var expected = binding.ComputeBinding(observed.Identity, NotificationIntentBinding.PayloadVersion, digest, observed.KeyId);
        if (!NotificationIntentBinding.BindingEquals(expected, observed.Binding)) throw new DeliveryIntentConflictException();
        if (observed.State != DeliveryIntentState.Admitted) return observed;
        var fence = await store.TryFenceAsync(observed, token);
        if (fence is null) return await ReadAsync(observed, token);
        string messageId;
        try
        {
            token.ThrowIfCancellationRequested();
            messageId = await submission.SubmitAsync(fence, payload, token);
        }
        catch (Exception)
        {
            logger.LogWarning("Notification intent result is uncertain; category {Category}.", "provider-submission");
            using var checkpoint = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { return await store.TryMarkUnknownAsync(fence, checkpoint.Token) ?? await ReadAsync(fence, checkpoint.Token); }
            catch (DeliveryIntentUnavailableException) { return fence; }
            catch (OperationCanceledException) when (checkpoint.IsCancellationRequested) { return fence; }
        }

        // Acceptance acknowledgment is separate from provider acknowledgment; never downgrade it.
        using var completion = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return await store.TryAcceptAsync(fence, messageId, completion.Token) ?? await ReadAsync(fence, completion.Token);
    }

    private async Task<DeliveryIntentRecord> ReadAsync(DeliveryIntentRecord observed, CancellationToken token) =>
        await store.ReadAsync(observed.Identity.Issuer, observed.Identity.ServiceSubject, observed.Identity.IntentId, token)
        ?? throw new DeliveryIntentUnavailableException();
}
