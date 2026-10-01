using Legacy.Maliev.NotificationService.Application.Interfaces;
using Legacy.Maliev.NotificationService.Data;
using Legacy.Maliev.NotificationService.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using Npgsql;
using Testcontainers.PostgreSql;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit.Abstractions;
using Legacy.Maliev.NotificationService.Application.Services;

namespace Legacy.Maliev.NotificationService.Tests.Data;

public sealed class DeliveryIntentStorePostgresTests(DeliveryIntentPostgresFixture fixture, ITestOutputHelper output) : IClassFixture<DeliveryIntentPostgresFixture>
{
    private static DeliveryIntentIdentity Identity() => new("https://iam.maliev.com", "service:legacy-accounting",
        Guid.NewGuid(), "invoice-issued", "invoice", 42, Guid.NewGuid(), EmailChannel.Info);

    private static NotificationIntentBinding Bindings() => new("key-a", new Dictionary<string, byte[]> { ["key-a"] = new byte[32], ["key-b"] = Enumerable.Repeat((byte)1, 32).ToArray() });

    [Fact]
    public async Task RotatedActiveKey_ReplayVerifiesRetainedKey_WithoutRehashOrOverwrite()
    {
        var factory = await fixture.FactoryAsync();
        var identity = Identity();
        var oldBinding = Bindings();
        var oldStore = new PostgresDeliveryIntentStore(factory, TimeProvider.System, oldBinding);
        var winner = await new DeliveryIntentAdmissionService(oldStore, oldBinding).AdmitAsync(identity,
            NotificationIntentBinding.PayloadVersion, new string('a', 64), default);
        var rotated = new NotificationIntentBinding("key-b", new Dictionary<string, byte[]> { ["key-a"] = new byte[32], ["key-b"] = Enumerable.Repeat((byte)1, 32).ToArray() });
        var rotatedStore = new PostgresDeliveryIntentStore(factory, TimeProvider.System, rotated);
        var replay = await new DeliveryIntentAdmissionService(rotatedStore, rotated).AdmitAsync(identity,
            NotificationIntentBinding.PayloadVersion, new string('a', 64), default);
        Assert.Equal(winner, replay);
        Assert.Equal("key-a", replay.KeyId);
        await Assert.ThrowsAsync<DeliveryIntentConflictException>(() => new DeliveryIntentAdmissionService(rotatedStore, rotated).AdmitAsync(identity,
            NotificationIntentBinding.PayloadVersion, new string('b', 64), default));
        Assert.Equal(winner, await oldStore.ReadAsync(identity.Issuer, identity.ServiceSubject, identity.IntentId, default));
    }

    [Fact]
    public async Task MissingRetainedKey_RefusesAdmissionReadAndFence_WithoutRebinding()
    {
        var factory = await fixture.FactoryAsync();
        var oldStore = new PostgresDeliveryIntentStore(factory, TimeProvider.System, Bindings());
        var identity = Identity();
        var admitted = await oldStore.AdmitAsync(identity, "key-a", new string('a', 64), default);
        var missing = new NotificationIntentBinding("key-b", new Dictionary<string, byte[]> { ["key-b"] = new byte[32] });
        var unavailable = new PostgresDeliveryIntentStore(factory, TimeProvider.System, missing);
        await Assert.ThrowsAsync<DeliveryIntentUnavailableException>(() => unavailable.ReadAsync(identity.Issuer, identity.ServiceSubject, identity.IntentId, default));
        await Assert.ThrowsAsync<DeliveryIntentUnavailableException>(() => unavailable.TryFenceAsync(admitted, default));
        await Assert.ThrowsAsync<DeliveryIntentUnavailableException>(() => new DeliveryIntentAdmissionService(unavailable, missing).AdmitAsync(identity,
            NotificationIntentBinding.PayloadVersion, new string('a', 64), default));
        Assert.Equal(admitted, await oldStore.ReadAsync(identity.Issuer, identity.ServiceSubject, identity.IntentId, default));
    }

