using System.Globalization;
using Asp.Versioning;
using Legacy.Maliev.NotificationService.Api.Models;
using Legacy.Maliev.NotificationService.Application.Interfaces;
using Legacy.Maliev.NotificationService.Application.Services;
using Legacy.Maliev.NotificationService.Domain;
using Maliev.Aspire.ServiceDefaults.IAM;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Legacy.Maliev.NotificationService.Api.Controllers;

/// <summary>Opt-in immutable invoice notification admission, one attempt and scoped readback.</summary>
[ApiController, ApiVersion("2.0"), Authorize]
[Route("notifications/v{version:apiVersion}/delivery-intents")]
public sealed class DeliveryIntentsController(IConfiguration configuration, IServiceProvider services,
    ILogger<DeliveryIntentsController> logger) : ControllerBase
{
    /// <summary>Admits message authority without contacting the provider.</summary>
    [HttpPut("{intentId}")]
    [ProducesResponseType<DeliveryIntentResponse>(StatusCodes.Status200OK)]
    public Task<ActionResult> AdmitAsync(string intentId, DeliveryIntentAdmissionRequest request, CancellationToken token) => GuardAsync(async () =>
    {
        if (!Enabled) return Unavailable();
        if (!TryActor(out var issuer)) return Forbid();
        var id = CanonicalUuid(intentId);
        var workflow = CanonicalUuid(request.WorkflowOperationId);
        if (!int.TryParse(request.ResourceId, NumberStyles.None, CultureInfo.InvariantCulture, out var resource) || resource <= 0 ||
            request.ResourceId != resource.ToString(CultureInfo.InvariantCulture) || !Enum.TryParse<EmailChannel>(request.Channel, out var channel) || channel.ToString() != request.Channel)
            throw new ArgumentException();
        var identity = new DeliveryIntentIdentity(issuer, "service:legacy-accounting", id, request.Purpose, request.ResourceType, resource, workflow, channel);
        NotificationIntentBinding.ValidateIdentity(identity);
        if (!await AuthorizedAsync(identity, "admit", token)) return Forbid();
        var admitted = await services.GetRequiredService<DeliveryIntentAdmissionService>().AdmitAsync(identity, request.PayloadDigestVersion, request.PayloadDigest, token);
        return Ok(Receipt(admitted));
    }, token);

    /// <summary>Executes only the sole acknowledged durable attempt.</summary>
    [HttpPost("{intentId}/execute")]
    [ProducesResponseType<DeliveryIntentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<DeliveryIntentResponse>(StatusCodes.Status202Accepted)]
    public Task<ActionResult> ExecuteAsync(string intentId, DeliveryIntentExecuteRequest request, CancellationToken token) => GuardAsync(async () =>
    {
        if (!Enabled) return Unavailable();
        if (!TryActor(out var issuer)) return Forbid();
        var observed = await Store.ReadAsync(issuer, "service:legacy-accounting", CanonicalUuid(intentId), token);
        if (observed is null) return NotFound();
        if (!await AuthorizedAsync(observed.Identity, "execute", token)) return Forbid();
        if (request.Channel != observed.Identity.Channel.ToString()) throw new DeliveryIntentConflictException();
        var payload = new NotificationSendRequest
        {
            To = request.To,
            Subject = request.Subject,
            Body = request.Body,
            ReplyTo = request.ReplyTo,
            Cc = request.Cc,
            Bcc = request.Bcc,
            Attachments = request.Attachments?.Select(attachment => attachment is null ? null! :
                new NotificationAttachment(attachment.FileName, attachment.ContentType, attachment.Content)).ToArray(),
        };
        var result = await services.GetRequiredService<DeliveryIntentExecutionService>().ExecuteAsync(observed, payload, token);
        return StatusCode(result.State == DeliveryIntentState.ProviderAccepted ? StatusCodes.Status200OK : StatusCodes.Status202Accepted, Receipt(result));
    }, token);

    /// <summary>Reads an authorized service-owned receipt; absence is not no-send proof.</summary>
    [HttpGet("{intentId}")]
    [ProducesResponseType<DeliveryIntentResponse>(StatusCodes.Status200OK)]
    public Task<ActionResult> ReadAsync(string intentId, CancellationToken token) => GuardAsync(async () =>
    {
        if (!Enabled) return Unavailable();
        if (!TryActor(out var issuer)) return Forbid();
        var observed = await Store.ReadAsync(issuer, "service:legacy-accounting", CanonicalUuid(intentId), token);
        if (observed is null) return NotFound();
        if (!await AuthorizedAsync(observed.Identity, "read", token)) return Forbid();
        return Ok(Receipt(observed));
    }, token);

    private bool Enabled => configuration.GetValue<bool>("Notifications:DeliveryIntentsEnabled");
    private IDeliveryIntentStore Store => services.GetRequiredService<IDeliveryIntentStore>();

    private bool TryActor(out string issuer)
    {
        issuer = configuration["Jwt:Issuer"] ?? string.Empty;
        static bool Exactly(System.Security.Claims.ClaimsPrincipal principal, string type, string value) =>
            principal.FindAll(type).Select(claim => claim.Value).ToArray() is [var actual] && actual == value;
        return User.Identity?.IsAuthenticated == true && issuer.Length > 0 &&
            Exactly(User, "iss", issuer) && Exactly(User, "aud", configuration["Jwt:Audience"] ?? string.Empty) &&
            Exactly(User, "sub", "service:legacy-accounting") && Exactly(User, "identity_kind", "service");
    }

    private async Task<bool> AuthorizedAsync(DeliveryIntentIdentity identity, string operation, CancellationToken token)
    {
        var iam = services.GetService<IamServiceClient>();
        return iam is not null && await iam.CheckPermissionLiveAsync(identity.ServiceSubject,
            "legacy.notifications.intent." + operation,
            "legacy-notification/invoice/" + identity.ResourceId.ToString(CultureInfo.InvariantCulture), token);
    }

    private static Guid CanonicalUuid(string value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty && value == id.ToString("D")
        ? id : throw new ArgumentException();

    private static DeliveryIntentResponse Receipt(DeliveryIntentRecord record) => new(record.Identity.IntentId.ToString("D"),
        record.Identity.Purpose, record.Identity.ResourceType, record.Identity.ResourceId.ToString(CultureInfo.InvariantCulture),
        record.Identity.WorkflowOperationId.ToString("D"), char.ToLowerInvariant(record.State.ToString()[0]) + record.State.ToString()[1..],
        record.Version, record.AdmittedAt, record.UpdatedAt, record.ProviderMessageId);

    private ActionResult Unavailable() => StatusCode(503, new ProblemDetails { Status = 503, Title = "Notification intent authority unavailable" });

    private async Task<ActionResult> GuardAsync(Func<Task<ActionResult>> execute, CancellationToken token)
    {
        try { return await execute(); }
        catch (ArgumentException) { return BadRequest(new ProblemDetails { Status = 400, Title = "Invalid notification intent request" }); }
        catch (DeliveryIntentConflictException) { return Conflict(new ProblemDetails { Status = 409, Title = "Notification intent conflict" }); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            logger.LogWarning("Notification intent operation unavailable; category {Category}.", "authority-unavailable");
            return Unavailable();
        }
    }
}
