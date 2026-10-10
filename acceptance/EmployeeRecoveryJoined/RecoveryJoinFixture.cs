extern alias AuthApi;
extern alias BffApi;
extern alias NotificationApi;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Legacy.Maliev.AuthService.Infrastructure;
using Legacy.Maliev.NotificationService.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;
using Xunit;
using AuthProgram = AuthApi::Program;
using BffProgram = BffApi::Program;
using NotificationProgram = NotificationApi::Program;

public sealed class RecoveryJoinFixture : IAsyncDisposable
{
    public static readonly Uri PublicOrigin = new("https://localhost");
    private const string Issuer = "https://joined-recovery.example.invalid";
    private const string Audience = "joined-recovery";
    private static int cleanupPoisoned;
    private readonly string run = Required("N62_RUN_ID");
    private readonly string resourceDirectory = Required("N62_RESOURCE_DIRECTORY");
    private readonly string fixtureId = Guid.NewGuid().ToString("N");
    private readonly DateTimeOffset created = DateTimeOffset.UtcNow;
    private readonly CancellationTokenSource lease = new(TimeSpan.FromMinutes(4));
    private readonly RSA signing = RSA.Create(2048);
    private readonly string serviceSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly PostgreSqlContainer postgres;
    private readonly RedisContainer redis;
    private readonly ConcurrentQueue<(string Host, string Path, int Status)> routes = new();
    private readonly HashSet<string> connections = new(StringComparer.Ordinal);
    private readonly HashSet<string> clearedConnections = new(StringComparer.Ordinal);
    private readonly List<string> cleanupFailures = [];
    private AuthFactory? auth;
    private NotificationFactory? notifications;
    private BffFactory? bff;
    private string employeeConnection = "";
    private string stateConnection = "";
    private string customerConnection = "";
    private string? postgresId;
    private string? redisId;
    private int disposed;
    private HostDisposalGate? hostDisposalGate;
    private bool dependentCleanupDeferred;
    public ProviderCapture Provider { get; } = new();
    public CancellationToken Token => lease.Token;

    private RecoveryJoinFixture()
    {
        postgres = new PostgreSqlBuilder("postgres:18-alpine")
            .WithLabel("maliev.validation.notification62.run", run)
            .WithLabel("maliev.validation.notification62.fixture", fixtureId)
            .WithCreateParameterModifier(parameters =>
            {
                parameters.HostConfig.Memory = 1024L * 1024 * 1024;
                parameters.HostConfig.NanoCPUs = 1_000_000_000;
                parameters.HostConfig.Tmpfs = new Dictionary<string, string>
                { ["/var/lib/postgresql"] = "rw,size=512m" };
                foreach (var bindings in parameters.HostConfig.PortBindings.Values)
                    foreach (var binding in bindings) binding.HostIP = "127.0.0.1";
            }).Build();
        redis = new RedisBuilder("redis:7-alpine")
            .WithLabel("maliev.validation.notification62.run", run)
            .WithLabel("maliev.validation.notification62.fixture", fixtureId)
            .WithCreateParameterModifier(parameters =>
            {
                parameters.HostConfig.Memory = 128L * 1024 * 1024;
                parameters.HostConfig.NanoCPUs = 500_000_000;
                parameters.HostConfig.Tmpfs = new Dictionary<string, string> { ["/data"] = "rw,size=64m" };
                foreach (var bindings in parameters.HostConfig.PortBindings.Values)
                    foreach (var binding in bindings) binding.HostIP = "127.0.0.1";
            }).Build();
        SaveLedger("allocated-source-handles");
    }

    public static async Task<RecoveryJoinFixture> CreateAsync()
    {
        if (Volatile.Read(ref cleanupPoisoned) != 0)
            throw new InvalidOperationException("Earlier owned cleanup failed; further host/container admission is blocked.");
        var fixture = new RecoveryJoinFixture();
        try
        {
            await fixture.StartAsync();
            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync();
            throw;
        }
    }

