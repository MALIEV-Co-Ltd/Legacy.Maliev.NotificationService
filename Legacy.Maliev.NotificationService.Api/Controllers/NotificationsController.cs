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
    private const long AttachmentSizeLimit = 200L * 1024L * 1024L;

    /// <summary>Sends one email through the selected configured sender channel.</summary>
    /// <param name="channel">Configured sender channel.</param>
    /// <param name="request">Recipient and message body.</param>
    /// <param name="idempotencyKey">Optional stable UUID for one logical delivery operation.</param>
    /// <param name="cancellationToken">Request cancellation.</param>
    /// <returns>The provider message identifier when available.</returns>
    [HttpPost("{channel}")]
    [RequirePermission(NotificationPermissions.Send)]
    [ProducesResponseType<SendEmailNotificationResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult> SendEmailAsync(
        EmailChannel channel,
        [FromBody] SendEmailNotificationRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(idempotencyKey) &&
            !Guid.TryParseExact(idempotencyKey, "D", out _))
        {
            return this.BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid idempotency key",
                Detail = "Idempotency-Key must be a UUID in canonical D format.",
            });
        }

        if (request.Attachments?.Any(attachment =>
                attachment is null ||
                string.IsNullOrWhiteSpace(attachment.FileName) ||
                attachment.Content is null ||
                attachment.Content.Length == 0) == true ||
            request.Attachments?.Sum(attachment => (long)attachment.Content.Length) > AttachmentSizeLimit)
        {
            return this.BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid attachment",
                Detail = "Attachments must be non-empty and may not exceed 200 MiB combined.",
            });
        }

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
                Attachments = request.Attachments?.Select(attachment => new NotificationAttachment(
                    attachment.FileName,
                    attachment.ContentType,
                    attachment.Content)).ToArray(),
                IdempotencyKey = idempotencyKey,
            },
            cancellationToken);

        return StatusCode(
            (int)result.StatusCode,
            new SendEmailNotificationResponse(result.ProviderMessageId));
    }
}
