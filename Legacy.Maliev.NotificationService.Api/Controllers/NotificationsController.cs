using Legacy.Maliev.NotificationService.Api.Authorization;
using Legacy.Maliev.NotificationService.Api.Models;
using Legacy.Maliev.NotificationService.Application.Interfaces;
using Legacy.Maliev.NotificationService.Domain;
using Maliev.Aspire.ServiceDefaults.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Legacy.Maliev.NotificationService.Api.Controllers;

/// <summary>Modern JSON API for authenticated transactional email delivery.</summary>
[ApiController]
[Route("notifications/v1/email")]
[Authorize]
public sealed class NotificationsController(INotificationService notificationService) : ControllerBase
{
    /// <summary>Sends one email through the selected configured sender channel.</summary>
    /// <param name="channel">Configured sender channel.</param>
    /// <param name="request">Recipient and message body.</param>
    /// <param name="cancellationToken">Request cancellation.</param>
    /// <returns>The provider message identifier when available.</returns>
    [HttpPost("{channel}")]
    [RequirePermission(NotificationPermissions.Send, RequireLiveCheck = true)]
    [ProducesResponseType<SendEmailNotificationResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult> SendEmailAsync(
        EmailChannel channel,
        [FromBody] SendEmailNotificationRequest request,
        CancellationToken cancellationToken)
    {
        var result = await notificationService.SendAsync(
            channel,
            new NotificationSendRequest
            {
                To = request.To,
                Subject = request.Subject,
                Body = request.Body,
                ReplyTo = request.ReplyTo,
                Cc = request.Cc,
                Bcc = request.Bcc,
            },
            cancellationToken);

        return StatusCode(
            (int)result.StatusCode,
            new SendEmailNotificationResponse(result.ProviderMessageId));
    }
}
