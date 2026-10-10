using System.ComponentModel.DataAnnotations;
using System.Net.Mail;

namespace Legacy.Maliev.NotificationService.Api.Models;

/// <summary>Accepts the exact mailbox syntax supported by the legacy application without normalizing caller input.</summary>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property | AttributeTargets.Field)]
public sealed class LegacyMailboxAttribute : ValidationAttribute
{
    /// <summary>Validates a mailbox; separate required validation owns null admission.</summary>
    public override bool IsValid(object? value) => value is null ||
        value is string text && !string.IsNullOrWhiteSpace(text) &&
        MailAddress.TryCreate(text, out var address) && address.Address == text;
}
