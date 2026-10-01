namespace Legacy.Maliev.NotificationService.Domain;

/// <summary>Monotonic notification authority, distinct from recipient delivery.</summary>
public enum DeliveryIntentState
{
    /// <summary>Durably admitted with no provider submission fence.</summary>
    Admitted,
    /// <summary>A submission fence exists; absence of a result is uncertain.</summary>
    Submitting,
    /// <summary>A provider acceptance identifier was durably recorded.</summary>
    ProviderAccepted,
    /// <summary>A provider effect may exist and automatic resubmission is forbidden.</summary>
    OutcomeUnknown,
    /// <summary>A definitive rejection occurred before any submission.</summary>
    RejectedBeforeSubmission,
}

/// <summary>Verified service-owned scope and immutable business purpose.</summary>
/// <param name="Issuer">Verified token issuer.</param>
/// <param name="ServiceSubject">Verified service subject.</param>
/// <param name="IntentId">Notification-specific UUID.</param>
/// <param name="Purpose">Exact supported purpose.</param>
/// <param name="ResourceType">Exact resource type.</param>
/// <param name="ResourceId">Positive invoice identity.</param>
/// <param name="WorkflowOperationId">Producer workflow UUID, distinct from notification UUID.</param>
/// <param name="Channel">Selected sender channel.</param>
public sealed record DeliveryIntentIdentity(string Issuer, string ServiceSubject, Guid IntentId,
    string Purpose, string ResourceType, int ResourceId, Guid WorkflowOperationId, EmailChannel Channel);

/// <summary>Opaque durable authority without raw message or payload digest.</summary>
/// <param name="Identity">Immutable service-owned tuple.</param>
/// <param name="BindingVersion">Canonical HMAC framing revision.</param>
/// <param name="KeyId">Retained verification-key identity.</param>
/// <param name="Binding">Lowercase HMAC-SHA256.</param>
/// <param name="State">Durable monotonic state.</param>
/// <param name="Version">CAS generation.</param>
/// <param name="AdmittedAt">UTC admission time.</param>
/// <param name="UpdatedAt">UTC last transition time.</param>
/// <param name="ProviderMessageId">Provider acceptance identifier, never delivery proof.</param>
public sealed record DeliveryIntentRecord(DeliveryIntentIdentity Identity, string BindingVersion, string KeyId,
    string Binding, DeliveryIntentState State, long Version, DateTime AdmittedAt, DateTime UpdatedAt, string? ProviderMessageId);
