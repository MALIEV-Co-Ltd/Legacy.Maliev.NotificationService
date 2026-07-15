using System.ComponentModel.DataAnnotations;
using Legacy.Maliev.NotificationService.Domain;

namespace Legacy.Maliev.NotificationService.Data;

/// <summary>Brevo provider configuration supplied from Secret Manager or environment variables.</summary>
public sealed class BrevoNotificationOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Brevo";

    /// <summary>Brevo API key. Must come from <c>maliev-legacy-secrets</c> outside local development.</summary>
    [Required]
    public string ApiKey { get; init; } = string.Empty;

    /// <summary>Configured sender identities by legacy channel.</summary>
    [Required]
    [MinLength(4)]
    public Dictionary<EmailChannel, BrevoSenderOptions> Senders { get; init; } = new();
}
