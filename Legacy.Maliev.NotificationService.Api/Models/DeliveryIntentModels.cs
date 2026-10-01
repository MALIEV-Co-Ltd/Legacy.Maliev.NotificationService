using System.ComponentModel.DataAnnotations;

namespace Legacy.Maliev.NotificationService.Api.Models;

/// <summary>V2 admission metadata; actor identity comes only from verified service authorization.</summary>
public sealed record DeliveryIntentAdmissionRequest(
    [param: Required] string Purpose,
    [param: Required] string ResourceType,
    [param: Required] string ResourceId,
    [param: Required] string WorkflowOperationId,
    [param: Required] string Channel,
    [param: Required] string PayloadDigestVersion,
    [param: Required] string PayloadDigest);

/// <summary>Transient V2 payload; optional arrays deliberately retain null versus present-empty.</summary>
public sealed record DeliveryIntentExecuteRequest(
    [param: Required] string Channel,
    [param: Required] string To,
    [param: Required] string Subject,
    [param: Required] string Body,
    string? ReplyTo,
    IReadOnlyList<string>? Cc,
    IReadOnlyList<string>? Bcc,
    IReadOnlyList<SendEmailNotificationAttachment>? Attachments);

/// <summary>PII-minimal V2 authority receipt; acceptance is not recipient delivery.</summary>
public sealed record DeliveryIntentResponse(string IntentId, string Purpose, string ResourceType,
    string ResourceId, string WorkflowOperationId, string State, long Version, DateTime AdmittedAt,
    DateTime UpdatedAt, string? ProviderMessageId);
