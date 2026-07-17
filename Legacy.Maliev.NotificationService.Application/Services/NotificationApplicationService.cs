using System.Net;
using System.Net.Mail;
using Legacy.Maliev.NotificationService.Application.Interfaces;
using Legacy.Maliev.NotificationService.Domain;
using Microsoft.Extensions.Logging;

namespace Legacy.Maliev.NotificationService.Application.Services;

/// <summary>Validates and sends legacy notification requests.</summary>
public sealed class NotificationApplicationService(
    INotificationProvider provider,
    ILogger<NotificationApplicationService> logger) : INotificationService
{
    /// <inheritdoc />
    public async Task<NotificationSendResult> SendAsync(
        EmailChannel channel,
        NotificationSendRequest request,
        CancellationToken cancellationToken)
    {
        if (!IsValidEmail(request.To) ||
            !IsValidOptionalEmail(request.ReplyTo) ||
            HasInvalidEmail(request.Cc) ||
            HasInvalidEmail(request.Bcc))
        {
            logger.LogWarning("Rejected legacy notification request with invalid email address for channel {Channel}.", channel);
            return new NotificationSendResult(HttpStatusCode.BadRequest);
        }

        try
        {
            return await provider.SendAsync(channel, request, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(
                "Notification provider failed for channel {Channel} with failure type {FailureType}.",
                channel,
                exception.GetType().Name);
            return new NotificationSendResult(HttpStatusCode.BadGateway);
        }
    }

    private static bool HasInvalidEmail(IReadOnlyList<string>? values)
    {
        return values is not null && values.Any(value => !string.IsNullOrWhiteSpace(value) && !IsValidEmail(value));
    }

    private static bool IsValidOptionalEmail(string? value)
    {
        return string.IsNullOrWhiteSpace(value) || IsValidEmail(value);
    }

    private static bool IsValidEmail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            var address = new MailAddress(value);
            return address.Address == value;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
