using Legacy.Maliev.NotificationService.Api.Controllers;
using Legacy.Maliev.NotificationService.Application.Interfaces;
using Legacy.Maliev.NotificationService.Application.Services;
using Legacy.Maliev.NotificationService.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Legacy.Maliev.NotificationService.Tests.Controllers;

public sealed class NotificationProviderAdmissionCancellationTests
{
    [Fact]
    public async Task AlreadyCancelledValidRequest_NeverEntersProvider()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var provider = new Mock<INotificationProvider>(MockBehavior.Strict);
        var service = Service(provider.Object);
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SendAsync(
            EmailChannel.Info,
            new NotificationSendRequest { To = "synthetic@example.invalid", Subject = "Synthetic", Body = "ทดสอบ" },
            cancellation.Token));
        Assert.Equal(cancellation.Token, failure.CancellationToken);
        provider.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(EmailChannel.Info)]
    [InlineData(EmailChannel.Manufacturing)]
    [InlineData(EmailChannel.NoReply)]
    [InlineData(EmailChannel.Support)]
    public async Task CancellationAfterLastAttachmentCopy_NeverEntersProviderAndDisposesSourceOnce(EmailChannel channel)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var provider = new Mock<INotificationProvider>(MockBehavior.Strict);
        var controller = new EmailsController(Service(provider.Object));
        var file = new CancellingFile(cancellation);
        var files = new List<IFormFile> { file };
        var send = channel switch
        {
            EmailChannel.Info => controller.SendInfoEmailAsync("synthetic@example.invalid", "Synthetic", "ทดสอบ", null, null, null, files, cancellation.Token),
            EmailChannel.Manufacturing => controller.SendManufacturingEmailAsync("synthetic@example.invalid", "Synthetic", "ทดสอบ", null, null, null, files, cancellation.Token),
            EmailChannel.NoReply => controller.SendNoReplyEmailAsync("synthetic@example.invalid", "Synthetic", "ทดสอบ", null, null, null, files, cancellation.Token),
            EmailChannel.Support => controller.SendSupportEmailAsync("synthetic@example.invalid", "Synthetic", "ทดสอบ", null, null, null, files, cancellation.Token),
            _ => throw new ArgumentOutOfRangeException(nameof(channel)),
        };
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.True(file.Source.CopyCompleted);
        Assert.Equal(1, file.Source.DisposalCount);
        Assert.Equal(1, file.OpenCount);
        provider.VerifyNoOtherCalls();
    }

    private static NotificationApplicationService Service(INotificationProvider provider) =>
        new(provider, NullLogger<NotificationApplicationService>.Instance);

    private sealed class CancellingFile(CancellationTokenSource cancellation) : IFormFile
    {
        public CancellingStream Source { get; } = new(cancellation);
        public int OpenCount { get; private set; }
        public string ContentType => "application/octet-stream";
        public string ContentDisposition => string.Empty;
        public IHeaderDictionary Headers { get; } = new HeaderDictionary();
        public long Length => 3;
        public string Name => "files";
        public string FileName => "synthetic.bin";
        public Stream OpenReadStream() { OpenCount++; return Source; }
        public void CopyTo(Stream target) => throw new NotSupportedException();
        public Task CopyToAsync(Stream target, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class CancellingStream(CancellationTokenSource cancellation) : MemoryStream([1, 2, 3])
    {
        public bool CopyCompleted { get; private set; }
        public int DisposalCount { get; private set; }
        public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            await base.CopyToAsync(destination, bufferSize, cancellationToken);
            CopyCompleted = true;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && CanRead)
            {
                DisposalCount++;
                // Deterministic boundary: copy succeeded, source release cancels
                // before the real application service may enter the provider.
                cancellation.Cancel();
            }
            base.Dispose(disposing);
        }
    }
}
