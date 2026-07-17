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

    /// <summary>Gets the maximum number of retries after the initial attempt.</summary>
    [Range(0, 3)]
    public int MaxRetryAttempts { get; init; } = 2;

    /// <summary>Gets the fixed delay between retry attempts.</summary>
    [Range(0, 5000)]
    public int RetryDelayMilliseconds { get; init; } = 200;

    /// <summary>Gets the maximum accepted provider-requested retry delay.</summary>
    [Range(0, 30000)]
    public int MaxRetryDelayMilliseconds { get; init; } = 5000;

    /// <summary>Gets the maximum duration of each provider attempt in milliseconds.</summary>
    [Range(10, 60000)]
    public int AttemptTimeoutMilliseconds { get; init; } = 10000;
}