    private async Task StartAsync()
    {
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(lease.Token);
        startup.CancelAfter(TimeSpan.FromSeconds(120));
        try
        {
            await postgres.StartAsync(startup.Token);
            SaveLedger("postgres-started");
            await redis.StartAsync(startup.Token);
            SaveLedger("redis-started");
            employeeConnection = await CreateDatabaseAsync(startup.Token);
            stateConnection = await CreateDatabaseAsync(startup.Token);
            customerConnection = await CreateDatabaseAsync(startup.Token);
            await using (var employees = Employees()) await employees.Database.MigrateAsync(startup.Token);
            await using (var state = State()) await state.Database.MigrateAsync(startup.Token);
            await using (var customers = new CustomerIdentityDbContext(new DbContextOptionsBuilder<CustomerIdentityDbContext>()
                .UseNpgsql(customerConnection).Options)) await customers.Database.MigrateAsync(startup.Token);
            auth = new AuthFactory(this);
            startup.Token.ThrowIfCancellationRequested();
            using (auth.CreateClient()) { }
            Assert.Contains(auth.Services.GetServices<IHostedService>(), service => service is EmployeeRecoveryWorker);
            notifications = new NotificationFactory(this);
            startup.Token.ThrowIfCancellationRequested();
            using (notifications.CreateClient()) { }
            bff = new BffFactory(this);
            startup.Token.ThrowIfCancellationRequested();
            using (Browser()) { }
            startup.Token.ThrowIfCancellationRequested();
            SaveLedger("hosts-started");
        }
        finally { SaveLedger("startup-finally"); }
    }

    private async Task<string> CreateDatabaseAsync(CancellationToken token)
    {
        var database = "joined_recovery_" + Guid.NewGuid().ToString("N");
        var bootstrapConnection = postgres.GetConnectionString();
        connections.Add(bootstrapConnection);
        await using var connection = new NpgsqlConnection(bootstrapConnection);
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE \"{database}\"";
        command.CommandTimeout = 15;
        await command.ExecuteNonQueryAsync(token);
        var value = new NpgsqlConnectionStringBuilder(postgres.GetConnectionString())
            { Database = database, Timeout = 10, CommandTimeout = 15, MaxPoolSize = 8 }.ConnectionString;
        connections.Add(value);
        return value;
    }

    public EmployeeIdentityDbContext Employees() => new(new DbContextOptionsBuilder<EmployeeIdentityDbContext>()
        .UseNpgsql(employeeConnection).Options);
    public RefreshSessionDbContext State() => new(new DbContextOptionsBuilder<RefreshSessionDbContext>()
        .UseNpgsql(stateConnection).Options);
    public bool IsPoolRegistered(string value) => connections.Contains(value);
    public bool WasPoolCleared(string value) => clearedConnections.Contains(value);
    public bool AllRegisteredPoolsCleared => connections.SetEquals(clearedConnections);

    // Read actual DI-created, unopened host connections; never duplicate Auth's pooling policy.
    private void RegisterHostPools(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        connections.Add(scope.ServiceProvider.GetRequiredService<EmployeeIdentityDbContext>().Database.GetDbConnection().ConnectionString);
        connections.Add(scope.ServiceProvider.GetRequiredService<CustomerIdentityDbContext>().Database.GetDbConnection().ConnectionString);
        connections.Add(scope.ServiceProvider.GetRequiredService<RefreshSessionDbContext>().Database.GetDbConnection().ConnectionString);
    }

    public (string FixtureConnection, string HostConnection)[] EffectivePoolsForRegression()
    {
        using var scope = auth!.Services.CreateScope();
        return
        [
            (employeeConnection, scope.ServiceProvider.GetRequiredService<EmployeeIdentityDbContext>().Database.GetDbConnection().ConnectionString),
            (customerConnection, scope.ServiceProvider.GetRequiredService<CustomerIdentityDbContext>().Database.GetDbConnection().ConnectionString),
            (stateConnection, scope.ServiceProvider.GetRequiredService<RefreshSessionDbContext>().Database.GetDbConnection().ConnectionString),
        ];
    }
    public HttpClient Browser()
    {
        lease.Token.ThrowIfCancellationRequested();
        var client = bff!.CreateClient(new WebApplicationFactoryClientOptions
            { BaseAddress = PublicOrigin, AllowAutoRedirect = false, HandleCookies = true });
        client.Timeout = TimeSpan.FromSeconds(30);
        return client;
    }

    public void AssertRealJoin(string purpose)
    {
        Assert.Single(routes.Where(x => x.Host == "auth" && x.Path == "/auth/v1/service/login" && x.Status == 200));
        Assert.Single(routes.Where(x => x.Host == "auth" && x.Path == "/auth/v1/employee-self-service/" + purpose + "/request" && x.Status == 200));
        var completions = routes.Where(x => x.Host == "auth" && x.Path == "/auth/v1/employee-self-service/" + purpose + "/complete").ToArray();
        Assert.Equal(new[] { 204, 400 }, completions.Select(x => x.Status));
        var sent = Assert.Single(routes.Where(x => x.Host == "notification" && x.Path == "/notifications/v1/email/NoReply"));
        Assert.Equal(Provider.Status == 201 ? 200 : 502, sent.Status);
    }

