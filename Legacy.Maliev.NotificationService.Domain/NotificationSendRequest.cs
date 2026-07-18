namespace Legacy.Maliev.NotificationService.Domain;

/// <summary>Provider-independent email send request.</summary>
public sealed record NotificationSendRequest
{
    /// <summary>Recipient email address.</summary>
    public required string To { get; init; }

    /// <summary>Email subject.</summary>
    public required string Subject { get; init; }

    /// <summary>Email body. Legacy HTML routes pass HTML; plaintext routes pass raw request body text.</summary>
    public required string Body { get; init; }

    /// <summary>Optional reply-to address.</summary>
    public string? ReplyTo { get; init; }

    /// <summary>Optional CC recipient list.</summary>
    public IReadOnlyList<string>? Cc { get; init; }

    /// <summary>Optional BCC recipient list.</summary>
    public IReadOnlyList<string>? Bcc { get; init; }

    /// <summary>Optional attachments.</summary>
    public IReadOnlyList<NotificationAttachment>? Attachments { get; init; }

    /// <summary>
    /// Optional caller-stable operation identifier used to suppress duplicate provider delivery
    /// when the same logical send is retried across HTTP requests.
    /// </summary>
    public string? IdempotencyKey { get; init; }
}
