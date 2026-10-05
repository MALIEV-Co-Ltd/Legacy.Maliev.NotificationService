using System.Globalization;
using System.Net;
using System.Text.Json;
using Legacy.Maliev.NotificationService.Application.Interfaces;
using Legacy.Maliev.NotificationService.Data;
using Legacy.Maliev.NotificationService.Domain;
using Legacy.Maliev.NotificationService.Tests.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using IntentFixture = Legacy.Maliev.AccountingService.Tests.InvoiceNotificationIntentAcceptanceTests.IntentFixture;

namespace Legacy.Maliev.NotificationService.Tests.Controllers;

// Both actual Programs, normal JWT/delegation/live-IAM middleware and real PG
// stores. Signed fixture actors and controlled remote transports are not a live
// AuthService exchange or recipient-delivery claim.
public sealed class AccountingNotificationJoinedHttpTests(DeliveryIntentPostgresFixture postgres)
    : IClassFixture<DeliveryIntentPostgresFixture>
{
    [Fact]
    public async Task AcceptedProducerReceipt_ReplaysAfterRestartWithoutAnyNotificationOrFinancialRequest()
    {
        await using var joined = await JoinedBoundary.CreateAsync(postgres);
        using var created = await joined.CreateAsync();
        var first = await AcceptedAsync(created);
        Assert.Equal(new[] { "PUT", "POST" }, joined.Methods);
        Assert.Equal(1, joined.Producer.ProviderCalls);
        Assert.Equal(0, joined.Producer.LegacyProviderCalls);
        var payload = joined.Producer.ProviderPayload!.Value;
        Assert.Equal("recipient@example.invalid", payload.GetProperty("to")[0].GetProperty("email").GetString());
        Assert.Equal("Invoice for your quotation [Invoice no. INV-intent]", payload.GetProperty("subject").GetString());
        Assert.Equal("mail-tracking@maliev.com", payload.GetProperty("bcc")[0].GetProperty("email").GetString());
        var attachment = Assert.Single(payload.GetProperty("attachment").EnumerateArray());
        Assert.Equal("invoice_INV-intent.pdf", attachment.GetProperty("name").GetString());
        Assert.Equal(new byte[] { 1, 2, 3 }, attachment.GetProperty("content").GetBytesFromBase64());
        var downstream = joined.Accounting.DownstreamCalls;
        await joined.Accounting.RestartHostAsync();
        using var replay = await joined.CreateAsync();
        Assert.Equal(first, await AcceptedAsync(replay));
        Assert.Equal(downstream, joined.Accounting.DownstreamCalls);
        Assert.Equal(new[] { "PUT", "POST" }, joined.Methods);
        Assert.Equal(2, joined.Accounting.LiveChecks);
        await joined.AssertDurableAsync("ProviderAccepted", "Completed", DeliveryIntentState.ProviderAccepted);
    }

    [Theory]
    [InlineData("deny")]
    [InlineData("unavailable")]
    public async Task LostCallerResponse_RealControllerReconcilesByGetOnlyAndTerminalReplayMakesNoRequest(string failedRead)
    {
        await using var joined = await JoinedBoundary.CreateAsync(postgres, loseResponse: true);
        joined.FailedReadAfterResponseLoss = failedRead;
        using var created = await joined.CreateAsync();
        await UncertainAsync(created);
        Assert.Equal(new[] { "PUT", "POST", "GET" }, joined.Methods);
        Assert.Equal(1, joined.Producer.ProviderCalls);
        var financial = joined.Accounting.DownstreamCalls - joined.Accounting.NotificationCalls;
        await joined.AssertDurableAsync("ExecutionIssued", "NeedsReconciliation", DeliveryIntentState.ProviderAccepted);

        using var stillUnknown = await joined.CreateAsync();
        await UncertainAsync(stillUnknown);
        Assert.Equal(financial, joined.Accounting.DownstreamCalls - joined.Accounting.NotificationCalls);
        Assert.Equal(new[] { "PUT", "POST", "GET", "GET" }, joined.Methods);
        joined.Producer.IamMode = "allow";
        using var recovered = await joined.CreateAsync();
        var invoice = await AcceptedAsync(recovered);
        Assert.Equal(financial, joined.Accounting.DownstreamCalls - joined.Accounting.NotificationCalls);
        Assert.Equal(new[] { "PUT", "POST", "GET", "GET", "GET" }, joined.Methods);
        await joined.AssertDurableAsync("ProviderAccepted", "Completed", DeliveryIntentState.ProviderAccepted);

        var requests = joined.Accounting.NotificationCalls;
        await joined.Accounting.RestartHostAsync();
        using var replay = await joined.CreateAsync();
        Assert.Equal(invoice, await AcceptedAsync(replay));
        Assert.Equal(requests, joined.Accounting.NotificationCalls);
        Assert.Equal(financial, joined.Accounting.DownstreamCalls - joined.Accounting.NotificationCalls);
        Assert.Equal(1, joined.Producer.ProviderCalls);
        Assert.Equal(0, joined.Producer.LegacyProviderCalls);
        Assert.Equal(4, joined.Accounting.LiveChecks);
    }

    [Fact]
    public async Task ProviderAcknowledgmentUnknown_ConsumerCannotResubmitOnReadOnlyRecovery()
    {
        await using var joined = await JoinedBoundary.CreateAsync(postgres);
        joined.Producer.LoseProviderAcknowledgment = true;
        using var created = await joined.CreateAsync();
        await UncertainAsync(created);
        var financial = joined.Accounting.DownstreamCalls - joined.Accounting.NotificationCalls;
        using var replay = await joined.CreateAsync();
        await UncertainAsync(replay);
        Assert.Equal(financial, joined.Accounting.DownstreamCalls - joined.Accounting.NotificationCalls);
        Assert.Equal(new[] { "PUT", "POST", "GET" }, joined.Methods);
        Assert.Equal(1, joined.Producer.ProviderCalls);
        await joined.AssertDurableAsync("OutcomeUnknown", "NeedsReconciliation", DeliveryIntentState.OutcomeUnknown);
    }

    [Fact]
    public async Task ExecutionDenied_AnAdmittedReadNeverRestoresMutationCapability()
    {
        await using var joined = await JoinedBoundary.CreateAsync(postgres);
        joined.DenyExecution = true;
        using var created = await joined.CreateAsync();
        await UncertainAsync(created);
        Assert.Equal(0, joined.Producer.ProviderCalls);
        joined.DenyExecution = false;
        joined.Producer.IamMode = "allow";
        var financial = joined.Accounting.DownstreamCalls - joined.Accounting.NotificationCalls;
        using var repeat = await joined.CreateAsync();
        await UncertainAsync(repeat);
        Assert.Equal(financial, joined.Accounting.DownstreamCalls - joined.Accounting.NotificationCalls);
        Assert.Equal(new[] { "PUT", "POST", "GET", "GET" }, joined.Methods);
        Assert.Equal(0, joined.Producer.ProviderCalls);
        await joined.AssertDurableAsync("ExecutionIssued", "NeedsReconciliation", DeliveryIntentState.Admitted);
    }

    [Theory]
    [InlineData(false, "service")]
    [InlineData(true, "other-service")]
    public async Task DisabledProducerOrWrongSignedService_NeverFallsBackOrReissuesAdmission(bool producerEnabled, string actor)
    {
        await using var joined = await JoinedBoundary.CreateAsync(postgres, producerEnabled: producerEnabled, actor: actor);
        using var created = await joined.CreateAsync();
        await UncertainAsync(created);
        var financial = joined.Accounting.DownstreamCalls - joined.Accounting.NotificationCalls;
        using var repeat = await joined.CreateAsync();
        await UncertainAsync(repeat);
        Assert.Equal(financial, joined.Accounting.DownstreamCalls - joined.Accounting.NotificationCalls);
        Assert.Equal(new[] { "PUT", "GET", "GET" }, joined.Methods);
        Assert.Equal(0, joined.Producer.ProviderCalls);
        Assert.Equal(0, joined.Producer.LegacyProviderCalls);
        await using var database = joined.Accounting.Database();
        Assert.Equal("AdmissionIssued", (await database.InvoiceNotificationCorrelations.AsNoTracking().SingleAsync()).Phase);
        Assert.Equal("NeedsReconciliation", (await database.InvoiceCreationAdmissions.AsNoTracking().SingleAsync()).State);
        Assert.Single(await database.Invoices.AsNoTracking().ToListAsync());
        Assert.Single(await database.Files.AsNoTracking().ToListAsync());
    }

    private static async Task<int> AcceptedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(3, body.RootElement.GetProperty("EmailState").GetInt32());
        Assert.Equal("controlled-provider-1", body.RootElement.GetProperty("ProviderMessageId").GetString());
        return body.RootElement.GetProperty("InvoiceId").GetInt32();
    }

    private static async Task UncertainAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(2, body.RootElement.GetProperty("EmailState").GetInt32());
        Assert.False(body.RootElement.TryGetProperty("ProviderMessageId", out _));
    }

    private sealed class JoinedBoundary(IntentV2Factory producer, IntentFixture accounting,
        NotificationCallerResponseLossHandler loss, HttpMessageInvoker transport) : IAsyncDisposable
    {
        private const string Issuer = "https://accounting-intent.invalid";
        public IntentV2Factory Producer { get; } = producer;
        public IntentFixture Accounting { get; } = accounting;
        public string? FailedReadAfterResponseLoss { get; set; }
        public bool DenyExecution { get; set; }
        public string[] Methods => loss.Requests.Select(value => value.Method).ToArray();

        public static async Task<JoinedBoundary> CreateAsync(DeliveryIntentPostgresFixture postgres, bool loseResponse = false,
            bool producerEnabled = true, string actor = "service")
        {
            var producer = await IntentV2Factory.CreateAsync(postgres, enabled: producerEnabled);
            producer.JwtIssuer = Issuer;
            producer.ExpectedBody = "<div>Hello Synthetic Customer,</div><div>&nbsp;</div>" +
                "<div>Thank you for accepting our quoted amount.</div>" +
                "<div>You'll find the payable invoice for your orders attached with this email.</div><div>&nbsp;</div>" +
                "<div>The production of your orders will start as soon as the payable amount is received.</div>" +
                "<div>&nbsp;</div><div>Best regards,</div><div>Maliev Co., Ltd.</div>";
            IntentFixture? accounting = null;
            try
            {
                using var tokenClient = producer.Client(actor);
                accounting = await IntentFixture.StartAsync();
                accounting.EnableNotificationV2 = true;
                accounting.WorkloadToken = tokenClient.DefaultRequestHeaders.Authorization!.Parameter!;
                var loss = new NotificationCallerResponseLossHandler(producer.Server.CreateHandler(), null, loseResponse);
                var joined = new JoinedBoundary(producer, accounting, loss, new HttpMessageInvoker(loss));
                accounting.NotificationV2Transport = joined.ForwardAsync;
                return joined;
            }
            catch
            {
                if (accounting is not null) await accounting.DisposeAsync();
                await producer.DisposeAsync();
                throw;
            }
        }

        public Task<HttpResponseMessage> CreateAsync() => Accounting.CreateAsync(delegation: Accounting.Delegation());

        private async Task<HttpResponseMessage> ForwardAsync(HttpRequestMessage request, CancellationToken token)
        {
            // Bind expectations to the actual acknowledged first writer, not the
            // request body or a newly invented test intent/permit.
            if (request.Method == HttpMethod.Put)
            {
                await using var database = Accounting.Database();
                var retained = await database.InvoiceNotificationCorrelations.AsNoTracking().SingleAsync(token);
                Assert.Equal("AdmissionIssued", retained.Phase);
                Assert.Equal(Accounting.Operation, retained.WorkflowOperationId);
                Assert.NotEqual(Accounting.Operation, retained.IntentId);
                Producer.ExpectedIntentId = retained.IntentId.ToString("D");
                Producer.ExpectedResourceId = retained.InvoiceId.ToString(CultureInfo.InvariantCulture);
                Assert.Equal("/notifications/v2/delivery-intents/" + Producer.ExpectedIntentId, request.RequestUri!.AbsolutePath);
            }
            if (request.Method == HttpMethod.Post && DenyExecution) Producer.IamMode = "deny";
            try { return await transport.SendAsync(request, token); }
            catch (HttpRequestException)
            {
                if (FailedReadAfterResponseLoss is not null) Producer.IamMode = FailedReadAfterResponseLoss;
                throw;
            }
        }

        public async Task AssertDurableAsync(string phase, string admissionState, DeliveryIntentState producerState)
        {
            Assert.IsAssignableFrom<WebApplicationFactory<AccountingProgram>>(Accounting.Host);
            await using var database = Accounting.Database();
            var invoice = Assert.Single(await database.Invoices.AsNoTracking().ToListAsync());
            var file = Assert.Single(await database.Files.AsNoTracking().ToListAsync());
            var retained = await database.InvoiceNotificationCorrelations.AsNoTracking().SingleAsync();
            Assert.Equal(invoice.Id, retained.InvoiceId);
            Assert.Equal(invoice.Id, file.InvoiceId);
            Assert.Equal(Accounting.Operation, retained.WorkflowOperationId);
            Assert.Equal(84, retained.QuotationId);
            Assert.Equal(phase, retained.Phase);
            Assert.Equal(admissionState, (await database.InvoiceCreationAdmissions.AsNoTracking().SingleAsync()).State);
            using var scope = Producer.Services.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IDeliveryIntentStore>();
            Assert.IsType<PostgresDeliveryIntentStore>(store);
            var record = await store.ReadAsync(Issuer, "service:legacy-accounting", retained.IntentId, CancellationToken.None);
            Assert.NotNull(record);
            Assert.Equal(producerState, record.State);
            Assert.Equal(retained.WorkflowOperationId, record.Identity.WorkflowOperationId);
            Assert.Equal(retained.InvoiceId, record.Identity.ResourceId);
            Assert.All(loss.Requests, request => Assert.StartsWith("/notifications/v2/delivery-intents/" + retained.IntentId.ToString("D"), request.Path));
        }

        public async ValueTask DisposeAsync()
        {
            transport.Dispose();
            await Accounting.DisposeAsync();
            await Producer.DisposeAsync();
        }
    }
}
