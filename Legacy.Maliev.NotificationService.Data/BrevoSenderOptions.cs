using System.ComponentModel.DataAnnotations;

namespace Legacy.Maliev.NotificationService.Data;

/// <summary>Configured sender identity for one legacy channel.</summary>
public sealed class BrevoSenderOptions
{
    /// <summary>Sender email address.</summary>
    [Required]
    [EmailAddress]
    public string Address { get; init; } = string.Empty;

    /// <summary>Sender display name.</summary>
    [Required]
    public string DisplayName { get; init; } = string.Empty;
}
