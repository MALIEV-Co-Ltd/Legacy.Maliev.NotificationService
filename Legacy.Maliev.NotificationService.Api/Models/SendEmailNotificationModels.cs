using System.ComponentModel.DataAnnotations;

namespace Legacy.Maliev.NotificationService.Api.Models;

/// <summary>JSON request for one provider-independent email notification.</summary>
public sealed record SendEmailNotificationRequest(
    [param: Required, EmailAddress] string To,
    [param: Required, StringLength(200)] string Subject,
    [param: Required, StringLength(100_000)] string Body,
    [param: EmailAddress] string? ReplyTo,
    IReadOnlyList<string>? Cc,
    IReadOnlyList<string>? Bcc,
    IReadOnlyList<SendEmailNotificationAttachment>? Attachments = null);

/// <summary>Base64-encoded attachment included in a JSON notification request.</summary>
public sealed record SendEmailNotificationAttachment(
    [param: Required, StringLength(255)] string FileName,
    [param: StringLength(255)] string? ContentType,
    [param: Required] byte[] Content);

/// <summary>JSON response returned after the configured provider accepts an email.</summary>
public sealed record SendEmailNotificationResponse(string? ProviderMessageId);
