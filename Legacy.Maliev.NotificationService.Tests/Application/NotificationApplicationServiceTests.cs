using System.Net;
using Legacy.Maliev.NotificationService.Application.Interfaces;
using Legacy.Maliev.NotificationService.Application.Services;
using Legacy.Maliev.NotificationService.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Legacy.Maliev.NotificationService.Tests.Application;

public sealed class NotificationApplicationServiceTests
{
    [Fact]
    public async Task SendAsync_WithValidRequest_ForwardsToProvider()
    {
        var provider = new Mock<INotificationProvider>(MockBehavior.Strict);
        var expected = new NotificationSendResult(HttpStatusCode.OK, "message-id");
        provider
            .Setup(item => item.SendAsync(EmailChannel.Info, It.IsAny<NotificationSendRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var service = new NotificationApplicationService(provider.Object, NullLogger<NotificationApplicationService>.Instance);

        var result = await service.SendAsync(
            EmailChannel.Info,
            new NotificationSendRequest
            {
                To = "customer@example.com",
                Subject = "Subject",
                Body = "Body",
                ReplyTo = "reply@example.com",
                Cc = ["copy@example.com"],
                Bcc = ["blind@example.com"],
            },
            CancellationToken.None);

        Assert.Equal(expected, result);
        provider.VerifyAll();
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("")]
    public async Task SendAsync_WithInvalidRecipient_ReturnsBadRequestAndDoesNotCallProvider(string to)
    {
        var provider = new Mock<INotificationProvider>(MockBehavior.Strict);
        var service = new NotificationApplicationService(provider.Object, NullLogger<NotificationApplicationService>.Instance);

        var result = await service.SendAsync(
            EmailChannel.Info,
            new NotificationSendRequest
            {
                To = to,
                Subject = "Subject",
                Body = "Body",
            },
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        provider.Verify(item => item.SendAsync(It.IsAny<EmailChannel>(), It.IsAny<NotificationSendRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SendAsync_WhenProviderThrows_ReturnsBadGateway()
    {
        var provider = new Mock<INotificationProvider>(MockBehavior.Strict);
        provider
            .Setup(item => item.SendAsync(It.IsAny<EmailChannel>(), It.IsAny<NotificationSendRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("provider unavailable"));

        var service = new NotificationApplicationService(provider.Object, NullLogger<NotificationApplicationService>.Instance);

        var result = await service.SendAsync(
            EmailChannel.Support,
            new NotificationSendRequest
            {
                To = "customer@example.com",
                Subject = "Subject",
                Body = "Body",
            },
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadGateway, result.StatusCode);
    }
}
