using System.Globalization;
using Legacy.Maliev.NotificationService.Application.Interfaces;
using Legacy.Maliev.NotificationService.Application.Services;
using Legacy.Maliev.NotificationService.Domain;

namespace Legacy.Maliev.NotificationService.Tests.Application;

public sealed class NotificationIntentBindingTests
{
    private static NotificationIntentBinding Binding() => new("key-a", new Dictionary<string, byte[]>
    {
        ["key-a"] = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray(),
        ["key-b"] = Enumerable.Range(32, 32).Select(value => (byte)value).ToArray(),
    });

    private static NotificationSendRequest Payload() => new() { To = "recipient@example.invalid", Subject = "Invoice", Body = "ใบแจ้งหนี้" };

    private static DeliveryIntentIdentity Identity() => new("https://iam.maliev.com", "service:legacy-accounting",
        Guid.Parse("fcdac787-947f-43fc-8022-f7be910b77e3"), "invoice-issued", "invoice", 42,
        Guid.Parse("d66d56b7-3c19-46e5-b2df-cb9077463521"), EmailChannel.Info);

    [Fact]
    public void IndependentLiteralFrameAndHmac_ThaiNullArrays_Vector()
    {
        // Independent Python stdlib reference: explicit tag/NUL, marker, uint64-BE framing;
        // 197 payload bytes, hashlib.sha256 and hmac.new(bytes(range(32)), ..., sha256).
        const string digest = "9c4420bb6e625b32fe65c1397af1b939cabe10963cf440cf8e69fffe7e28307a";
        Assert.Equal(digest, Binding().PayloadDigest(EmailChannel.Info, Payload()));
        Assert.Equal("093d80f6cfa026c7f01437ab2ea56f55af3a8e5c8710cee5108669aa6040eb11",
            Binding().ComputeBinding(Identity(), "notification-payload-v1", digest, "key-a"));
        Assert.Equal("f7400836c61b5f42078181e2b3ff29182aac7581b1fb35615918672baf22708f",
            Binding().PayloadDigest(EmailChannel.Info, Payload() with { Cc = [] }));
        Assert.Equal("00f0207ca1a1b87dc7ce01892a090c80268dfcda1ac0398889a8fe3a08fe7ab7",
            Binding().PayloadDigest(EmailChannel.Info, Payload() with { Cc = ["a@example.invalid", "b@example.invalid"] }));
        Assert.Equal("df4ac6912fa03e4afc846e33ae2082b005ddf2e6a7f733b618ec860789ef95bb",
            Binding().PayloadDigest(EmailChannel.Info, Payload() with { Cc = ["b@example.invalid", "a@example.invalid"] }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnpairedSurrogate_IsFixedValidationFailure_NotRawParserDetails(bool identityField)
    {
        var error = identityField
            ? Assert.Throws<ArgumentException>(() => Binding().ComputeBinding(Identity() with { Issuer = "\ud800" },
                NotificationIntentBinding.PayloadVersion, new string('a', 64), "key-a"))
            : Assert.Throws<ArgumentException>(() => Binding().PayloadDigest(EmailChannel.Info, Payload() with { Body = "\ud800" }));
        Assert.Equal("Invalid notification text encoding.", error.Message);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public void OptionalArrays_NullEmptyAndOrder_AreDistinct()
    {
        var binding = Binding();
        var absent = binding.PayloadDigest(EmailChannel.Info, Payload());
        Assert.NotEqual(absent, binding.PayloadDigest(EmailChannel.Info, Payload() with { Cc = [] }));
        Assert.NotEqual(absent, binding.PayloadDigest(EmailChannel.Info, Payload() with { Attachments = [] }));
        Assert.NotEqual(binding.PayloadDigest(EmailChannel.Info, Payload() with { Cc = ["a@example.invalid", "b@example.invalid"] }),
            binding.PayloadDigest(EmailChannel.Info, Payload() with { Cc = ["b@example.invalid", "a@example.invalid"] }));
    }

    [Fact]
    public void AttachmentBytesAndMetadata_AreBoundWithoutDelimiterCollisions()
    {
        var binding = Binding();
        var payload = Payload() with { Attachments = [new("invoice.pdf", "application/pdf", [1, 2])] };
        Assert.NotEqual(binding.PayloadDigest(EmailChannel.Info, payload),
            binding.PayloadDigest(EmailChannel.Info, payload with { Attachments = [new("invoice.pdf", "application/pdf", [1, 3])] }));
        Assert.NotEqual(binding.PayloadDigest(EmailChannel.Info, Payload() with { Subject = "ab", Body = "c" }),
            binding.PayloadDigest(EmailChannel.Info, Payload() with { Subject = "a", Body = "bc" }));
    }

    [Fact]
    public void EveryTupleField_AndRetainedKeyIdentity_AffectsBinding()
    {
        var binding = Binding();
        var digest = binding.PayloadDigest(EmailChannel.Info, Payload());
        var original = binding.ComputeBinding(Identity(), NotificationIntentBinding.PayloadVersion, digest, "key-a");
        Assert.NotEqual(original, binding.ComputeBinding(Identity() with { ResourceId = 43 }, NotificationIntentBinding.PayloadVersion, digest, "key-a"));
        Assert.NotEqual(original, binding.ComputeBinding(Identity() with { IntentId = Guid.Parse("bbf7e6a6-073f-4b57-9c8d-663f1ac1f11d") }, NotificationIntentBinding.PayloadVersion, digest, "key-a"));
        Assert.NotEqual(original, binding.ComputeBinding(Identity(), NotificationIntentBinding.PayloadVersion, digest, "key-b"));
        Assert.NotEqual(original, binding.ComputeBinding(Identity() with { Issuer = "https://other.example.invalid" }, NotificationIntentBinding.PayloadVersion, digest, "key-a"));
        Assert.NotEqual(original, binding.ComputeBinding(Identity() with { WorkflowOperationId = Guid.Parse("1bcb1cd4-8187-48b4-888d-1c3063a89aef") }, NotificationIntentBinding.PayloadVersion, digest, "key-a"));
        Assert.NotEqual(original, binding.ComputeBinding(Identity() with { Channel = EmailChannel.Support }, NotificationIntentBinding.PayloadVersion, digest, "key-a"));
        Assert.Throws<ArgumentException>(() => binding.ComputeBinding(Identity() with { ServiceSubject = "service:other" }, NotificationIntentBinding.PayloadVersion, digest, "key-a"));
        Assert.Throws<ArgumentException>(() => binding.ComputeBinding(Identity() with { Purpose = "other-purpose" }, NotificationIntentBinding.PayloadVersion, digest, "key-a"));
        Assert.Throws<ArgumentException>(() => binding.ComputeBinding(Identity() with { ResourceType = "other-resource" }, NotificationIntentBinding.PayloadVersion, digest, "key-a"));
    }

    [Fact]
    public void MissingRetainedKey_IsUnavailable_NotAnEmptyBinding()
    {
        Assert.Throws<DeliveryIntentUnavailableException>(() => Binding().ComputeBinding(Identity(), NotificationIntentBinding.PayloadVersion, new string('a', 64), "retired-missing"));
    }

    [Fact]
    public void CultureAndThaiBytes_AreStable_NotNormalized()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("th-TH");
            var thai = Binding().PayloadDigest(EmailChannel.Info, Payload());
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            Assert.Equal(thai, Binding().PayloadDigest(EmailChannel.Info, Payload()));
            Assert.Matches("^[0-9a-f]{64}$", thai);
            Assert.NotEqual(thai, Binding().PayloadDigest(EmailChannel.Info, Payload() with { Body = "ใบแจ้งหนี้ " }));
        }
        finally { CultureInfo.CurrentCulture = originalCulture; }
    }
}
