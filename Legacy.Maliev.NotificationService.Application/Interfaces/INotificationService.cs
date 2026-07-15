using Legacy.Maliev.NotificationService.Domain;

namespace Legacy.Maliev.NotificationService.Application.Interfaces;

/// <summary>Coordinates legacy notification send use cases.</summary>
public interface INotificationService
{
    /// <summary>Sends a legacy notification request through the configured provider.</summary>
    Task<NotificationSendResult> SendAsync(
        EmailChannel channel,
        NotificationSendRequest request,
        CancellationToken cancellationToken);
}
