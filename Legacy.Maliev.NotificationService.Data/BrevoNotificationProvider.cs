using System.Net;
using brevo_csharp.Api;
using brevo_csharp.Client;
using brevo_csharp.Model;
using Legacy.Maliev.NotificationService.Application.Interfaces;
using Legacy.Maliev.NotificationService.Domain;
using Microsoft.Extensions.Options;

namespace Legacy.Maliev.NotificationService.Data;

/// <summary>Sends legacy notifications through Brevo transactional email.</summary>
public sealed class BrevoNotificationProvider : INotificationProvider
{
    private readonly BrevoNotificationOptions options;
    private readonly TransactionalEmailsApi transactionalEmailsApi;

    /// <summary>Initializes a new instance of the <see cref="BrevoNotificationProvider"/> class.</summary>
    public BrevoNotificationProvider(IOptions<BrevoNotificationOptions> options)
    {
        this.options = options.Value;

        var configuration = new brevo_csharp.Client.Configuration();
        configuration.ApiKey["api-key"] = this.options.ApiKey;
        this.transactionalEmailsApi = new TransactionalEmailsApi(configuration);
    }

    /// <inheritdoc />
    public async Task<NotificationSendResult> SendAsync(
        EmailChannel channel,
        NotificationSendRequest request,
        CancellationToken cancellationToken)
    {
        if (!this.options.Senders.TryGetValue(channel, out var sender))
        {
            return new NotificationSendResult(HttpStatusCode.BadRequest);
        }

        var email = new SendSmtpEmail(
            sender: new SendSmtpEmailSender(name: sender.DisplayName, email: sender.Address),
            to: new List<SendSmtpEmailTo> { new(request.To) },
            subject: request.Subject,
            htmlContent: request.Body)
        {
            ReplyTo = string.IsNullOrWhiteSpace(request.ReplyTo) ? null : new SendSmtpEmailReplyTo(request.ReplyTo),
            Attachment = ConvertAttachments(request.Attachments),
        };

        if (request.Cc is { Count: > 0 })
        {
            email.Cc = request.Cc
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => new SendSmtpEmailCc(value))
                .ToList();
        }

        if (request.Bcc is { Count: > 0 })
        {
            email.Bcc = request.Bcc
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => new SendSmtpEmailBcc(value))
                .ToList();
        }

        cancellationToken.ThrowIfCancellationRequested();
        var result = await this.transactionalEmailsApi.SendTransacEmailAsync(email);
        return new NotificationSendResult(
            result.MessageId is null ? HttpStatusCode.BadRequest : HttpStatusCode.OK,
            result.MessageId);
    }

    private static List<SendSmtpEmailAttachment>? ConvertAttachments(IReadOnlyList<NotificationAttachment>? attachments)
    {
        if (attachments is null || attachments.Count == 0)
        {
            return null;
        }

        return attachments
            .Select(attachment => new SendSmtpEmailAttachment(
                content: attachment.Content,
                name: attachment.FileName))
            .ToList();
    }
}
