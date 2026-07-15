using System.Net;
using System.Reflection;
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
                ["bcc@example.com"]),
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
        Assert.Null(captured.Attachments);
    }
}
