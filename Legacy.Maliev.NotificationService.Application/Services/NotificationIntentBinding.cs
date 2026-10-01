using System.Buffers.Binary;
using System.Globalization;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using Legacy.Maliev.NotificationService.Application.Interfaces;
using Legacy.Maliev.NotificationService.Domain;

namespace Legacy.Maliev.NotificationService.Application.Services;

/// <summary>Versioned canonical payload and keyed immutable intent binding.</summary>
public sealed class NotificationIntentBinding
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly IReadOnlyDictionary<string, byte[]> keys;
    /// <summary>Payload framing revision shared with the producer.</summary>
    public const string PayloadVersion = "notification-payload-v1";
    /// <summary>Persisted HMAC binding revision.</summary>
    public const string BindingVersion = "notification-intent-hmac-v1";

    /// <summary>Creates a binding service with explicitly supplied runtime-only verification keys.</summary>
    public NotificationIntentBinding(string activeKeyId, IReadOnlyDictionary<string, byte[]> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (!IsKeyId(activeKeyId) || !keys.ContainsKey(activeKeyId) ||
            keys.Any(pair => !IsKeyId(pair.Key) || pair.Value is null || pair.Value.Length is < 32 or > 128))
        {
            throw new DeliveryIntentUnavailableException();
        }

        this.keys = keys.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.Ordinal);
        ActiveKeyId = activeKeyId;
    }

    /// <summary>Gets the active admission key identity.</summary>
    public string ActiveKeyId { get; }

    /// <summary>Requires retained verification authority without revealing or replacing its key.</summary>
    public void EnsureAvailableKey(string keyId)
    {
        if (!keys.ContainsKey(keyId)) throw new DeliveryIntentUnavailableException();
    }

    /// <summary>Validates and hashes exact payload bytes without canonicalizing caller strings.</summary>
    public string PayloadDigest(EmailChannel channel, NotificationSendRequest payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (!Enum.IsDefined(channel)) throw new ArgumentException("Invalid notification channel.", nameof(channel));
        ValidateMailbox(payload.To, optional: false);
        ValidateMailbox(payload.ReplyTo, optional: true);
        ValidateText(payload.Subject, 998, required: true);
        ValidateText(payload.Body, 2 * 1024 * 1024, required: true);
        ValidateRecipients(payload.Cc);
        ValidateRecipients(payload.Bcc);
        if (payload.Attachments is { Count: > 64 }) throw new ArgumentException("Too many notification attachments.");
        long bytes = 0;
        if (payload.Attachments is not null)
        {
            foreach (var attachment in payload.Attachments)
            {
                if (attachment is null || attachment.Content is null || attachment.Content.Length == 0)
                    throw new ArgumentException("Invalid notification attachment.");
                ValidateText(attachment.FileName, 255, required: true);
                ValidateText(attachment.ContentType, 127, required: false);
                bytes = checked(bytes + attachment.Content.LongLength);
                if (bytes > 200L * 1024 * 1024) throw new ArgumentException("Notification attachments exceed the limit.");
            }
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Text(hash, "version", PayloadVersion);
        Text(hash, "channel", channel.ToString());
        Text(hash, "to", payload.To);
        Text(hash, "subject", payload.Subject);
        Text(hash, "body", payload.Body);
        Text(hash, "replyTo", payload.ReplyTo);
        Recipients(hash, "cc", payload.Cc);
        Recipients(hash, "bcc", payload.Bcc);
        ArrayHeader(hash, "attachments", payload.Attachments?.Count);
        if (payload.Attachments is not null)
        {
            foreach (var attachment in payload.Attachments)
            {
                Text(hash, "fileName", attachment.FileName);
                Text(hash, "contentType", attachment.ContentType);
                Field(hash, "content", attachment.Content);
            }
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>Computes a keyed digest over the full immutable service-owned tuple.</summary>
    public string ComputeBinding(DeliveryIntentIdentity identity, string payloadVersion, string payloadDigest, string keyId)
    {
        ValidateIdentity(identity);
        if (payloadVersion != PayloadVersion || !IsDigest(payloadDigest)) throw new ArgumentException("Invalid notification digest.");
        if (!keys.TryGetValue(keyId, out var key)) throw new DeliveryIntentUnavailableException();
        using var hash = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key);
        Text(hash, "version", BindingVersion);
        Text(hash, "issuer", identity.Issuer);
        Text(hash, "serviceSubject", identity.ServiceSubject);
        Text(hash, "intentId", identity.IntentId.ToString("D"));
        Text(hash, "purpose", identity.Purpose);
        Text(hash, "resourceType", identity.ResourceType);
        Text(hash, "resourceId", identity.ResourceId.ToString(CultureInfo.InvariantCulture));
        Text(hash, "workflowOperationId", identity.WorkflowOperationId.ToString("D"));
        Text(hash, "channel", identity.Channel.ToString());
        Text(hash, "payloadVersion", payloadVersion);
        Text(hash, "payloadDigest", payloadDigest);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>Checks the supported immutable identity without claiming authentication.</summary>
    public static void ValidateIdentity(DeliveryIntentIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (string.IsNullOrWhiteSpace(identity.Issuer) || identity.Issuer.Length > 256 ||
            identity.ServiceSubject != "service:legacy-accounting" || identity.IntentId == Guid.Empty ||
            identity.WorkflowOperationId == Guid.Empty || identity.IntentId == identity.WorkflowOperationId ||
            identity.Purpose != "invoice-issued" || identity.ResourceType != "invoice" || identity.ResourceId <= 0 ||
            !Enum.IsDefined(identity.Channel)) throw new ArgumentException("Invalid notification intent identity.");
    }

    /// <summary>Checks the exact persisted digest format.</summary>
    public static bool IsDigest(string? value) => value is { Length: 64 } && value.All(character =>
        character is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>Checks bounded opaque key identifiers.</summary>
    public static bool IsKeyId(string? value) => value is { Length: >= 1 and <= 64 } && value.All(character =>
        character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_');

    /// <summary>Compares retained bindings without content-dependent comparison timing.</summary>
    public static bool BindingEquals(string first, string second) => IsDigest(first) && IsDigest(second) &&
        CryptographicOperations.FixedTimeEquals(Convert.FromHexString(first), Convert.FromHexString(second));

    private static void ValidateText(string? value, int maximumBytes, bool required)
    {
        if ((required && string.IsNullOrWhiteSpace(value)) || (value is not null && EncodedLength(value) > maximumBytes))
            throw new ArgumentException("Invalid notification field length.");
    }

    private static void ValidateMailbox(string? value, bool optional)
    {
        if (optional && value is null) return;
        ValidateText(value, 254, required: true);
        if (!MailAddress.TryCreate(value, out var address) || address.Address != value)
            throw new ArgumentException("Invalid notification mailbox.");
    }

    private static void ValidateRecipients(IReadOnlyList<string>? recipients)
    {
        if (recipients is { Count: > 100 }) throw new ArgumentException("Too many notification recipients.");
        if (recipients is not null) foreach (var recipient in recipients) ValidateMailbox(recipient, optional: false);
    }

    private static void Text(IncrementalHash hash, string tag, string? value)
    {
        try { Field(hash, tag, value is null ? null : Utf8.GetBytes(value)); }
        catch (EncoderFallbackException) { throw new ArgumentException("Invalid notification text encoding."); }
    }

    private static int EncodedLength(string value)
    {
        try { return Utf8.GetByteCount(value); }
        catch (EncoderFallbackException) { throw new ArgumentException("Invalid notification text encoding."); }
    }

    private static void Field(IncrementalHash hash, string tag, byte[]? value)
    {
        hash.AppendData(Encoding.ASCII.GetBytes(tag));
        hash.AppendData([0]);
        hash.AppendData(value is null ? [255] : [0]);
        if (value is null) return;
        Length(hash, (ulong)value.LongLength);
        hash.AppendData(value);
    }

    private static void ArrayHeader(IncrementalHash hash, string tag, int? count)
    {
        hash.AppendData(Encoding.ASCII.GetBytes(tag));
        hash.AppendData([0]);
        hash.AppendData(count is null ? [255] : [0]);
        if (count is not null) Length(hash, (ulong)count.Value);
    }

    private static void Length(IncrementalHash hash, ulong value)
    {
        Span<byte> length = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(length, value);
        hash.AppendData(length);
    }

    private static void Recipients(IncrementalHash hash, string tag, IReadOnlyList<string>? recipients)
    {
        ArrayHeader(hash, tag, recipients?.Count);
        if (recipients is not null) foreach (var recipient in recipients) Text(hash, "recipient", recipient);
    }
}
