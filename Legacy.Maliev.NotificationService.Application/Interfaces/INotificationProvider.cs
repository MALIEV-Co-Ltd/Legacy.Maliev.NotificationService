using Legacy.Maliev.NotificationService.Domain;

namespace Legacy.Maliev.NotificationService.Application.Interfaces;

/// <summary>Provider adapter for sending notifications.</summary>
public interface INotificationProvider
{
    /// <summary>Sends the notification through the concrete provider.</summary>
    Task<NotificationSendResult> SendAsync(
        EmailChannel channel,
        NotificationSendRequest request,
        CancellationToken cancellationToken);
}
