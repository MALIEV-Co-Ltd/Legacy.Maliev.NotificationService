using System.Net;
using Legacy.Maliev.NotificationService.Api.Controllers;
using Legacy.Maliev.NotificationService.Application.Interfaces;
using Legacy.Maliev.NotificationService.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace Legacy.Maliev.NotificationService.Tests.Controllers;

// The legacy controller's finally block owned every opened attachment stream.
// The byte-copy adapter must release streams before invoking the provider and on copy failure.
public sealed class EmailAttachmentOwnershipTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CopiedStreams_AreClosedBeforeProviderSuccessOrFailure(bool providerFailure)
    {
        byte[] firstBytes = [0, 1, 255];
        byte[] secondBytes = [128, 0];
        using var first = new OwnedStream(firstBytes);
        using var second = new OwnedStream(secondBytes);
        var failure = new IOException("synthetic provider failure");
        var service = new Mock<INotificationService>(MockBehavior.Strict);
        service.Setup(value => value.SendAsync(EmailChannel.Info,
                It.IsAny<NotificationSendRequest>(), It.IsAny<CancellationToken>()))
            .Returns((EmailChannel _, NotificationSendRequest request, CancellationToken _) =>
            {
                Assert.Equal(1, first.DisposalCount);
                Assert.Equal(1, second.DisposalCount);
                Assert.NotNull(request.Attachments);
                Assert.Equal(2, request.Attachments.Count);
                Assert.Equal(firstBytes, request.Attachments[0].Content);
                Assert.Equal(secondBytes, request.Attachments[1].Content);
                return providerFailure
                    ? Task.FromException<NotificationSendResult>(failure)
                    : Task.FromResult(new NotificationSendResult(HttpStatusCode.Accepted));
            });
        var controller = new EmailsController(service.Object);
        var files = new List<IFormFile> { File(first, "first.bin"), File(second, "second.bin") };

        if (providerFailure)
        {
            var observed = await Assert.ThrowsAsync<IOException>(() => SendAsync(controller, files));
            Assert.Same(failure, observed);
        }
        else
        {
            var result = await SendAsync(controller, files);
            Assert.Equal(202, Assert.IsType<StatusCodeResult>(result).StatusCode);
        }
        Assert.Equal(1, first.DisposalCount);
        Assert.Equal(1, second.DisposalCount);
        service.Verify(value => value.SendAsync(EmailChannel.Info,
            It.IsAny<NotificationSendRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task LaterCopyFailure_ClosesBothOpenedStreamsWithoutCallingProvider()
    {
        using var first = new OwnedStream([0, 1]);
        using var second = new OwnedStream([2, 3], failCopy: true);
        var service = new Mock<INotificationService>(MockBehavior.Strict);
        var controller = new EmailsController(service.Object);

        await Assert.ThrowsAsync<IOException>(() => SendAsync(controller,
            [File(first, "first.bin"), File(second, "second.bin")]));

        Assert.Equal(1, first.DisposalCount);
        Assert.Equal(1, second.DisposalCount);
        service.Verify(value => value.SendAsync(It.IsAny<EmailChannel>(),
            It.IsAny<NotificationSendRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CancelledCopy_ClosesOpenedStreamWithoutCallingProvider()
    {
        using var stream = new OwnedStream([0, 1]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var service = new Mock<INotificationService>(MockBehavior.Strict);
        var controller = new EmailsController(service.Object);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SendAsync(controller,
            [File(stream, "first.bin")], cancellation.Token));

        Assert.Equal(1, stream.DisposalCount);
        service.Verify(value => value.SendAsync(It.IsAny<EmailChannel>(),
            It.IsAny<NotificationSendRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CancellationAfterPartialLaterCopy_ClosesOpenedStreamsAndNeverOpensNextAttachment()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var copyStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        byte[] secondBytes = [2, 3];
        var copiedBytes = 0;
        using var first = new OwnedStream([0, 1]);
        using var second = new OwnedStream(secondBytes, copy: async (destination, token) =>
        {
            await destination.WriteAsync(secondBytes.AsMemory(0, 1), token);
            copiedBytes++;
            copyStarted.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token).WaitAsync(TimeSpan.FromSeconds(10));
        });
        using var third = new OwnedStream([4]);
        var files = new List<IFormFile>
        {
            File(first, "first.bin"), File(second, "second.bin"), File(third, "third.bin"),
        };
        var service = new Mock<INotificationService>(MockBehavior.Strict);
        var controller = new EmailsController(service.Object);
        var sending = SendAsync(controller, files, cancellation.Token);
        try
        {
            await copyStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, copiedBytes);
            Assert.Equal(1, first.DisposalCount);
            Assert.Equal(0, second.DisposalCount);
            cancellation.Cancel();

            var observed = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);

            Assert.Equal(cancellation.Token, observed.CancellationToken);
            Assert.Equal(1, first.DisposalCount);
            Assert.Equal(1, second.DisposalCount);
            Assert.Equal(0, third.DisposalCount);
            Mock.Get(files[2]).Verify(value => value.OpenReadStream(), Times.Never);
            service.Verify(value => value.SendAsync(It.IsAny<EmailChannel>(),
                It.IsAny<NotificationSendRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            cancellation.Cancel();
            try { await sending; }
            catch (OperationCanceledException) { }
        }
    }

    private static Task<ActionResult> SendAsync(EmailsController controller, List<IFormFile> files,
        CancellationToken cancellationToken = default) => controller.SendInfoEmailAsync(
        "recipient@example.invalid", "synthetic subject", "synthetic body", null, null, null,
        files, cancellationToken);

    private static IFormFile File(OwnedStream stream, string name)
    {
        var file = new Mock<IFormFile>(MockBehavior.Strict);
        file.SetupGet(value => value.Length).Returns(stream.Length);
        file.SetupGet(value => value.FileName).Returns(name);
        file.SetupGet(value => value.ContentType).Returns("application/octet-stream");
        file.Setup(value => value.OpenReadStream()).Returns(stream);
        return file.Object;
    }

    private sealed class OwnedStream(byte[] bytes, bool failCopy = false,
        Func<Stream, CancellationToken, Task>? copy = null) : MemoryStream(bytes, writable: false)
    {
        public int DisposalCount { get; private set; }

        public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            if (copy is not null) return copy(destination, cancellationToken);
            if (failCopy) return Task.FromException(new IOException("synthetic attachment copy failure"));
            return base.CopyToAsync(destination, bufferSize, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) DisposalCount++;
            base.Dispose(disposing);
        }
    }
}
