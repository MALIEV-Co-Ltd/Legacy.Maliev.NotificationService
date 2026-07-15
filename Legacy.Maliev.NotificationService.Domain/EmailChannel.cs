namespace Legacy.Maliev.NotificationService.Domain;

/// <summary>Legacy sender channel selected by the public <c>/Emails</c> route.</summary>
public enum EmailChannel
{
    /// <summary>General information sender.</summary>
    Info,

    /// <summary>Manufacturing sender.</summary>
    Manufacturing,

    /// <summary>No-reply sender.</summary>
    NoReply,

    /// <summary>Support sender.</summary>
    Support,
}
