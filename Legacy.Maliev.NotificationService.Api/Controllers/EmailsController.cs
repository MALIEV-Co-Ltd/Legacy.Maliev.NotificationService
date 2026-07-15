using Legacy.Maliev.NotificationService.Application.Interfaces;
using Legacy.Maliev.NotificationService.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Legacy.Maliev.NotificationService.Api.Controllers;

/// <summary>Preserves the legacy EmailService HTTP contract during migration.</summary>
[ApiController]
[Route("Emails")]
[Authorize]
public sealed class EmailsController(INotificationService notificationService) : ControllerBase
{
    /// <summary>Legacy combined attachment size limit, preserved from UploadService.Common.FileUpload.</summary>
    public const long SizeLimit = 200L * 1024L * 1024L;

    /// <summary>Sends an informational email.</summary>
    [HttpPost("info")]
    public Task<ActionResult> SendInfoEmailAsync(
        [FromQuery] string? to,
        [FromQuery] string? subject,
        [FromQuery] string? body,
        [FromQuery] string? replyTo,
        [FromQuery] List<string>? cc,
        [FromQuery] List<string>? bcc,
        [FromForm] List<IFormFile>? files = null,
        CancellationToken cancellationToken = default)
    {
        return this.SendEmailCoreAsync(EmailChannel.Info, to, subject, body, replyTo, cc, bcc, files, cancellationToken);
    }

    /// <summary>Sends a manufacturing-related email.</summary>
    [HttpPost("manufacturing")]
    public Task<ActionResult> SendManufacturingEmailAsync(
        [FromQuery] string? to,
        [FromQuery] string? subject,
        [FromQuery] string? body,
        [FromQuery] string? replyTo,
        [FromQuery] List<string>? cc,
        [FromQuery] List<string>? bcc,
        [FromForm] List<IFormFile>? files = null,
        CancellationToken cancellationToken = default)
    {
        return this.SendEmailCoreAsync(EmailChannel.Manufacturing, to, subject, body, replyTo, cc, bcc, files, cancellationToken);
    }

    /// <summary>Sends a no-reply email.</summary>
    [HttpPost("noreply")]
    public Task<ActionResult> SendNoReplyEmailAsync(
        [FromQuery] string? to,
        [FromQuery] string? subject,
        [FromQuery] string? body,
        [FromQuery] string? replyTo,
        [FromQuery] List<string>? cc,
        [FromQuery] List<string>? bcc,
        [FromForm] List<IFormFile>? files = null,
        CancellationToken cancellationToken = default)
    {
        return this.SendEmailCoreAsync(EmailChannel.NoReply, to, subject, body, replyTo, cc, bcc, files, cancellationToken);
    }

    /// <summary>Sends a support email.</summary>
    [HttpPost("support")]
    public Task<ActionResult> SendSupportEmailAsync(
        [FromQuery] string? to,
        [FromQuery] string? subject,
        [FromQuery] string? body,
        [FromQuery] string? replyTo,
        [FromQuery] List<string>? cc,
        [FromQuery] List<string>? bcc,
        [FromForm] List<IFormFile>? files = null,
        CancellationToken cancellationToken = default)
    {
        return this.SendEmailCoreAsync(EmailChannel.Support, to, subject, body, replyTo, cc, bcc, files, cancellationToken);
    }

    /// <summary>Sends an informational email with a plain text body.</summary>
    [HttpPost("info-plaintext")]
    public async Task<ActionResult> SendInfoEmailPlainTextAsync(
        [FromQuery] string? to,
        [FromQuery] string? subject,
        [FromQuery] string? replyTo,
        [FromQuery] List<string>? cc,
        [FromQuery] List<string>? bcc,
        [FromForm] List<IFormFile>? files = null,
        CancellationToken cancellationToken = default)
    {
        var body = await this.ReadPlainTextBodyAsync(cancellationToken);
        return await this.SendEmailCoreAsync(EmailChannel.Info, to, subject, body, replyTo, cc, bcc, files, cancellationToken);
    }

