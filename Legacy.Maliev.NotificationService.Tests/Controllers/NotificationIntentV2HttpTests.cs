using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Legacy.Maliev.NotificationService.Application.Services;
using Legacy.Maliev.NotificationService.Data;
using Legacy.Maliev.NotificationService.Domain;
using Legacy.Maliev.NotificationService.Tests.Data;
using Maliev.Aspire.ServiceDefaults.LegacyAuth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using Npgsql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace Legacy.Maliev.NotificationService.Tests.Controllers;

// Real Production RS256, runtime DI, PG18 store and IAM client; remote HTTP only is controlled.
public sealed class NotificationIntentV2HttpTests(DeliveryIntentPostgresFixture postgres) : IClassFixture<DeliveryIntentPostgresFixture>
{
    [Fact]
    public async Task AdmitExecuteReplayAndRead_PreserveOneEffect_AndRequireFreshResourceChecks()
    {
        await using var factory = await IntentV2Factory.CreateAsync(postgres);
        using var client = factory.Client();
        using var admit = await factory.AdmitAsync(client);
        Assert.Equal(HttpStatusCode.OK, admit.StatusCode);
        Assert.Equal(0, factory.ProviderCalls);
        using var first = await factory.ExecuteAsync(client);
        using var replay = await factory.ExecuteAsync(client);
        using var read = await client.GetAsync(IntentV2Factory.Path);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var receipt = await read.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("providerAccepted", receipt.GetProperty("state").GetString());
        Assert.Equal("controlled-provider-1", receipt.GetProperty("providerMessageId").GetString());
        Assert.Equal(3, receipt.GetProperty("version").GetInt64());
        Assert.False(receipt.TryGetProperty("binding", out _));
        Assert.False(receipt.TryGetProperty("keyId", out _));
        Assert.Equal(1, factory.ProviderCalls);
        Assert.Equal(["admit", "execute", "execute", "read"], factory.IamOperations);
        Assert.Equal(1, factory.AuthCalls);
    }