    private Dictionary<string, string?> Common() => new()
    {
        ["Jwt:Issuer"] = Issuer, ["Jwt:Audience"] = Audience, ["Jwt:KeyId"] = "joined-recovery",
        ["Jwt:PublicKeyPem"] = signing.ExportSubjectPublicKeyInfoPem(),
        ["Jwt:PublicKey"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(signing.ExportSubjectPublicKeyInfoPem())),
        ["Logging:LogLevel:Default"] = "Warning", ["CORS:AllowedOrigins"] = PublicOrigin.ToString(),
    };

    private static void Configure(IWebHostBuilder builder, string root, Dictionary<string, string?> settings)
    {
        builder.UseEnvironment("Production").UseContentRoot(root);
        foreach (var item in settings) builder.UseSetting(item.Key, item.Value);
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings));
        builder.ConfigureServices(services => services.Configure<HostOptions>(options =>
        {
            options.StartupTimeout = TimeSpan.FromSeconds(30);
            options.ShutdownTimeout = TimeSpan.FromSeconds(10);
        }));
    }

    private sealed class AuthFactory(RecoveryJoinFixture fixture) : WebApplicationFactory<AuthProgram>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            var settings = fixture.Common();
            settings["Jwt:PrivateKeyPem"] = fixture.signing.ExportPkcs8PrivateKeyPem();
            settings["ConnectionStrings:EmployeeIdentity"] = fixture.employeeConnection;
            settings["ConnectionStrings:RefreshSessions"] = fixture.stateConnection;
            settings["ConnectionStrings:CustomerIdentity"] = fixture.customerConnection;
            settings["EmployeeRecovery:Enabled"] = "true";
            settings["ServiceClients:Clients:legacy-intranet:SecretSha256"] = ServiceClientCredential.HashSecret(fixture.serviceSecret);
            settings["ServiceClients:Clients:legacy-intranet:Permissions:0"] = AuthApi::Legacy.Maliev.AuthService.Api.Authorization.EmployeeSelfServicePermissions.Use;
            settings["ServiceClients:Clients:legacy-intranet:Permissions:1"] = NotificationApi::Legacy.Maliev.NotificationService.Api.Authorization.NotificationPermissions.Send;
            Configure(builder, Path.Combine(Required("RECOVERY_AUTH_ROOT"), "Legacy.Maliev.AuthService.Api"), settings);
            builder.ConfigureServices(services => services.ConfigureAll<HttpClientFactoryOptions>(options =>
                options.HttpMessageHandlerBuilderActions.Add(handler => handler.PrimaryHandler = new BlockOutbound())));
            builder.ConfigureServices(services => services.AddSingleton<IHostedService>(provider => new HostPoolObserver(fixture, provider)));
        }
    }

    // Generic Host calls lifecycle StartingAsync before hosted-service StartAsync.
    // This captures effective pools before the real recovery worker can open a connection.
    private sealed class HostPoolObserver(RecoveryJoinFixture fixture, IServiceProvider services) : IHostedLifecycleService
    {
        public Task StartingAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            fixture.RegisterHostPools(services);
            return Task.CompletedTask;
        }
        public Task StartAsync(CancellationToken token) => Task.CompletedTask;
        public Task StartedAsync(CancellationToken token) => Task.CompletedTask;
        public Task StoppingAsync(CancellationToken token) => Task.CompletedTask;
        public Task StopAsync(CancellationToken token) => Task.CompletedTask;
        public Task StoppedAsync(CancellationToken token) => Task.CompletedTask;
    }

    private sealed class NotificationFactory(RecoveryJoinFixture fixture) : WebApplicationFactory<NotificationProgram>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            var settings = fixture.Common();
            settings["Brevo:ApiKey"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            settings["Brevo:MaxRetryAttempts"] = "0";
            settings["Brevo:AttemptTimeoutMilliseconds"] = "1000";
            foreach (var channel in new[] { "Info", "Manufacturing", "NoReply", "Support" })
            {
                settings[$"Brevo:Senders:{channel}:Address"] = channel + "@example.invalid";
                settings[$"Brevo:Senders:{channel}:DisplayName"] = "Synthetic " + channel;
            }
            Configure(builder, Path.Combine(Required("RECOVERY_NOTIFICATION_ROOT"), "Legacy.Maliev.NotificationService.Api"), settings);
            builder.ConfigureServices(services => services.ConfigureAll<HttpClientFactoryOptions>(options =>
                options.HttpMessageHandlerBuilderActions.Add(handler => handler.PrimaryHandler =
                    handler.Name == nameof(IBrevoNotificationTransport) ? fixture.Provider.CreateHandler() : new BlockOutbound())));
        }
    }

    private sealed class BffFactory(RecoveryJoinFixture fixture) : WebApplicationFactory<BffProgram>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            var settings = fixture.Common();
            settings["ConnectionStrings:redis"] = fixture.redis.GetConnectionString();
            settings["EmployeeConfirmation:PublicOrigin"] = PublicOrigin.ToString();
            settings["ServiceAuthentication:ClientId"] = "legacy-intranet";
            settings["ServiceAuthentication:ClientSecret"] = fixture.serviceSecret;
            using var certificateKey = RSA.Create(2048);
            var request = new CertificateRequest("CN=joined-recovery", certificateKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
            var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            settings["DataProtection:CertificatePfxBase64"] = Convert.ToBase64String(certificate.Export(X509ContentType.Pfx, password));
            settings["DataProtection:CertificatePassword"] = password;
            foreach (var name in new[] { "Auth", "Notification", "Customer", "Employee", "Catalog", "Order", "Accounting", "File", "Document", "Quotation", "Procurement" })
                settings["Services:" + name] = "https://" + name.ToLowerInvariant() + ".joined.invalid";
            Configure(builder, Path.Combine(Required("RECOVERY_INTRANET_ROOT"), "Legacy.Maliev.Intranet.Bff"), settings);
            builder.ConfigureServices(services => services.ConfigureAll<HttpClientFactoryOptions>(options =>
                options.HttpMessageHandlerBuilderActions.Add(handler =>
                {
                    if (handler.Name == "service-auth" || handler.Name == "EmployeeRecoveryAuthProxy")
                        handler.PrimaryHandler = new ObserveRoute(fixture, "auth") { InnerHandler = fixture.auth!.Server.CreateHandler() };
                    else if (handler.Name == "EmployeeRecoveryNotificationProxy")
                        handler.PrimaryHandler = new ObserveRoute(fixture, "notification") { InnerHandler = fixture.notifications!.Server.CreateHandler() };
                    else handler.PrimaryHandler = new BlockOutbound();
                })));
        }
    }

    private sealed class ObserveRoute(RecoveryJoinFixture fixture, string host) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            using var combined = CancellationTokenSource.CreateLinkedTokenSource(token, fixture.lease.Token);
            var response = await base.SendAsync(request, combined.Token);
            fixture.routes.Enqueue((host, request.RequestUri!.AbsolutePath, (int)response.StatusCode));
            return response;
        }
    }

    private sealed class BlockOutbound : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Unrouted external network is forbidden in the isolated join.");
    }

    public sealed class ProviderCapture
    {
        public int Status { get; set; } = 201;
        public List<JsonElement> Payloads { get; } = [];
        public HttpMessageHandler CreateHandler() => new CaptureHandler(this);
        private sealed class CaptureHandler(ProviderCapture capture) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                Assert.Equal("https://api.brevo.com/v3/smtp/email", request.RequestUri!.AbsoluteUri);
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Single(request.Headers.GetValues("api-key"));
                Assert.True(capture.Payloads.Count < 16);
                capture.Payloads.Add(await request.Content!.ReadFromJsonAsync<JsonElement>(token));
                return new((HttpStatusCode)capture.Status) { Content = JsonContent.Create(capture.Status == 201
                    ? new { messageId = "isolated-provider-capture" } as object
                    : new { code = "invalid_parameter", message = "controlled rejection" }) };
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try { lease.Cancel(); }
        catch (Exception exception)
        {
            Interlocked.Exchange(ref cleanupPoisoned, 1);
            dependentCleanupDeferred = true;
            cleanupFailures.Add("lease-cancel:" + exception.GetType().Name);
            SaveLedger("lease-cancel-failed-dependent-resources-preserved");
            throw new InvalidOperationException("Lease cancellation failed; hosts and dependent resources remain supervisor-owned.");
        }
        hostDisposalGate = new HostDisposalGate(() => Interlocked.Exchange(ref cleanupPoisoned, 1));
        var verified = await hostDisposalGate.DisposeBeforeDependentsAsync(
            new (string, Func<Task>)[]
            {
                ("bff", async () => { if (bff is not null) await bff.DisposeAsync(); }),
                ("notification", async () => { if (notifications is not null) await notifications.DisposeAsync(); }),
                ("auth", async () => { if (auth is not null) await auth.DisposeAsync(); }),
            }, TimeSpan.FromSeconds(30), async () =>
            {
                foreach (var value in connections)
                    await Release("pool", () =>
                    {
                        using var pool = new NpgsqlConnection(value);
                        NpgsqlConnection.ClearPool(pool);
                        clearedConnections.Add(value);
                        return Task.CompletedTask;
                    });
                await Release("redis", async () => await redis.DisposeAsync());
                await Release("postgres", async () => await postgres.DisposeAsync());
            });
        if (!verified)
        {
            dependentCleanupDeferred = true;
            cleanupFailures.AddRange(hostDisposalGate.Failures);
            SaveLedger("host-disposal-unverified-pools-containers-preserved");
            // Keep pools, containers, keys and lease handles intact while host disposal may be live.
            // The existing supervisor owns final removal only after native-process absence.
            throw new InvalidOperationException("Host disposal unverified; dependent cleanup deferred to owned native-exit supervisor.");
        }
        Provider.Payloads.Clear();
        signing.Dispose();
        lease.Dispose();
        SaveLedger(cleanupFailures.Count == 0 ? "disposed" : "cleanup-failed");
        if (cleanupFailures.Count != 0)
        {
            Interlocked.Exchange(ref cleanupPoisoned, 1);
            throw new InvalidOperationException("Owned resource cleanup failed: " + string.Join(",", cleanupFailures));
        }
    }

    private async Task Release(string name, Func<Task> dispose)
    {
        try { await dispose().WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (Exception exception)
        {
            Interlocked.Exchange(ref cleanupPoisoned, 1);
            cleanupFailures.Add(name + ":" + exception.GetType().Name);
        }
    }
    private static string? Id(Func<string> read) { try { return read(); } catch (InvalidOperationException) { return null; } }
    private static int? Port(Func<int> read) { try { return read(); } catch (InvalidOperationException) { return null; } }
    private void SaveLedger(string phase)
    {
        using var process = Process.GetCurrentProcess();
        var directory = resourceDirectory;
        Directory.CreateDirectory(directory);
        postgresId ??= Id(() => postgres.Id);
        redisId ??= Id(() => redis.Id);
        var value = new
        {
            run, fixtureId, phase, created, expires = created.AddMinutes(4),
            pid = process.Id, processStartUtc = process.StartTime.ToUniversalTime(), executable = Environment.ProcessPath,
            hosts = new[] { "AuthProgram", "BffProgram", "NotificationProgram" }, hostPorts = Array.Empty<int>(),
            transport = "in-process TestServer, no listening host/provider ports", persistentData = false,
            postgresId, redisId,
            postgresPort = Port(() => postgres.GetMappedPublicPort(5432)), redisPort = Port(() => redis.GetMappedPublicPort(6379)),
            binding = "127.0.0.1", volumeNames = Array.Empty<string>(), mountPolicy = "tmpfs only; independently inspected by hosted supervisor",
            ownershipLabel = "maliev.validation.notification62.run", cleanupFailures,
            registeredPoolCount = connections.Count, clearedPoolCount = clearedConnections.Count,
            allRegisteredPoolsCleared = AllRegisteredPoolsCleared,
            dependentCleanupDeferred,
            supervisorCustodyReceipt = Path.GetFullPath(Path.Combine(resourceDirectory, "..", "run-owner.json")),
            pendingExpiryOwner = dependentCleanupDeferred ? "Recorded finite supervisor stage/job lease; fixture admission remains poisoned" : null,
            hostDisposal = hostDisposalGate?.Snapshot(),
            remainingOwnership = dependentCleanupDeferred
                ? "Exact run-labelled containers and runtime-only pools/keys/lease retained; no fixture reuse; supervisor removes containers only after native process-group absence"
                : null,
        };
        var path = Path.Combine(directory, fixtureId + ".json");
        File.WriteAllText(path + ".new", JsonSerializer.Serialize(value));
        File.Move(path + ".new", path, true);
    }
    private static string Required(string name) => Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException("Required isolated hosted setting is missing: " + name);
}
