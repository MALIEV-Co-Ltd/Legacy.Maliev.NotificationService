using System.Collections.Concurrent;
using System.Net;
using Legacy.Maliev.NotificationService.Application.Interfaces;
using Legacy.Maliev.NotificationService.Domain;

namespace Legacy.Maliev.NotificationService.Data;

/// <summary>
/// Records delivery metadata in memory for explicitly enabled local development environments.
/// Message bodies, attachments and address-copy fields are deliberately never retained.
/// </summary>
public sealed class DevelopmentRecordingNotificationProvider : INotificationProvider
{
    private readonly ConcurrentQueue<DevelopmentRecordedNotification> notifications = new();
    private readonly TimeProvider timeProvider;

    /// <summary>Initializes a development recorder with the system clock or a supplied test clock.</summary>
    public DevelopmentRecordingNotificationProvider(TimeProvider? timeProvider = null)
    {
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Returns an immutable snapshot of recorded delivery metadata.</summary>
    public IReadOnlyList<DevelopmentRecordedNotification> Snapshot() => notifications.ToArray();

    /// <inheritdoc />
    public Task<NotificationSendResult> SendAsync(
        EmailChannel channel,
        NotificationSendRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var messageId = $"development-{Guid.NewGuid():N}";
        notifications.Enqueue(new DevelopmentRecordedNotification(
            messageId,
            channel,
            request.To,
            request.Subject,
            timeProvider.GetUtcNow()));
        return Task.FromResult(new NotificationSendResult(HttpStatusCode.OK, messageId));
    }
}

/// <summary>Non-secret delivery metadata exposed only by the explicitly enabled development recorder.</summary>
/// <param name="MessageId">Ephemeral local provider message identifier.</param>
/// <param name="Channel">Legacy sender channel.</param>
/// <param name="To">Local test recipient.</param>
/// <param name="Subject">Notification subject.</param>
/// <param name="RecordedAt">UTC recording time.</param>
public sealed record DevelopmentRecordedNotification(
    string MessageId,
    EmailChannel Channel,
    string To,
    string Subject,
    DateTimeOffset RecordedAt);
