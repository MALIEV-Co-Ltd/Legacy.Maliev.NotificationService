using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Legacy.Maliev.NotificationService.Data;

/// <summary>Calls the Brevo transactional email API through a cancellable HTTP boundary.</summary>
public sealed class BrevoNotificationTransport(
    HttpClient httpClient,
    IOptions<BrevoNotificationOptions> options) : IBrevoNotificationTransport
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly BrevoNotificationOptions options = options.Value;

    /// <inheritdoc />
    public async Task<BrevoTransportResult> SendAsync(
        BrevoTransportRequest request,
        CancellationToken cancellationToken)
    {
        var notification = request.Notification;
        var payload = new
        {
            sender = new { name = request.Sender.DisplayName, email = request.Sender.Address },
            to = new[] { new { email = notification.To } },
            subject = notification.Subject,
            htmlContent = notification.Body,
            replyTo = string.IsNullOrWhiteSpace(notification.ReplyTo)
                ? null
                : new { email = notification.ReplyTo },
            cc = MapRecipients(notification.Cc),
            bcc = MapRecipients(notification.Bcc),
            attachment = notification.Attachments?.Select(attachment => new
            {
                content = Convert.ToBase64String(attachment.Content),
                name = attachment.FileName,
            }),
            headers = new Dictionary<string, string>
            {
                ["idempotencyKey"] = request.IdempotencyKey,
            },
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "smtp/email")
        {
            Content = JsonContent.Create(payload, options: SerializerOptions),
        };
        httpRequest.Headers.Add("api-key", this.options.ApiKey);

        using var response = await httpClient.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new BrevoTransportException(response.StatusCode, GetRetryAfter(response));
        }

        var result = await response.Content.ReadFromJsonAsync<BrevoSendResponse>(
            SerializerOptions,
            cancellationToken);
        return new BrevoTransportResult(result?.MessageId);
    }

    private static object[]? MapRecipients(IReadOnlyList<string>? recipients)
    {
        return recipients?
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => (object)new { email = value })
            .ToArray();
    }

    private static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
        {
            return delta;
        }

        if (retryAfter?.Date is { } date)
        {
            return date - DateTimeOffset.UtcNow;
        }

        return null;
    }

    private sealed record BrevoSendResponse(
        [property: JsonPropertyName("messageId")] string? MessageId);
}
