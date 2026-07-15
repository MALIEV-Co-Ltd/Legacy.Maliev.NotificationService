namespace Legacy.Maliev.NotificationService.Domain;

/// <summary>Attachment payload copied from the inbound multipart request.</summary>
/// <param name="FileName">Original uploaded file name.</param>
/// <param name="ContentType">Uploaded content type, when supplied by the client.</param>
/// <param name="Content">Attachment content bytes.</param>
public sealed record NotificationAttachment(string FileName, string? ContentType, byte[] Content);
