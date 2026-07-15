using System.ComponentModel.DataAnnotations;

namespace Legacy.Maliev.NotificationService.Api.Models;

/// <summary>JSON request for one provider-independent email notification.</summary>
public sealed record SendEmailNotificationRequest(
    [property: Required, EmailAddress] string To,
    [property: Required, StringLength(200)] string Subject,
    [property: Required, StringLength(100_000)] string Body,
    [property: EmailAddress] string? ReplyTo,
    IReadOnlyList<string>? Cc,
    IReadOnlyList<string>? Bcc);

/// <summary>JSON response returned after the configured provider accepts an email.</summary>
public sealed record SendEmailNotificationResponse(string? ProviderMessageId);