    [Fact]
    public async Task ProviderLostAcknowledgment_Is202ReadbackUnknown_AndNeverResubmits()
    {
        await using var factory = await IntentV2Factory.CreateAsync(postgres);
        factory.LoseProviderAcknowledgment = true;
        using var client = factory.Client();
        using var admit = await factory.AdmitAsync(client);
        Assert.Equal(HttpStatusCode.OK, admit.StatusCode);
        using var first = await factory.ExecuteAsync(client);
        using var replay = await factory.ExecuteAsync(client);
        using var read = await client.GetAsync(IntentV2Factory.Path);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        Assert.Equal("outcomeUnknown", (await read.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString());
        Assert.Equal(1, factory.ProviderCalls);
    }

    [Theory]
    [InlineData(307)]
    [InlineData(308)]
    public async Task ProviderRedirect_IsUnknown_NoSecondRequestOrAutomaticResubmission(int status)
    {
        await using var factory = await IntentV2Factory.CreateAsync(postgres);
        factory.ProviderStatus = (HttpStatusCode)status;
        using var client = factory.Client();
        using var admit = await factory.AdmitAsync(client);
        Assert.Equal(HttpStatusCode.OK, admit.StatusCode);
        using var execute = await factory.ExecuteAsync(client);
        using var retry = await factory.ExecuteAsync(client);
        Assert.Equal(HttpStatusCode.Accepted, execute.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        Assert.Equal("outcomeUnknown", (await execute.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString());
        Assert.Equal(1, factory.ProviderCalls);
    }

    [Fact]
    public async Task ProductionNamedProviderPrimary_IsActuallyRedirectDisabled_NotOnlyControlledTransport()
    {
        await using var factory = await IntentV2Factory.CreateAsync(postgres, controlledProvider: false);
        using var client = factory.Client();
        var handler = factory.Services.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(BrevoDeliveryIntentSubmission.ClientName);
        while (handler is DelegatingHandler delegating) handler = delegating.InnerHandler!;
        Assert.False(Assert.IsType<SocketsHttpHandler>(handler).AllowAutoRedirect);
        Assert.Equal(0, factory.ProviderCalls);
    }

    [Theory]
    [InlineData("IAMService")]
    [InlineData("LegacyAuthServiceTokenExchange")]
    public async Task NamedAuthorityPrimary_DoesNotRedirectWorkloadOrLiveCredentials(string name)
    {
        await using var factory = await IntentV2Factory.CreateAsync(postgres, controlledAuthorities: false);
        using var client = factory.Client();
        var handler = factory.Services.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(name);
        while (handler is DelegatingHandler delegating) handler = delegating.InnerHandler!;
        Assert.False(Assert.IsType<SocketsHttpHandler>(handler).AllowAutoRedirect);
        Assert.Equal(0, factory.AuthCalls);
        Assert.Empty(factory.IamOperations);
    }

    [Fact]
    public async Task ChangedPayloadAndDuplicateBusinessEffect_ConflictWithoutAnotherSubmission()
    {
        await using var factory = await IntentV2Factory.CreateAsync(postgres);
        using var client = factory.Client();
        using var admit = await factory.AdmitAsync(client);
        Assert.Equal(HttpStatusCode.OK, admit.StatusCode);
        using var first = await factory.ExecuteAsync(client);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var changed = await factory.ExecuteAsync(client, "changed synthetic invoice");
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        using var duplicate = await factory.AdmitAsync(client, Guid.NewGuid().ToString("D"));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal(1, factory.ProviderCalls);
    }

    [Theory]
    [InlineData("deny")]
    [InlineData("malformed")]
    [InlineData("unavailable")]
    public async Task RevokedOrUnavailableIam_DeniesExecuteAndRead_DespitePriorAllowAndWildcard(string mode)
    {
        await using var factory = await IntentV2Factory.CreateAsync(postgres);
        using var client = factory.Client();
        using var admitted = await factory.AdmitAsync(client);
        Assert.Equal(HttpStatusCode.OK, admitted.StatusCode);
        factory.IamMode = mode;
        using var execute = await factory.ExecuteAsync(client);
        using var read = await client.GetAsync(IntentV2Factory.Path);
        Assert.Equal(HttpStatusCode.Forbidden, execute.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        Assert.Equal(0, factory.ProviderCalls);
        Assert.Equal(["admit", "execute", "read"], factory.IamOperations);
    }

    [Theory]
    [InlineData("employee", 403)]
    [InlineData("other-service", 403)]
    [InlineData("duplicate-kind", 403)]
    [InlineData("duplicate-sub", 401)]
    public async Task InvalidServiceIdentity_DeniesBeforeIamOrPersistence(string identity, int expectedStatus)
    {
        await using var factory = await IntentV2Factory.CreateAsync(postgres);
        using var client = factory.Client(identity);
        using var response = await factory.AdmitAsync(client);
        Assert.Equal(expectedStatus, (int)response.StatusCode);
        Assert.Empty(factory.IamOperations);
        Assert.Equal(0, factory.ProviderCalls);
        await using var database = await factory.Database.CreateDbContextAsync();
        Assert.Equal(0, await database.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM public.\"NotificationDeliveryIntent\"").SingleAsync());
    }

    [Fact]
    public async Task DefaultOff_Is503_NoAuthorityOrRemoteCalls()
    {
        await using var factory = await IntentV2Factory.CreateAsync(postgres, enabled: false);
        using var client = factory.Client();
        using var response = await factory.AdmitAsync(client);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Empty(factory.IamOperations);
        Assert.Equal(0, factory.ProviderCalls);
    }

    [Fact]
    public async Task V2Registration_DoesNotBroadenV1PermissionAuthority_OrContactLegacyProvider()
    {
        await using var factory = await IntentV2Factory.CreateAsync(postgres);
        factory.IamMode = "legacy-only";
        using var client = factory.Client(); // Wildcard is not the pre-existing exact V1 send grant.
        using var response = await client.PostAsJsonAsync("/notifications/v1/email/Info",
            new { to = "recipient@example.invalid", subject = "Invoice fixture", body = "synthetic invoice" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(factory.IamOperations);
        Assert.Equal(0, factory.LegacyProviderCalls);
    }

    [Theory]
    [InlineData("http://iam-fixture.invalid", "https://auth-fixture.invalid")]
    [InlineData("https://iam-fixture.invalid", "http://auth-fixture.invalid")]
    [InlineData(" https://iam-fixture.invalid", "https://auth-fixture.invalid")]
    [InlineData("https://iam-fixture.invalid", "https://auth-fixture.invalid/other")]
    public async Task NoncanonicalOrInsecureAuthorityOrigin_RefusesStartup_NoRemoteEffects(string iam, string auth)
    {
        await using var factory = await IntentV2Factory.CreateAsync(postgres);
        factory.IamOrigin = iam;
        factory.AuthOrigin = auth;
        Assert.Throws<Legacy.Maliev.NotificationService.Application.Interfaces.DeliveryIntentUnavailableException>(() => factory.Client());
        Assert.Empty(factory.IamOperations);
        Assert.Equal(0, factory.AuthCalls);
        Assert.Equal(0, factory.ProviderCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegisteredRetryStrategy_CommitAcknowledgmentLost_CannotForwardUncommittedFenceOrResendAccepted(bool acceptance)
    {
        await using var factory = await IntentV2Factory.CreateAsync(postgres);
        var fault = new HttpCommitFault(acceptance ? "ProviderAccepted" : "Submitting");
        factory.DatabaseInterceptors = [fault.Command, fault.Transaction];
        using var client = factory.Client();
        await using (var actual = await factory.Services.GetRequiredService<IDbContextFactory<DeliveryIntentDbContext>>().CreateDbContextAsync())
            Assert.True(actual.Database.CreateExecutionStrategy().RetriesOnFailure);
        using var admit = await factory.AdmitAsync(client);
        Assert.Equal(HttpStatusCode.OK, admit.StatusCode);
        fault.Armed = true;
        using var execute = await factory.ExecuteAsync(client);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, execute.StatusCode);
        Assert.Equal(1, fault.CommitAttempts);
        Assert.Equal(acceptance ? 1 : 0, factory.ProviderCalls);
        fault.Armed = false;
        using var read = await client.GetAsync(IntentV2Factory.Path);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(acceptance ? "providerAccepted" : "submitting", (await read.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString());
        using var replay = await factory.ExecuteAsync(client);
        Assert.Equal(acceptance ? HttpStatusCode.OK : HttpStatusCode.Accepted, replay.StatusCode);
        Assert.Equal(acceptance ? 1 : 0, factory.ProviderCalls);
    }

    [Fact]
    public async Task MissingPhysicalAuthority_RefusesStartup_WithoutAutomaticDdlOrRemoteEffects()
    {
        await using var factory = await IntentV2Factory.CreateAsync(postgres);
        await using var database = await factory.Database.CreateDbContextAsync();
        await database.Database.ExecuteSqlRawAsync("DROP TABLE public.\"NotificationDeliveryIntent\"");
        var error = Assert.Throws<InvalidOperationException>(() => factory.Client());
        Assert.Equal("Notification intent physical readiness is unavailable.", error.Message);
        Assert.Null(error.InnerException);
        Assert.Equal(0, factory.ProviderCalls);
        Assert.Empty(factory.IamOperations);
        Assert.Equal(0, await database.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM pg_class JOIN pg_namespace ON pg_namespace.oid=relnamespace WHERE nspname='public' AND relname='NotificationDeliveryIntent'").SingleAsync());
    }

    private sealed class HttpCommitFault(string target)
    {
        public bool Armed { get; set; }
        public bool MatchingUpdate { get; private set; }
        public int CommitAttempts { get; private set; }
        public DbCommandInterceptor Command => new CommandFault(this, target);
        public DbTransactionInterceptor Transaction => new TransactionFault(this);
        private sealed class CommandFault(HttpCommitFault owner, string target) : DbCommandInterceptor
        {
            public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
                InterceptionResult<int> result, CancellationToken cancellationToken = default)
            {
                if (owner.Armed && command.CommandText.TrimStart().StartsWith("UPDATE public.\"NotificationDeliveryIntent\"", StringComparison.Ordinal) &&
                    command.Parameters.Cast<DbParameter>().Any(parameter => parameter.Value is string value && value == target))
                    owner.MatchingUpdate = true;
                return ValueTask.FromResult(result);
            }
        }
        private sealed class TransactionFault(HttpCommitFault owner) : DbTransactionInterceptor
        {
            public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData,
                InterceptionResult result, CancellationToken cancellationToken = default)
            {
                if (owner.Armed && owner.MatchingUpdate) owner.CommitAttempts++;
                return ValueTask.FromResult(result);
            }
            public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
            {
                if (owner.Armed && owner.MatchingUpdate) throw new NpgsqlException("Controlled real HTTP COMMIT acknowledgment fault.", new IOException("Controlled I/O fault."));
                return Task.CompletedTask;
            }
        }
    }

    [Fact]
    public async Task ConcurrentHttpExecute_OnlyAcknowledgedWinnerReachesProvider_LoserSeesSubmitting()
    {
        await using var factory = await IntentV2Factory.CreateAsync(postgres);
        factory.BlockProvider = true;
        using var client = factory.Client();
        using var admit = await factory.AdmitAsync(client);
        Assert.Equal(HttpStatusCode.OK, admit.StatusCode);
        var first = factory.ExecuteAsync(client);
        await factory.ProviderEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var loser = await factory.ExecuteAsync(client);
        Assert.Equal(HttpStatusCode.Accepted, loser.StatusCode);
        Assert.Equal("submitting", (await loser.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString());
        Assert.Equal(1, factory.ProviderCalls);
        factory.ReleaseProvider.TrySetResult();
        using var winner = await first;
        Assert.Equal(HttpStatusCode.OK, winner.StatusCode);
        Assert.Equal(1, factory.ProviderCalls);
    }

    [Fact]
    public async Task KeyRotationAcrossActualHosts_RetainsBinding_AndMissingHistoricalKeyRefusesStartup()
    {
        await using var original = await IntentV2Factory.CreateAsync(postgres);
        using var client = original.Client();
        using var admit = await original.AdmitAsync(client);
        Assert.Equal(HttpStatusCode.OK, admit.StatusCode);
        var before = await original.Services.GetRequiredService<Legacy.Maliev.NotificationService.Application.Interfaces.IDeliveryIntentStore>()
            .ReadAsync("https://fixture.invalid", "service:legacy-accounting", Guid.Parse(IntentV2Factory.IntentId), default);
        await using var rotated = await IntentV2Factory.ReopenAsync(original, retainOldKey: true);
        using var rotatedClient = rotated.Client();
        using var replay = await rotated.AdmitAsync(rotatedClient);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var after = await rotated.Services.GetRequiredService<Legacy.Maliev.NotificationService.Application.Interfaces.IDeliveryIntentStore>()
            .ReadAsync("https://fixture.invalid", "service:legacy-accounting", Guid.Parse(IntentV2Factory.IntentId), default);
        Assert.Equal(before, after);
        Assert.Equal("fixture", after!.KeyId);
        await using var missing = await IntentV2Factory.ReopenAsync(original, retainOldKey: false);
        var error = Assert.Throws<InvalidOperationException>(() => missing.Client());
        Assert.Equal("Notification intent physical readiness is unavailable.", error.Message);
        Assert.Equal(0, missing.ProviderCalls);
        Assert.Empty(missing.IamOperations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnonymousOrMissingLiveCredential_CannotAdmitOrProduceAuthority(bool missingCredential)
    {
        await using var factory = await IntentV2Factory.CreateAsync(postgres);
        factory.IncludeLiveCredential = !missingCredential;
        using var client = missingCredential ? factory.Client() : factory.CreateClient();
        using var response = await factory.AdmitAsync(client);
        Assert.Equal(missingCredential ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(factory.IamOperations);
        Assert.Equal(0, factory.ProviderCalls);
        await using var database = await factory.Database.CreateDbContextAsync();
        Assert.Equal(0, await database.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM public.\"NotificationDeliveryIntent\"").SingleAsync());
    }

    [Fact]
    public async Task OptionalArrayPresentEmpty_DiffersFromAdmittedNull_BeforeProviderFence()
    {
        await using var factory = await IntentV2Factory.CreateAsync(postgres);
        using var client = factory.Client();
        using var admit = await factory.AdmitAsync(client);
        Assert.Equal(HttpStatusCode.OK, admit.StatusCode);
        using var changed = await client.PostAsJsonAsync(IntentV2Factory.Path + "/execute",
            new { channel = "Info", to = "recipient@example.invalid", subject = "Invoice fixture", body = "synthetic invoice", cc = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        Assert.Equal(0, factory.ProviderCalls);
        using var read = await client.GetAsync(IntentV2Factory.Path);
        var receipt = await read.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("admitted", receipt.GetProperty("state").GetString());
        Assert.Equal(1, receipt.GetProperty("version").GetInt64());
    }

    [Fact]
    public async Task InvalidCanonicalId_AndMalformedPayload_AreFixed400_NoProviderFence()
    {
        await using var factory = await IntentV2Factory.CreateAsync(postgres);
        using var client = factory.Client();
        using var uppercase = await factory.AdmitAsync(client, IntentV2Factory.IntentId.ToUpperInvariant());
        Assert.Equal(HttpStatusCode.BadRequest, uppercase.StatusCode);
        Assert.Equal("Invalid notification intent request", (await uppercase.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
        Assert.Empty(factory.IamOperations);
        using var admitted = await factory.AdmitAsync(client);
        Assert.Equal(HttpStatusCode.OK, admitted.StatusCode);
        using var malformed = await client.PostAsJsonAsync(IntentV2Factory.Path + "/execute",
            new { channel = "Info", to = "not-a-mailbox", subject = "Invoice fixture", body = "synthetic invoice" });
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Equal("Invalid notification intent request", (await malformed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
        Assert.Equal(0, factory.ProviderCalls);
    }

    [Fact]
    public async Task NonProductionOpenApi_DefaultOff_DoesNotPublishV2Operations()
    {
        await using var factory = await IntentV2Factory.CreateAsync(postgres, enabled: false);
        factory.HostEnvironment = "Development";
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/emails/openapi/v2.json");
        if (response.StatusCode == HttpStatusCode.NotFound) return;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var document = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.DoesNotContain(document.GetProperty("paths").EnumerateObject(), path => path.Name.Contains("delivery-intents", StringComparison.Ordinal));
        Assert.Equal(0, factory.ProviderCalls);
        Assert.Empty(factory.IamOperations);
    }

    [Fact]
    public async Task NonProductionOpenApi_ExplicitOn_MatchesLiteralCamelCaseRequestAndReceiptContract()
    {
        await using var factory = await IntentV2Factory.CreateAsync(postgres);
        factory.HostEnvironment = "Development";
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/emails/openapi/v2.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var document = await response.Content.ReadFromJsonAsync<JsonElement>();
        var schemas = document.GetProperty("components").GetProperty("schemas");
        static string[] Properties(JsonElement schema) => schema.GetProperty("properties").EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "channel", "payloadDigest", "payloadDigestVersion", "purpose", "resourceId", "resourceType", "workflowOperationId" }, Properties(schemas.GetProperty("DeliveryIntentAdmissionRequest")));
        Assert.Equal(new[] { "attachments", "bcc", "body", "cc", "channel", "replyTo", "subject", "to" }, Properties(schemas.GetProperty("DeliveryIntentExecuteRequest")));
        Assert.True(schemas.TryGetProperty("DeliveryIntentResponse", out var receipt), "The V2 response schema is required for compatible producer readback.");
        Assert.Equal(new[] { "admittedAt", "intentId", "providerMessageId", "purpose", "resourceId", "resourceType", "state", "updatedAt", "version", "workflowOperationId" }, Properties(receipt));
        var paths = document.GetProperty("paths");
        Assert.True(paths.GetProperty("/notifications/v2/delivery-intents/{intentId}").TryGetProperty("put", out _));
        Assert.True(paths.GetProperty("/notifications/v2/delivery-intents/{intentId}").TryGetProperty("get", out _));
        Assert.True(paths.GetProperty("/notifications/v2/delivery-intents/{intentId}/execute").TryGetProperty("post", out _));
        Assert.Equal(0, factory.ProviderCalls);
        Assert.Empty(factory.IamOperations);
    }

    [Fact]
    public async Task ProductionOpenApi_RemainsUnavailable_ForBothVersions()
    {
        await using var factory = await IntentV2Factory.CreateAsync(postgres);
        using var client = factory.CreateClient();
        using var v1 = await client.GetAsync("/emails/openapi/v1.json");
        using var v2 = await client.GetAsync("/emails/openapi/v2.json");
        Assert.Equal(HttpStatusCode.NotFound, v1.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, v2.StatusCode);
    }
}

public sealed class IntentV2Factory : WebApplicationFactory<Program>
{
    public const string IntentId = "fcdac787-947f-43fc-8022-f7be910b77e3";
    public const string WorkflowId = "d66d56b7-3c19-46e5-b2df-cb9077463521";
    public const string Path = "/notifications/v2/delivery-intents/" + IntentId;
    private const string Issuer = "https://fixture.invalid";
    private const string Audience = "notification-fixture";
    private const string WorkloadToken = "synthetic-notification-workload-token";
    private const string LiveCredential = "synthetic-notification-live-check";
    private readonly RSA signingKey = RSA.Create(2048);
    private readonly string connection;
    private readonly bool enabled;
    private readonly bool controlledProvider;
    private readonly bool controlledAuthorities;
    public IntentContextFactory Database { get; }
    public bool LoseProviderAcknowledgment { get; set; }
    public string IamMode { get; set; } = "allow";
    public List<string> IamOperations { get; } = [];
    private int providerCalls;
    public int ProviderCalls => Volatile.Read(ref providerCalls);
    public int AuthCalls { get; private set; }
    public int LegacyProviderCalls { get; private set; }
    public HttpStatusCode ProviderStatus { get; set; } = HttpStatusCode.Created;
    public string IamOrigin { get; set; } = "https://iam-fixture.invalid";
    public string AuthOrigin { get; set; } = "https://auth-fixture.invalid";
    public IInterceptor[] DatabaseInterceptors { get; set; } = [];
    public string ActiveKeyId { get; set; } = "fixture";
    public bool RetainOldKey { get; set; } = true;
    public bool IncludeLiveCredential { get; set; } = true;
    public string HostEnvironment { get; set; } = "Production";
    public bool BlockProvider { get; set; }
    public TaskCompletionSource ProviderEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseProvider { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private IntentV2Factory(IntentContextFactory database, string connection, bool enabled, bool controlledProvider, bool controlledAuthorities)
    { Database = database; this.connection = connection; this.enabled = enabled; this.controlledProvider = controlledProvider; this.controlledAuthorities = controlledAuthorities; }

    public static async Task<IntentV2Factory> CreateAsync(DeliveryIntentPostgresFixture postgres, bool enabled = true, bool controlledProvider = true, bool controlledAuthorities = true)
    {
        var database = await postgres.FactoryAsync();
        await using var context = await database.CreateDbContextAsync();
        return new(database, context.Database.GetConnectionString()!, enabled, controlledProvider, controlledAuthorities);
    }

    public static async Task<IntentV2Factory> ReopenAsync(IntentV2Factory original, bool retainOldKey)
    {
        await using var context = await original.Database.CreateDbContextAsync();
        return new(original.Database, context.Database.GetConnectionString()!, true, true, true) { ActiveKeyId = "rotated", RetainOldKey = retainOldKey };
    }

    public HttpClient Client(string identity = "service")
    {
        var client = CreateClient();
        var claims = new List<Claim>
        {
            new("sub", identity == "other-service" ? "service:other" : "service:legacy-accounting"),
            new("identity_kind", identity == "employee" ? "employee" : "service"),
            new("permissions", "*"),
        };
        if (identity == "duplicate-kind") claims.Add(new("identity_kind", "employee"));
        if (identity == "duplicate-sub") claims.Add(new("sub", "service:other"));
        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken(Issuer, Audience, claims, now.AddMinutes(-1), now.AddMinutes(5),
            new SigningCredentials(new RsaSecurityKey(signingKey), SecurityAlgorithms.RsaSha256));
        client.DefaultRequestHeaders.Authorization = new("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }

    public Task<HttpResponseMessage> AdmitAsync(HttpClient client, string intent = IntentId)
    {
        // Independent Python stdlib reference, exact framing with null optional arrays.
        const string digest = "8df2575fc38b90f91c153416beb7ca539b4f41ee2c1676f993f497d583ddf741";
        return client.PutAsJsonAsync("/notifications/v2/delivery-intents/" + intent, new
        { purpose = "invoice-issued", resourceType = "invoice", resourceId = "42", workflowOperationId = WorkflowId, channel = "Info", payloadDigestVersion = "notification-payload-v1", payloadDigest = digest });
    }

    public Task<HttpResponseMessage> ExecuteAsync(HttpClient client, string body = "synthetic invoice") => client.PostAsJsonAsync(Path + "/execute",
        new { channel = "Info", to = "recipient@example.invalid", subject = "Invoice fixture", body });

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(HostEnvironment);
        builder.UseSetting("Jwt:PublicKey", Convert.ToBase64String(Encoding.UTF8.GetBytes(signingKey.ExportSubjectPublicKeyInfoPem())));
        builder.UseSetting("Jwt:Issuer", Issuer);
        builder.UseSetting("Jwt:Audience", Audience);
        builder.UseSetting("Notifications:DeliveryIntentsEnabled", enabled.ToString());
        builder.UseSetting("ConnectionStrings:NotificationDeliveryIntentDbContext", connection);
        builder.UseSetting("Notifications:IntentKeys:ActiveKeyId", ActiveKeyId);
        if (RetainOldKey) builder.UseSetting("Notifications:IntentKeys:Keys:fixture", Convert.ToBase64String(new byte[32]));
        if (ActiveKeyId == "rotated") builder.UseSetting("Notifications:IntentKeys:Keys:rotated", Convert.ToBase64String(Enumerable.Repeat((byte)1, 32).ToArray()));
        builder.UseSetting("Services:IAMService:BaseUrl", IamOrigin);
        builder.UseSetting("Services:Auth:BaseUrl", AuthOrigin);
        builder.ConfigureAppConfiguration((_, config) =>
        {
            var values = new Dictionary<string, string?>
            {
                ["Notifications:DeliveryIntentsEnabled"] = enabled.ToString(),
                ["ConnectionStrings:NotificationDeliveryIntentDbContext"] = connection,
                ["Notifications:IntentKeys:ActiveKeyId"] = ActiveKeyId,
                ["ServiceAuthentication:ClientId"] = "synthetic-notification-client",
                ["ServiceAuthentication:ClientSecret"] = "synthetic-notification-client-secret",
                ["Services:IAMService:BaseUrl"] = IamOrigin,
                ["Services:Auth:BaseUrl"] = AuthOrigin,
                ["Brevo:ApiKey"] = "synthetic-fixture-only",
            };
            if (RetainOldKey) values["Notifications:IntentKeys:Keys:fixture"] = Convert.ToBase64String(new byte[32]);
            if (IncludeLiveCredential) values["IAM:LivePermissionChecks:Credential"] = LiveCredential;
            if (ActiveKeyId == "rotated") values["Notifications:IntentKeys:Keys:rotated"] = Convert.ToBase64String(Enumerable.Repeat((byte)1, 32).ToArray());
            foreach (var channel in new[] { "Info", "Manufacturing", "NoReply", "Support" })
            { values[$"Brevo:Senders:{channel}:Address"] = "sender@example.invalid"; values[$"Brevo:Senders:{channel}:DisplayName"] = "Synthetic fixture"; }
            config.AddInMemoryCollection(values);
        });
        builder.ConfigureServices(services =>
        {
            if (DatabaseInterceptors.Length > 0) services.AddDbContextFactory<DeliveryIntentDbContext>(options => options.AddInterceptors(DatabaseInterceptors));
            if (controlledAuthorities)
            {
                services.AddHttpClient("IAMService").ConfigurePrimaryHttpMessageHandler(() => new RemoteHandler(IamAsync));
                services.AddHttpClient(LegacyServiceAccessTokenProvider.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new RemoteHandler(AuthAsync));
            }
            if (controlledProvider) services.AddHttpClient("NotificationIntentSingleAttempt").ConfigurePrimaryHttpMessageHandler(() => new RemoteHandler(ProviderAsync));
            services.AddHttpClient<IBrevoNotificationTransport, BrevoNotificationTransport>().ConfigurePrimaryHttpMessageHandler(() => new RemoteHandler((_, _) =>
            {
                LegacyProviderCalls++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }));
        });
    }

    private async Task<HttpResponseMessage> IamAsync(HttpRequestMessage request, CancellationToken token)
    {
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://iam-fixture.invalid/iam/v1/auth/check-permission", request.RequestUri!.AbsoluteUri);
        Assert.Equal(WorkloadToken, request.Headers.Authorization!.Parameter);
        var body = await request.Content!.ReadFromJsonAsync<JsonElement>(token);
        Assert.Equal("service:legacy-accounting", body.GetProperty("principalId").GetString());
        var permission = body.GetProperty("permissionId").GetString()!;
        if (permission == "legacy.notifications.send")
        {
            Assert.Equal("legacy-only", IamMode);
            Assert.Equal("global", body.GetProperty("resourcePath").GetString());
            Assert.False(body.GetProperty("bypassCache").GetBoolean());
            Assert.False(request.Headers.Contains("X-Maliev-IAM-Live-Check-Key"));
            IamOperations.Add("legacy-send");
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { allowed = true }) };
        }
        Assert.Equal(LiveCredential, Assert.Single(request.Headers.GetValues("X-Maliev-IAM-Live-Check-Key")));
        Assert.Equal("legacy-notification/invoice/42", body.GetProperty("resourcePath").GetString());
        Assert.True(body.GetProperty("bypassCache").GetBoolean());
        Assert.Contains(permission, new[] { "legacy.notifications.intent.admit", "legacy.notifications.intent.execute", "legacy.notifications.intent.read" });
        IamOperations.Add(permission.Split('.').Last());
        return IamMode switch
        {
            "unavailable" => new(HttpStatusCode.ServiceUnavailable),
            "malformed" => new(HttpStatusCode.OK) { Content = new StringContent("not-json") },
            _ => new(HttpStatusCode.OK) { Content = JsonContent.Create(new { allowed = IamMode == "allow" }) },
        };
    }

    private async Task<HttpResponseMessage> AuthAsync(HttpRequestMessage request, CancellationToken token)
    {
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://auth-fixture.invalid/auth/v1/service/login", request.RequestUri!.AbsoluteUri);
        var body = await request.Content!.ReadFromJsonAsync<JsonElement>(token);
        Assert.Equal("synthetic-notification-client", body.GetProperty("clientId").GetString());
        Assert.Equal("synthetic-notification-client-secret", body.GetProperty("clientSecret").GetString());
        AuthCalls++;
        return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { accessToken = WorkloadToken, expiresIn = 300 }) };
    }

    private async Task<HttpResponseMessage> ProviderAsync(HttpRequestMessage request, CancellationToken token)
    {
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.brevo.com/v3/smtp/email", request.RequestUri!.AbsoluteUri);
        var body = await request.Content!.ReadFromJsonAsync<JsonElement>(token);
        Assert.Equal(IntentId, body.GetProperty("headers").GetProperty("idempotencyKey").GetString());
        Assert.Equal("synthetic invoice", body.GetProperty("htmlContent").GetString());
        Interlocked.Increment(ref providerCalls);
        if (BlockProvider)
        {
            ProviderEntered.TrySetResult();
            await ReleaseProvider.Task.WaitAsync(token);
        }
        if (LoseProviderAcknowledgment) throw new HttpRequestException("Controlled possible acceptance followed by lost acknowledgment.");
        var response = new HttpResponseMessage(ProviderStatus) { Content = JsonContent.Create(new { messageId = "controlled-provider-1" }) };
        if ((int)ProviderStatus is 307 or 308) response.Headers.Location = new Uri("https://redirect-fixture.invalid/no-effects");
        return response;
    }

    private sealed class RemoteHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken); }
}
