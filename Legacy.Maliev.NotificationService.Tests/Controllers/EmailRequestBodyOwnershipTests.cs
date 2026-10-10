using System.Net;
using System.Text;
using Legacy.Maliev.NotificationService.Api.Controllers;
using Legacy.Maliev.NotificationService.Application.Interfaces;
using Legacy.Maliev.NotificationService.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace Legacy.Maliev.NotificationService.Tests.Controllers;

public sealed class EmailRequestBodyOwnershipTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlaintextReader_LeavesFrameworkBodyOpenBeforeProviderAndOnProviderFailure(bool providerFailure)
    {
        const string body = "<p>Body ทดสอบ</p>";
        using var stream = new ObservedBody([0xef, 0xbb, 0xbf, .. Encoding.UTF8.GetBytes(body)]);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var failure = new IOException("synthetic provider failure");
        var service = new Mock<INotificationService>(MockBehavior.Strict);
        service.Setup(value => value.SendAsync(EmailChannel.Info,
                It.IsAny<NotificationSendRequest>(), deadline.Token))
            .Returns((EmailChannel _, NotificationSendRequest request, CancellationToken _) =>
            {
                Assert.Equal(0, stream.DisposalCount);
                Assert.True(stream.CanRead);
                Assert.Equal(body, request.Body);
                return providerFailure
                    ? Task.FromException<NotificationSendResult>(failure)
                    : Task.FromResult(new NotificationSendResult(HttpStatusCode.Accepted));
            });
        var controller = Controller(service.Object, stream);
        if (providerFailure)
        {
            var observed = await Assert.ThrowsAsync<IOException>(() => controller.SendInfoEmailPlainTextAsync(
                "synthetic@example.invalid", "Synthetic", null, null, null, cancellationToken: deadline.Token));
            Assert.Same(failure, observed);
        }
        else
        {
            var result = await controller.SendInfoEmailPlainTextAsync(
                "synthetic@example.invalid", "Synthetic", null, null, null, cancellationToken: deadline.Token);
            Assert.Equal(202, Assert.IsType<StatusCodeResult>(result).StatusCode);
        }
        Assert.Equal(0, stream.DisposalCount);
        Assert.True(stream.CanRead);
        service.VerifyAll();
        stream.Dispose();
        Assert.Equal(1, stream.DisposalCount);
    }

    [Fact]
    public async Task PlaintextReadFailure_PreservesFrameworkBodyAndNeverCallsProvider()
    {
        using var stream = new ObservedBody([], failRead: true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var service = new Mock<INotificationService>(MockBehavior.Strict);
        var controller = Controller(service.Object, stream);
        var failure = await Assert.ThrowsAsync<IOException>(() => controller.SendInfoEmailPlainTextAsync(
            "synthetic@example.invalid", "Synthetic", null, null, null, cancellationToken: deadline.Token));
        Assert.Same(stream.Failure, failure);
        Assert.Equal(0, stream.DisposalCount);
        Assert.True(stream.CanRead);
        service.VerifyNoOtherCalls();
        stream.Dispose();
        Assert.Equal(1, stream.DisposalCount);
    }

    private static EmailsController Controller(INotificationService service, Stream body)
    {
        var context = new DefaultHttpContext();
        context.Request.Body = body;
        return new EmailsController(service) { ControllerContext = new ControllerContext { HttpContext = context } };
    }

    private sealed class ObservedBody(byte[] data, bool failRead = false) : MemoryStream(data)
    {
        public int DisposalCount { get; private set; }
        public IOException Failure { get; } = new("synthetic request body read failure");

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            failRead ? ValueTask.FromException<int>(Failure) : base.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            failRead ? Task.FromException<int>(Failure) : base.ReadAsync(buffer, offset, count, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing && CanRead) DisposalCount++;
            base.Dispose(disposing);
        }
    }
}
