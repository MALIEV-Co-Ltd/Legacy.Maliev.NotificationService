using Legacy.Maliev.NotificationService.Application.Interfaces;
using Legacy.Maliev.NotificationService.Application.Services;
using Legacy.Maliev.NotificationService.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Legacy.Maliev.NotificationService.Data;

/// <summary>PostgreSQL authority; no provider effects run within this store or its retry strategy.</summary>
public sealed class PostgresDeliveryIntentStore(IDbContextFactory<DeliveryIntentDbContext> factory, TimeProvider timeProvider,
    NotificationIntentBinding bindingService) : IDeliveryIntentStore
{
    /// <inheritdoc />
    public Task<DeliveryIntentRecord> AdmitAsync(DeliveryIntentIdentity identity, string keyId, string binding, CancellationToken cancellationToken)
    {
        NotificationIntentBinding.ValidateIdentity(identity);
        if (!NotificationIntentBinding.IsKeyId(keyId) || !NotificationIntentBinding.IsDigest(binding))
            throw new ArgumentException("Invalid notification binding.");
        bindingService.EnsureAvailableKey(keyId);
        return TransactionAsync(async database =>
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;
            await database.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO public."NotificationDeliveryIntent"
                ("Issuer", "ServiceSubject", "IntentId", "Purpose", "ResourceType", "ResourceId", "WorkflowOperationId", "Channel", "BindingVersion", "KeyId", "Binding", "State", "Version", "AdmittedAt", "UpdatedAt")
                VALUES ({identity.Issuer}, {identity.ServiceSubject}, {identity.IntentId}, {identity.Purpose}, {identity.ResourceType}, {identity.ResourceId}, {identity.WorkflowOperationId}, {identity.Channel.ToString()}, {NotificationIntentBinding.BindingVersion}, {keyId}, {binding}, 'Admitted', 1, {now}, {now})
                ON CONFLICT DO NOTHING
                """, cancellationToken);
            var existing = await LockedAsync(database, identity, cancellationToken);
            if (existing is null)
            {
                var knownBusinessEffect = await database.Set<DeliveryIntentRow>().AsNoTracking().AnyAsync(value =>
                    value.Issuer == identity.Issuer && value.ServiceSubject == identity.ServiceSubject &&
                    value.Purpose == identity.Purpose && value.ResourceType == identity.ResourceType &&
                    value.ResourceId == identity.ResourceId && value.WorkflowOperationId == identity.WorkflowOperationId,
                    cancellationToken);
                if (knownBusinessEffect) throw new DeliveryIntentConflictException();
                throw new DeliveryIntentUnavailableException();
            }
            if (existing.Identity != identity || existing.KeyId != keyId ||
                existing.BindingVersion != NotificationIntentBinding.BindingVersion ||
                !NotificationIntentBinding.BindingEquals(existing.Binding, binding)) throw new DeliveryIntentConflictException();
            return existing;
        }, cancellationToken);
    }
    /// <inheritdoc />
    public Task<DeliveryIntentRecord?> ReadAsync(string issuer, string serviceSubject, Guid intentId, CancellationToken cancellationToken) =>
        TransactionAsync<DeliveryIntentRecord?>(async database =>
        {
            var row = await database.Set<DeliveryIntentRow>().AsNoTracking().SingleOrDefaultAsync(value =>
                value.Issuer == issuer && value.ServiceSubject == serviceSubject && value.IntentId == intentId, cancellationToken);
            return row?.Record();
        }, cancellationToken);
    /// <inheritdoc />
    public Task<DeliveryIntentRecord?> TryFenceAsync(DeliveryIntentRecord observed, CancellationToken cancellationToken) =>
        TransitionAsync(observed, DeliveryIntentState.Admitted, DeliveryIntentState.Submitting, null, cancellationToken);
    /// <inheritdoc />
    public Task<DeliveryIntentRecord?> TryAcceptAsync(DeliveryIntentRecord fence, string providerMessageId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(providerMessageId) || providerMessageId.Length > 256)
            throw new ArgumentException("Invalid provider acceptance identifier.");
        return TransitionAsync(fence, DeliveryIntentState.Submitting, DeliveryIntentState.ProviderAccepted, providerMessageId, cancellationToken);
    }
    /// <inheritdoc />
    public Task<DeliveryIntentRecord?> TryMarkUnknownAsync(DeliveryIntentRecord fence, CancellationToken cancellationToken) =>
        TransitionAsync(fence, DeliveryIntentState.Submitting, DeliveryIntentState.OutcomeUnknown, null, cancellationToken);

    private Task<DeliveryIntentRecord?> TransitionAsync(DeliveryIntentRecord observed, DeliveryIntentState expected,
        DeliveryIntentState target, string? messageId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(observed);
        NotificationIntentBinding.ValidateIdentity(observed.Identity);
        cancellationToken.ThrowIfCancellationRequested();
        if (observed.State != expected) return Task.FromResult<DeliveryIntentRecord?>(null);
        if (observed.Version is <= 0 or long.MaxValue || !NotificationIntentBinding.IsDigest(observed.Binding))
            throw new DeliveryIntentUnavailableException();
        return TransactionAsync<DeliveryIntentRecord?>(async database =>
        {
            var current = await LockedAsync(database, observed.Identity, cancellationToken);
            if (current != observed) return null;
            var now = timeProvider.GetUtcNow().UtcDateTime;
            if (now < current.UpdatedAt) now = current.UpdatedAt;
            var identity = observed.Identity;
            var changed = await database.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE public."NotificationDeliveryIntent" SET "State" = {target.ToString()}, "Version" = "Version" + 1,
                    "ProviderMessageId" = {messageId}, "UpdatedAt" = {now}
                WHERE "Issuer" = {identity.Issuer} AND "ServiceSubject" = {identity.ServiceSubject} AND "IntentId" = {identity.IntentId}
                    AND "Version" = {observed.Version} AND "State" = {expected.ToString()}
                """, cancellationToken);
            if (changed != 1) throw new DeliveryIntentUnavailableException();
            return await LockedAsync(database, identity, cancellationToken) ?? throw new DeliveryIntentUnavailableException();
        }, cancellationToken);
    }

    private static async Task<DeliveryIntentRecord?> LockedAsync(DeliveryIntentDbContext database,
        DeliveryIntentIdentity identity, CancellationToken cancellationToken)
    {
        var rows = await database.Set<DeliveryIntentRow>().FromSqlInterpolated($"""
            SELECT * FROM public."NotificationDeliveryIntent" WHERE "Issuer" = {identity.Issuer}
                AND "ServiceSubject" = {identity.ServiceSubject} AND "IntentId" = {identity.IntentId} FOR UPDATE
            """).AsNoTracking().ToListAsync(cancellationToken);
        return rows.SingleOrDefault()?.Record();
    }

    private async Task<T> TransactionAsync<T>(Func<DeliveryIntentDbContext, Task<T>> execute, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var attempted = false;
        var commitSubmitted = false;
        try
        {
            await using var strategyContext = await factory.CreateDbContextAsync(cancellationToken);
            return await strategyContext.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                if (attempted) throw new DeliveryIntentUnavailableException();
                attempted = true;
                try
                {
                    await using var database = await factory.CreateDbContextAsync(cancellationToken);
                    await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
                    await DeliveryIntentSchema.EnsureReadyAsync(database, bindingService, cancellationToken);
                    var result = await execute(database);
                    cancellationToken.ThrowIfCancellationRequested();
                    commitSubmitted = true;
                    await transaction.CommitAsync(cancellationToken);
                    // await-using disposal completes before this result leaves the guarded attempt.
                    return result;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && !commitSubmitted) { throw; }
                catch (PostgresException exception) when (!commitSubmitted && exception.SqlState == PostgresErrorCodes.UniqueViolation &&
                    exception.ConstraintName == "AK_NotificationIntent_BusinessEffect")
                { throw new DeliveryIntentConflictException(exception); }
                catch (DeliveryIntentConflictException) when (!commitSubmitted) { throw; }
                catch (DeliveryIntentUnavailableException) { throw; }
                catch (Exception exception) { throw new DeliveryIntentUnavailableException(exception); }
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && !commitSubmitted) { throw; }
        catch (DeliveryIntentConflictException) when (!commitSubmitted) { throw; }
        catch (DeliveryIntentUnavailableException) { throw; }
        catch (Exception exception) { throw new DeliveryIntentUnavailableException(exception); }
    }
}
