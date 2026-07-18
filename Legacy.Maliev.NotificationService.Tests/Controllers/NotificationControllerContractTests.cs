using System.Net;
using System.Reflection;
using System.ComponentModel.DataAnnotations;
using Legacy.Maliev.NotificationService.Api.Authorization;
using Legacy.Maliev.NotificationService.Api.Controllers;
using Legacy.Maliev.NotificationService.Api.Models;
using Legacy.Maliev.NotificationService.Application.Interfaces;
using Legacy.Maliev.NotificationService.Domain;
using Maliev.Aspire.ServiceDefaults.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace Legacy.Maliev.NotificationService.Tests.Controllers;

public sealed class NotificationControllerContractTests
{
    [Fact]
    public void Controller_RequiresAuthenticationAndExplicitSendPermission()
    {
        var controller = typeof(NotificationsController);
        Assert.Equal("notifications/v1/email", controller.GetCustomAttribute<RouteAttribute>()?.Template);
        Assert.NotNull(controller.GetCustomAttribute<AuthorizeAttribute>());
        var action = controller.GetMethod(nameof(NotificationsController.SendEmailAsync))!;
        var permission = Assert.Single(action.GetCustomAttributes<RequirePermissionAttribute>());
        Assert.Equal(NotificationPermissions.Send, permission.Permission);
        Assert.False(permission.RequireLiveCheck);
        Assert.NotNull(action.GetParameters()[1].GetCustomAttribute<FromBodyAttribute>());
        var idempotencyHeader = action.GetParameters()[2].GetCustomAttribute<FromHeaderAttribute>();
        Assert.Equal("Idempotency-Key", idempotencyHeader?.Name);
    }

    [Fact]
    public void JsonRequest_DefinesValidationOnPrimaryConstructorParametersForAspNetCore10()
    {
        var request = typeof(SendEmailNotificationRequest);
        var constructor = Assert.Single(request.GetConstructors());
        var parameters = constructor.GetParameters().ToDictionary(
            parameter => parameter.Name!,
            StringComparer.OrdinalIgnoreCase);

        Assert.IsType<RequiredAttribute>(Assert.Single(parameters["To"].GetCustomAttributes<RequiredAttribute>()));
        Assert.IsType<EmailAddressAttribute>(Assert.Single(parameters["To"].GetCustomAttributes<EmailAddressAttribute>()));
        Assert.IsType<RequiredAttribute>(Assert.Single(parameters["Subject"].GetCustomAttributes<RequiredAttribute>()));
        Assert.IsType<RequiredAttribute>(Assert.Single(parameters["Body"].GetCustomAttributes<RequiredAttribute>()));
        Assert.IsType<EmailAddressAttribute>(Assert.Single(parameters["ReplyTo"].GetCustomAttributes<EmailAddressAttribute>()));
        Assert.All(
            request.GetProperties(),
            property => Assert.Empty(property.GetCustomAttributes<ValidationAttribute>()));
    }

    [Fact]
    public async Task SendEmail_MapsJsonBodyToProviderIndependentRequest()
    {
        NotificationSendRequest? captured = null;
        var service = new Mock<INotificationService>(MockBehavior.Strict);
        service
            .Setup(item => item.SendAsync(
                EmailChannel.Manufacturing,
                It.IsAny<NotificationSendRequest>(),
                It.IsAny<CancellationToken>()))
            .Callback<EmailChannel, NotificationSendRequest, CancellationToken>((_, request, _) => captured = request)
            .ReturnsAsync(new NotificationSendResult(HttpStatusCode.OK, "provider-message-id"));
        var controller = new NotificationsController(service.Object);

        var result = await controller.SendEmailAsync(
            EmailChannel.Manufacturing,
            new SendEmailNotificationRequest(
                "customer@example.com",
                "Quotation request #42",
                "<p>Received</p>",
                "reply@example.com",
                ["cc@example.com"],
                ["bcc@example.com"],
                [new SendEmailNotificationAttachment("receipt.pdf", "application/pdf", [1, 2, 3])]),
            "9e60b70d-21af-473e-8749-fab4993e4f4f",
            CancellationToken.None);

        var response = Assert.IsType<ObjectResult>(result);
        Assert.Equal(200, response.StatusCode);
        Assert.Equal("provider-message-id", Assert.IsType<SendEmailNotificationResponse>(response.Value).ProviderMessageId);
        Assert.NotNull(captured);
        Assert.Equal("customer@example.com", captured.To);
        Assert.Equal("Quotation request #42", captured.Subject);
        Assert.Equal("<p>Received</p>", captured.Body);
        Assert.Equal("reply@example.com", captured.ReplyTo);
        Assert.Equal(["cc@example.com"], captured.Cc);
        Assert.Equal(["bcc@example.com"], captured.Bcc);
        var attachment = Assert.Single(captured.Attachments!);
        Assert.Equal("receipt.pdf", attachment.FileName);
        Assert.Equal("application/pdf", attachment.ContentType);
        Assert.Equal([1, 2, 3], attachment.Content);
        Assert.Equal("9e60b70d-21af-473e-8749-fab4993e4f4f", captured.IdempotencyKey);
    }

    [Fact]
    public async Task SendEmail_RejectsMalformedIdempotencyKeyBeforeProviderCall()
    {
        var service = new Mock<INotificationService>(MockBehavior.Strict);
        var controller = new NotificationsController(service.Object);

        var result = await controller.SendEmailAsync(
            EmailChannel.Info,
            new SendEmailNotificationRequest(
                "customer@example.com",
                "Receipt",
                "Attached",
                null,
                null,
                null,
                null),
            "not-an-operation-id",
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }
}