    [Fact]
    public async Task ActualMigration_HasMatchingSnapshot_RerunAndRefusedDownRetainAuthority()
    {
        var factory = await fixture.FactoryAsync();
        await using var database = await factory.CreateDbContextAsync();
        var snapshot = database.GetService<IMigrationsAssembly>().ModelSnapshot;
        Assert.NotNull(snapshot);
        var initialized = database.GetService<IModelRuntimeInitializer>().Initialize(snapshot.Model);
        Assert.False(database.GetService<IMigrationsModelDiffer>().HasDifferences(initialized.GetRelationalModel(),
            database.GetService<IDesignTimeModel>().Model.GetRelationalModel()));
        var store = new PostgresDeliveryIntentStore(factory, TimeProvider.System, Bindings());
        var admitted = await store.AdmitAsync(Identity(), "key-a", new string('a', 64), default);
        await database.Database.MigrateAsync();
        Assert.Equal(["20261001143000_AddNotificationDeliveryIntent"], await database.Database.GetAppliedMigrationsAsync());
        await Assert.ThrowsAsync<NotSupportedException>(() => database.GetService<IMigrator>().MigrateAsync("0"));
        Assert.Equal(admitted, await store.ReadAsync(admitted.Identity.Issuer, admitted.Identity.ServiceSubject, admitted.Identity.IntentId, default));
        Assert.Single(await database.Database.GetAppliedMigrationsAsync());
    }

    [Fact]
    public async Task ActualMigration_EmitsReviewedPhysicalConstraintMetadata()
    {
        var factory = await fixture.FactoryAsync();
        await using var database = await factory.CreateDbContextAsync();
        var constraints = await database.Database.SqlQueryRaw<string>("SELECT conname || '=' || pg_get_constraintdef(oid) AS \"Value\" FROM pg_constraint WHERE conrelid='public.\"NotificationDeliveryIntent\"'::regclass AND contype IN ('p','u','c') ORDER BY conname").ToListAsync();
        Assert.Equal(5, constraints.Count);
        foreach (var constraint in constraints) output.WriteLine(constraint);
    }

