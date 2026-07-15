using System.Net;
using System.Text;
using Legacy.Maliev.NotificationService.Api.Controllers;
using Legacy.Maliev.NotificationService.Application.Interfaces;
using Legacy.Maliev.NotificationService.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace Legacy.Maliev.NotificationService.Tests.Controllers;

public sealed class EmailsControllerCompatibilityTests
{
    [Fact]
    public async Task SendInfoEmailAsync_PreservesLegacyRouteChannelAndQueryContract()
    {
        NotificationSendRequest? captured = null;
        var service = new Mock<INotificationService>(MockBehavior.Strict);
        service
            .Setup(item => item.SendAsync(EmailChannel.Info, It.IsAny<NotificationSendRequest>(), It.IsAny<CancellationToken>()))
            .Callback<EmailChannel, NotificationSendRequest, CancellationToken>((_, request, _) => captured = request)
            .ReturnsAsync(new NotificationSendResult(HttpStatusCode.OK, "message-id"));

        var controller = CreateController(service.Object);

        var result = await controller.SendInfoEmailAsync(
            to: "customer@example.com",
            subject: "Subject",
            body: "<p>Body</p>",
            replyTo: "reply@example.com",
            cc: ["cc@example.com"],
            bcc: ["bcc@example.com"]);

        var status = Assert.IsType<StatusCodeResult>(result);
        Assert.Equal(StatusCodes.Status200OK, status.StatusCode);
        Assert.NotNull(captured);
        Assert.Equal("customer@example.com", captured.To);
        Assert.Equal("Subject", captured.Subject);
        Assert.Equal("<p>Body</p>", captured.Body);
        Assert.Equal("reply@example.com", captured.ReplyTo);
        Assert.Equal(["cc@example.com"], captured.Cc);
        Assert.Equal(["bcc@example.com"], captured.Bcc);
    }

    [Fact]
    public async Task SendSupportEmailPlainTextAsync_ReadsRawBody()
    {
        NotificationSendRequest? captured = null;
        var service = new Mock<INotificationService>(MockBehavior.Strict);
        service
            .Setup(item => item.SendAsync(EmailChannel.Support, It.IsAny<NotificationSendRequest>(), It.IsAny<CancellationToken>()))
            .Callback<EmailChannel, NotificationSendRequest, CancellationToken>((_, request, _) => captured = request)
            .ReturnsAsync(new NotificationSendResult(HttpStatusCode.OK, "message-id"));

        var controller = CreateController(service.Object, "plain text body");

        var result = await controller.SendSupportEmailPlainTextAsync(
            to: "customer@example.com",
            subject: "Subject",
            replyTo: null,
            cc: null,
            bcc: null);

        var status = Assert.IsType<StatusCodeResult>(result);
        Assert.Equal(StatusCodes.Status200OK, status.StatusCode);
        Assert.Equal("plain text body", captured?.Body);
    }

    [Fact]
    public async Task SendNoReplyEmailAsync_ReturnsLegacyModelStateErrors()
    {
        var service = new Mock<INotificationService>(MockBehavior.Strict);
        var controller = CreateController(service.Object);

        var result = await controller.SendNoReplyEmailAsync(
            to: "",
            subject: "",
            body: "",
            replyTo: null,
            cc: null,
            bcc: null);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var modelState = Assert.IsAssignableFrom<SerializableError>(badRequest.Value);
        Assert.True(modelState.ContainsKey("to"));
        Assert.True(modelState.ContainsKey("subject"));
        Assert.True(modelState.ContainsKey("body"));
        service.Verify(item => item.SendAsync(It.IsAny<EmailChannel>(), It.IsAny<NotificationSendRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SendManufacturingEmailAsync_RejectsCombinedAttachmentsOverLegacyLimit()
    {
        var service = new Mock<INotificationService>(MockBehavior.Strict);
        var controller = CreateController(service.Object);

        var result = await controller.SendManufacturingEmailAsync(
            to: "customer@example.com",
            subject: "Subject",
            body: "Body",
            replyTo: null,
            cc: null,
            bcc: null,
            files: [CreateFormFile(EmailsController.SizeLimit + 1)]);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var modelState = Assert.IsAssignableFrom<SerializableError>(badRequest.Value);
        Assert.True(modelState.ContainsKey("files"));
        service.Verify(item => item.SendAsync(It.IsAny<EmailChannel>(), It.IsAny<NotificationSendRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SendInfoEmailAsync_CopiesAttachmentsBeforeProviderCall()
    {
        NotificationSendRequest? captured = null;
        var service = new Mock<INotificationService>(MockBehavior.Strict);
        service
            .Setup(item => item.SendAsync(EmailChannel.Info, It.IsAny<NotificationSendRequest>(), It.IsAny<CancellationToken>()))
            .Callback<EmailChannel, NotificationSendRequest, CancellationToken>((_, request, _) => captured = request)
            .ReturnsAsync(new NotificationSendResult(HttpStatusCode.OK, "message-id"));

        var controller = CreateController(service.Object);

        var result = await controller.SendInfoEmailAsync(
            to: "customer@example.com",
            subject: "Subject",
            body: "Body",
            replyTo: null,
            cc: null,
            bcc: null,
            files: [CreateFormFile(4, "test.txt", "text/plain", "test")]);

        var status = Assert.IsType<StatusCodeResult>(result);
        Assert.Equal(StatusCodes.Status200OK, status.StatusCode);
        var attachment = Assert.Single(captured?.Attachments ?? []);
        Assert.Equal("test.txt", attachment.FileName);
        Assert.Equal("text/plain", attachment.ContentType);
        Assert.Equal(Encoding.UTF8.GetBytes("test"), attachment.Content);
    }

    private static EmailsController CreateController(INotificationService service, string body = "")
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        return new EmailsController(service)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = context,
            },
        };
    }

    private static IFormFile CreateFormFile(long length, string fileName = "large.pdf", string contentType = "application/pdf", string? content = null)
    {
        var bytes = Encoding.UTF8.GetBytes(content ?? "x");
        var stream = new MemoryStream(bytes);
        return new FormFile(stream, 0, length, "files", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType,
        };
    }
}