    /// <summary>Sends a manufacturing-related email with a plain text body.</summary>
    [HttpPost("manufacturing-plaintext")]
    public async Task<ActionResult> SendManufacturingEmailPlainTextAsync(
        [FromQuery] string? to,
        [FromQuery] string? subject,
        [FromQuery] string? replyTo,
        [FromQuery] List<string>? cc,
        [FromQuery] List<string>? bcc,
        [FromForm] List<IFormFile>? files = null,
        CancellationToken cancellationToken = default)
    {
        var body = await this.ReadPlainTextBodyAsync(cancellationToken);
        return await this.SendEmailCoreAsync(EmailChannel.Manufacturing, to, subject, body, replyTo, cc, bcc, files, cancellationToken);
    }

    /// <summary>Sends a no-reply email with a plain text body.</summary>
    [HttpPost("noreply-plaintext")]
    public async Task<ActionResult> SendNoReplyEmailPlainTextAsync(
        [FromQuery] string? to,
        [FromQuery] string? subject,
        [FromQuery] string? replyTo,
        [FromQuery] List<string>? cc,
        [FromQuery] List<string>? bcc,
        [FromForm] List<IFormFile>? files = null,
        CancellationToken cancellationToken = default)
    {
        var body = await this.ReadPlainTextBodyAsync(cancellationToken);
        return await this.SendEmailCoreAsync(EmailChannel.NoReply, to, subject, body, replyTo, cc, bcc, files, cancellationToken);
    }

    /// <summary>Sends a support email with a plain text body.</summary>
    [HttpPost("support-plaintext")]
    public async Task<ActionResult> SendSupportEmailPlainTextAsync(
        [FromQuery] string? to,
        [FromQuery] string? subject,
        [FromQuery] string? replyTo,
        [FromQuery] List<string>? cc,
        [FromQuery] List<string>? bcc,
        [FromForm] List<IFormFile>? files = null,
        CancellationToken cancellationToken = default)
    {
        var body = await this.ReadPlainTextBodyAsync(cancellationToken);
        return await this.SendEmailCoreAsync(EmailChannel.Support, to, subject, body, replyTo, cc, bcc, files, cancellationToken);
    }

    private async Task<ActionResult> SendEmailCoreAsync(
        EmailChannel channel,
        string? to,
        string? subject,
        string? body,
        string? replyTo,
        List<string>? cc,
        List<string>? bcc,
        List<IFormFile>? files,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(to))
        {
            this.ModelState.AddModelError(nameof(to), "Recipient address is required.");
        }

        if (string.IsNullOrEmpty(subject))
        {
            this.ModelState.AddModelError(nameof(subject), "Email subject is required.");
        }

        if (string.IsNullOrEmpty(body))
        {
            this.ModelState.AddModelError(nameof(body), "Email body is required.");
        }

        if (files is not null)
        {
            if (files.Sum(item => item.Length) > SizeLimit)
            {
                this.ModelState.AddModelError(nameof(files), "Combined file size is too large.");
            }

            if (files.Any(item => item.Length == 0))
            {
                this.ModelState.AddModelError(nameof(files), "A file cannot be empty.");
            }
        }

        if (!this.ModelState.IsValid)
        {
            return new BadRequestObjectResult(this.ModelState);
        }

        var attachments = await CopyAttachmentsAsync(files, cancellationToken);
        var result = await notificationService.SendAsync(
            channel,
            new NotificationSendRequest
            {
                To = to!,
                Subject = subject!,
                Body = body!,
                ReplyTo = replyTo,
                Cc = cc,
                Bcc = bcc,
                Attachments = attachments,
            },
            cancellationToken);

        return new StatusCodeResult((int)result.StatusCode);
    }

    private async Task<string> ReadPlainTextBodyAsync(CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(this.Request.Body);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<NotificationAttachment>?> CopyAttachmentsAsync(
        IReadOnlyList<IFormFile>? files,
        CancellationToken cancellationToken)
    {
        if (files is null || files.Count == 0)
        {
            return null;
        }

        var attachments = new List<NotificationAttachment>(files.Count);
        foreach (var file in files)
        {
            await using var stream = file.OpenReadStream();
            using var memoryStream = new MemoryStream();
            await stream.CopyToAsync(memoryStream, cancellationToken);
            attachments.Add(new NotificationAttachment(file.FileName, file.ContentType, memoryStream.ToArray()));
        }

        return attachments;
    }
}