    [Fact]
    public async Task SameBusinessEffect_WithNewUuidAndChannel_AdmitsOnlyOneAcrossContexts()
    {
        var factory = await fixture.FactoryAsync();
        var original = Identity();
        var duplicate = original with { IntentId = Guid.NewGuid(), Channel = EmailChannel.Support };
        async Task<Exception?> Admit(DeliveryIntentIdentity identity) => await Record.ExceptionAsync(() =>
            new PostgresDeliveryIntentStore(factory, TimeProvider.System, Bindings()).AdmitAsync(identity, "key-a", new string('a', 64), default));
        var results = await Task.WhenAll(Admit(original), Admit(duplicate));
        Assert.Single(results, result => result is null);
        Assert.IsType<DeliveryIntentConflictException>(Assert.Single(results, result => result is not null));
        await using var database = await factory.CreateDbContextAsync();
        Assert.Equal(1, await database.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM public.\"NotificationDeliveryIntent\"").SingleAsync());
    }

    [Fact]
    public async Task AmbientSearchPath_CannotSelectShadowAuthority()
    {
        var factory = await fixture.FactoryAsync();
        var identity = Identity();
        var admitted = await new PostgresDeliveryIntentStore(factory, TimeProvider.System, Bindings()).AdmitAsync(identity, "key-a", new string('a', 64), default);
        await using (var database = await factory.CreateDbContextAsync())
        {
            await database.Database.ExecuteSqlRawAsync("CREATE SCHEMA poison; CREATE TABLE poison.\"NotificationDeliveryIntent\" (LIKE public.\"NotificationDeliveryIntent\" INCLUDING ALL)");
        }
        var shadowFactory = factory.WithSearchPath("poison,public");
        var observed = await new PostgresDeliveryIntentStore(shadowFactory, TimeProvider.System, Bindings())
            .ReadAsync(identity.Issuer, identity.ServiceSubject, identity.IntentId, default);
        Assert.Equal(admitted, observed);
    }

    [Theory]
    [InlineData("ALTER TABLE public.\"NotificationDeliveryIntent\" DROP CONSTRAINT \"CK_NotificationIntent_State\"")]
    [InlineData("ALTER TABLE public.\"NotificationDeliveryIntent\" ALTER COLUMN \"Binding\" TYPE character varying(65)")]
    [InlineData("ALTER TABLE public.\"NotificationDeliveryIntent\" DROP CONSTRAINT \"AK_NotificationIntent_BusinessEffect\"")]
    [InlineData("ALTER TABLE public.\"NotificationDeliveryIntent\" DROP CONSTRAINT \"PK_NotificationDeliveryIntent\"")]
    [InlineData("ALTER TABLE public.\"NotificationDeliveryIntent\" ALTER COLUMN \"ResourceId\" DROP NOT NULL")]
    [InlineData("ALTER TABLE public.\"NotificationDeliveryIntent\" ALTER COLUMN \"Version\" TYPE numeric")]
    [InlineData("ALTER TABLE public.\"NotificationDeliveryIntent\" ALTER COLUMN \"ProviderMessageId\" SET DEFAULT 'synthetic-default'")]
    [InlineData("ALTER TABLE public.\"NotificationDeliveryIntent\" DROP CONSTRAINT \"CK_NotificationIntent_State\"; ALTER TABLE public.\"NotificationDeliveryIntent\" ADD CONSTRAINT \"CK_NotificationIntent_State\" CHECK (true) NOT VALID")]
    [InlineData("ALTER TABLE public.\"NotificationDeliveryIntent\" SET UNLOGGED")]
    [InlineData("ALTER TABLE public.\"NotificationDeliveryIntent\" ENABLE ROW LEVEL SECURITY")]
    public async Task PhysicalShapeMismatch_RefusesAdmissionAndLeavesNoRows(string ddl)
    {
        var factory = await fixture.FactoryAsync();
        await using var database = await factory.CreateDbContextAsync();
        await database.Database.ExecuteSqlRawAsync(ddl);
        var store = new PostgresDeliveryIntentStore(factory, TimeProvider.System, Bindings());
        await Assert.ThrowsAsync<DeliveryIntentUnavailableException>(() => store.AdmitAsync(Identity(), "key-a", new string('a', 64), default));
        Assert.Equal(0, await database.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM public.\"NotificationDeliveryIntent\"").SingleAsync());
    }

    [Theory]
    [InlineData("ALTER TABLE public.\"NotificationDeliveryIntent\" ADD COLUMN \"UnexpectedGenerated\" integer GENERATED ALWAYS AS (\"ResourceId\" + 1) STORED")]
    [InlineData("ALTER TABLE public.\"NotificationDeliveryIntent\" ADD COLUMN \"UnexpectedIdentity\" bigint GENERATED ALWAYS AS IDENTITY")]
    [InlineData("CREATE RULE unexpected_rewrite AS ON INSERT TO public.\"NotificationDeliveryIntent\" DO ALSO NOTIFY synthetic_intent_rule")]
    public async Task HiddenColumnsAndRewriteRules_RefuseBeforeAdmission(string ddl)
    {
        var factory = await fixture.FactoryAsync();
        await using var database = await factory.CreateDbContextAsync();
        await database.Database.ExecuteSqlRawAsync(ddl);
        var store = new PostgresDeliveryIntentStore(factory, TimeProvider.System, Bindings());
        await Assert.ThrowsAsync<DeliveryIntentUnavailableException>(() => store.AdmitAsync(Identity(), "key-a", new string('a', 64), default));
        Assert.Equal(0, await database.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM public.\"NotificationDeliveryIntent\"").SingleAsync());
    }

    [Fact]
    public async Task UnexpectedRewriteRule_RefusesReadAuthority_NotOnlyUnsupportedOnConflictInsert()
    {
        var factory = await fixture.FactoryAsync();
        var store = new PostgresDeliveryIntentStore(factory, TimeProvider.System, Bindings());
        var identity = Identity();
        await store.AdmitAsync(identity, "key-a", new string('a', 64), default);
        await using var database = await factory.CreateDbContextAsync();
        await database.Database.ExecuteSqlRawAsync("CREATE RULE unexpected_rewrite AS ON INSERT TO public.\"NotificationDeliveryIntent\" DO ALSO NOTIFY synthetic_intent_rule");
        await Assert.ThrowsAsync<DeliveryIntentUnavailableException>(() => store.ReadAsync(identity.Issuer, identity.ServiceSubject, identity.IntentId, default));
        Assert.Equal(1, await database.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM public.\"NotificationDeliveryIntent\"").SingleAsync());
    }

    [Fact]
    public async Task Admission_ReplayCollisionAndServiceScope_RetainExactWinner()
    {
        var factory = await fixture.FactoryAsync();
        var store = new PostgresDeliveryIntentStore(factory, TimeProvider.System, Bindings());
        var identity = Identity();
        var winner = await store.AdmitAsync(identity, "key-a", new string('a', 64), default);
        var replay = await store.AdmitAsync(identity, "key-a", new string('a', 64), default);
        Assert.Equal(winner, replay);
        Assert.Equal(DeliveryIntentState.Admitted, winner.State);
        Assert.Equal(1, winner.Version);
        await Assert.ThrowsAsync<DeliveryIntentConflictException>(() => store.AdmitAsync(identity with { ResourceId = 43 }, "key-a", new string('a', 64), default));
        await Assert.ThrowsAsync<DeliveryIntentConflictException>(() => store.AdmitAsync(identity, "key-a", new string('b', 64), default));
        Assert.Null(await store.ReadAsync("https://other.invalid", identity.ServiceSubject, identity.IntentId, default));
        Assert.Equal(winner, await store.ReadAsync(identity.Issuer, identity.ServiceSubject, identity.IntentId, default));
    }

    [Fact]
    public async Task ConcurrentFreshContexts_AdmitOneAndFenceOne_UnknownCannotResend()
    {
        var factory = await fixture.FactoryAsync();
        var first = new PostgresDeliveryIntentStore(factory, TimeProvider.System, Bindings());
        var second = new PostgresDeliveryIntentStore(factory, TimeProvider.System, Bindings());
        var identity = Identity();
        var admissions = await Task.WhenAll(first.AdmitAsync(identity, "key-a", new string('a', 64), default),
            second.AdmitAsync(identity, "key-a", new string('a', 64), default));
        Assert.Equal(admissions[0], admissions[1]);
        var fences = await Task.WhenAll(first.TryFenceAsync(admissions[0], default), second.TryFenceAsync(admissions[1], default));
        var fence = Assert.Single(fences, value => value is not null)!;
        Assert.Equal(2, fence.Version);
        var unknown = await first.TryMarkUnknownAsync(fence, default);
        Assert.Equal(DeliveryIntentState.OutcomeUnknown, unknown!.State);
        Assert.Null(await second.TryFenceAsync(unknown, default));
        Assert.Null(await second.TryFenceAsync(admissions[0], default));
    }

    [Fact]
    public async Task AcceptedResult_RejectsStaleUnknownAndRepeatedFence()
    {
        var factory = await fixture.FactoryAsync();
        var store = new PostgresDeliveryIntentStore(factory, TimeProvider.System, Bindings());
        var admitted = await store.AdmitAsync(Identity(), "key-a", new string('a', 64), default);
        var fence = (await store.TryFenceAsync(admitted, default))!;
        var accepted = await store.TryAcceptAsync(fence, "controlled-provider-id", default);
        Assert.Equal(DeliveryIntentState.ProviderAccepted, accepted!.State);
        Assert.Equal(3, accepted.Version);
        Assert.Null(await store.TryMarkUnknownAsync(fence, default));
        Assert.Null(await store.TryAcceptAsync(fence, "replacement-id", default));
        Assert.Null(await store.TryFenceAsync(accepted, default));
        Assert.Equal(accepted, await store.ReadAsync(admitted.Identity.Issuer, admitted.Identity.ServiceSubject, admitted.Identity.IntentId, default));
    }

    [Fact]
    public async Task CallerCanceledBeforeAdmission_PropagatesAndLeavesNoAuthority()
    {
        var factory = await fixture.FactoryAsync();
        var store = new PostgresDeliveryIntentStore(factory, TimeProvider.System, Bindings());
        var identity = Identity();
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.AdmitAsync(identity, "key-a", new string('a', 64), caller.Token));
        Assert.Equal(caller.Token, error.CancellationToken);
        Assert.Null(await store.ReadAsync(identity.Issuer, identity.ServiceSubject, identity.IntentId, default));
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public async Task SubmittedCommitAmbiguity_UsesOneConfiguredStrategyAttempt_AndRetainsAuthority(bool afterCommit, int failureKind)
    {
        var fault = new CommitFault(afterCommit, failureKind);
        var factory = await fixture.FactoryAsync(fault);
        var store = new PostgresDeliveryIntentStore(factory, TimeProvider.System, Bindings());
        var admitted = await store.AdmitAsync(Identity(), "key-a", new string('a', 64), default);
        fault.Armed = true;
        var error = await Assert.ThrowsAsync<DeliveryIntentUnavailableException>(() => store.TryFenceAsync(admitted, default));
        Assert.Same(fault.Failure, error.InnerException);
        Assert.Equal(1, fault.Attempts);
        fault.Armed = false;
        var retained = await store.ReadAsync(admitted.Identity.Issuer, admitted.Identity.ServiceSubject, admitted.Identity.IntentId, default);
        Assert.Equal(afterCommit ? DeliveryIntentState.Submitting : DeliveryIntentState.Admitted, retained!.State);
        Assert.Equal(afterCommit ? 2 : 1, retained.Version);
        if (afterCommit) Assert.Null(await store.TryFenceAsync(admitted, default));
    }

    private sealed class CommitFault(bool afterCommit, int failureKind) : DbTransactionInterceptor
    {
        public bool Armed { get; set; }
        public int Attempts { get; private set; }
        public Exception Failure { get; } = failureKind switch
        {
            0 => new NpgsqlException("Controlled transient commit acknowledgment fault.", new IOException("Controlled I/O fault.")),
            1 => new InvalidOperationException("Controlled post-commit failure."),
            _ => new OperationCanceledException("Controlled post-commit cancellation."),
        };

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (Armed)
            {
                Attempts++;
                if (!afterCommit) throw Failure;
            }
            return ValueTask.FromResult(result);
        }

        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (Armed && afterCommit) throw Failure;
            return Task.CompletedTask;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualDisposalFailure_DoesNotReturnSubmissionAuthority_OrReplay(bool contextDisposal)
    {
        var factory = await fixture.FactoryAsync();
        var store = new PostgresDeliveryIntentStore(factory, TimeProvider.System, Bindings());
        var admitted = await store.AdmitAsync(Identity(), "key-a", new string('a', 64), default);
        var failure = new IOException("Controlled disposal acknowledgment fault.");
        var calls = 0;
        var failing = factory.WithDisposalFault(contextDisposal, () => { calls++; throw failure; });
        var error = await Assert.ThrowsAsync<DeliveryIntentUnavailableException>(() =>
            new PostgresDeliveryIntentStore(failing, TimeProvider.System, Bindings()).TryFenceAsync(admitted, default));
        Assert.Same(failure, error.InnerException);
        Assert.Equal(contextDisposal ? 2 : 1, calls);
        var retained = await store.ReadAsync(admitted.Identity.Issuer, admitted.Identity.ServiceSubject, admitted.Identity.IntentId, default);
        Assert.Equal(DeliveryIntentState.Submitting, retained!.State);
        Assert.Equal(2, retained.Version);
        Assert.Null(await store.TryFenceAsync(admitted, default));
    }

    [Fact]
    public async Task CallerCanceledAfterActualCommit_IsUnknown_NotCancellationSuccessOrReplay()
    {
        using var caller = new CancellationTokenSource();
        var fault = new CancelAfterCommit(caller);
        var factory = await fixture.FactoryAsync(fault);
        var store = new PostgresDeliveryIntentStore(factory, TimeProvider.System, Bindings());
        var admitted = await store.AdmitAsync(Identity(), "key-a", new string('a', 64), default);
        fault.Armed = true;
        var error = await Assert.ThrowsAsync<DeliveryIntentUnavailableException>(() => store.TryFenceAsync(admitted, caller.Token));
        Assert.Same(fault.Failure, error.InnerException);
        Assert.True(caller.IsCancellationRequested);
        Assert.Equal(1, fault.Commits);
        fault.Armed = false;
        var retained = await store.ReadAsync(admitted.Identity.Issuer, admitted.Identity.ServiceSubject, admitted.Identity.IntentId, default);
        Assert.Equal(DeliveryIntentState.Submitting, retained!.State);
        Assert.Equal(2, retained.Version);
        Assert.Null(await store.TryFenceAsync(admitted, default));
    }

    private sealed class CancelAfterCommit(CancellationTokenSource caller) : DbTransactionInterceptor
    {
        public bool Armed { get; set; }
        public int Commits { get; private set; }
        public OperationCanceledException Failure { get; } = new("Controlled post-COMMIT caller cancellation.", caller.Token);
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (Armed) { Commits++; caller.Cancel(); throw Failure; }
            return Task.CompletedTask;
        }
    }
}

public sealed class DeliveryIntentPostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:18-alpine").Build();

    public Task InitializeAsync() => postgres.StartAsync();
    public Task DisposeAsync() => postgres.DisposeAsync().AsTask();

    public async Task<IntentContextFactory> FactoryAsync(params IInterceptor[] interceptors)
    {
        var databaseName = "notification_intent_" + Guid.NewGuid().ToString("N");
        await using (var connection = new NpgsqlConnection(postgres.GetConnectionString()))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", connection);
            await command.ExecuteNonQueryAsync();
        }

        var connectionString = new NpgsqlConnectionStringBuilder(postgres.GetConnectionString()) { Database = databaseName }.ConnectionString;
        var options = new DbContextOptionsBuilder<DeliveryIntentDbContext>()
            .UseNpgsql(connectionString, provider => provider.EnableRetryOnFailure(3))
            .AddInterceptors(interceptors).Options;
        var factory = new IntentContextFactory(options);
        await using var database = await factory.CreateDbContextAsync();
        await database.Database.MigrateAsync();
        return factory;
    }
}

public sealed class IntentContextFactory(DbContextOptions<DeliveryIntentDbContext> options, Action? contextDisposal = null) : IDbContextFactory<DeliveryIntentDbContext>
{
    public IntentContextFactory WithDisposalFault(bool context, Action fault) => context
        ? new(options, fault)
        : new(new DbContextOptionsBuilder<DeliveryIntentDbContext>(options)
            .LogTo(_ => fault(), [RelationalEventId.TransactionDisposed], Microsoft.Extensions.Logging.LogLevel.Debug).Options);
    public IntentContextFactory WithSearchPath(string searchPath)
    {
        using var database = CreateDbContext();
        var connectionString = new NpgsqlConnectionStringBuilder(database.Database.GetConnectionString()) { SearchPath = searchPath }.ConnectionString;
        return new(new DbContextOptionsBuilder<DeliveryIntentDbContext>(options).UseNpgsql(connectionString,
            provider => provider.EnableRetryOnFailure(3)).Options);
    }
    public DeliveryIntentDbContext CreateDbContext() => contextDisposal is null ? new DeliveryIntentDbContext(options) : new DisposalContext(options, contextDisposal);
    public Task<DeliveryIntentDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(CreateDbContext());
    }

    private sealed class DisposalContext(DbContextOptions<DeliveryIntentDbContext> options, Action fault) : DeliveryIntentDbContext(options)
    {
        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            fault();
        }
    }
}
