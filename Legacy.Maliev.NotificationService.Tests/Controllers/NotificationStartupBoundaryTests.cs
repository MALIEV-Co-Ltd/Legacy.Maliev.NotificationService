using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Xunit.Abstractions;

namespace Legacy.Maliev.NotificationService.Tests.Controllers;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class NotificationStartupBoundaryCollection
{
    public const string Name = "Notification standalone startup boundary";
}

[Collection(NotificationStartupBoundaryCollection.Name)]
public sealed class NotificationStartupBoundaryTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("argument")]
    [InlineData("configuration")]
    public async Task ActualApiEntryPoint_FailsOnceWithPrivateMetadataAndNoPlaintextFallback(string failure)
    {
        const string sentinel = "PRIVATE-STARTUP-SENTINEL";
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        Assert.False(string.IsNullOrWhiteSpace(host), "The allocated test SDK must supply DOTNET_HOST_PATH; no provider discovery is allowed.");
        Assert.True(Path.IsPathFullyQualified(host!));
        Assert.True(File.Exists(host));
        var assembly = typeof(NotificationProgram).Assembly;
        Assert.Equal("Legacy.Maliev.NotificationService.Api", assembly.GetName().Name);
        var testAssembly = typeof(NotificationStartupBoundaryTests).Assembly.Location;
        var runtime = Path.ChangeExtension(testAssembly, ".runtimeconfig.json");
        var dependencies = Path.ChangeExtension(testAssembly, ".deps.json");
        Assert.True(File.Exists(runtime));
        Assert.True(File.Exists(dependencies));
        var owned = Path.Combine(Path.GetTempPath(), "notification-startup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(owned);
        var process = new Process
        {
            StartInfo = new ProcessStartInfo(host!)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = owned,
            },
        };
        // Own the exact handle and directory before Start, including a partially successful Start.
        var custody = new StartupCustody(process, owned);
        try
        {
            if (failure == "configuration")
                await File.WriteAllTextAsync(Path.Combine(owned, "appsettings.Production.json"), "{\"" + sentinel + "\": NOT_JSON}");
            var systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
            process.StartInfo.Environment.Clear();
            if (systemRoot is not null) process.StartInfo.Environment["SystemRoot"] = systemRoot;
            process.StartInfo.Environment["DOTNET_ROOT"] = Path.GetDirectoryName(host)!;
            process.StartInfo.Environment["DOTNET_ENVIRONMENT"] = "Production";
            process.StartInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
            foreach (var variable in new[] { "HOME", "USERPROFILE", "TEMP", "TMP", "TMPDIR" })
                process.StartInfo.Environment[variable] = owned;
            foreach (var argument in new[] { "exec", "--runtimeconfig", runtime, "--depsfile", dependencies,
                         assembly.Location, "--urls", "http://127.0.0.1:0", "--Notifications:DeliveryIntentsEnabled", "false" })
                process.StartInfo.ArgumentList.Add(argument);
            if (failure == "argument") process.StartInfo.ArgumentList.Add("-" + sentinel + "=invalid");
            custody.StartAttempted = true;
            custody.StartResult = process.Start();
            Assert.True(custody.StartResult.Value);
            // Reader tasks belong to custody before metadata reads can fail.
            custody.StdoutReader = process.StandardOutput;
            custody.Stdout = ReadBoundedAsync(custody.StdoutReader, custody.Readers.Token);
            custody.StderrReader = process.StandardError;
            custody.Stderr = ReadBoundedAsync(custody.StderrReader, custody.Readers.Token);
            custody.Pid = process.Id;
            custody.Birth = process.StartTime.ToUniversalTime();
            custody.Executable = process.MainModule!.FileName;
            await process.WaitForExitAsync(custody.Readers.Token);
            var publicOutput = await custody.Stdout;
            var privateOutput = await custody.Stderr;
            Assert.Equal(1, process.ExitCode);
            Assert.Equal(string.Empty, publicOutput);
            Assert.DoesNotContain(sentinel, privateOutput, StringComparison.Ordinal);
            Assert.DoesNotContain(owned, privateOutput, StringComparison.Ordinal);
            Assert.DoesNotContain("Unhandled exception", privateOutput, StringComparison.OrdinalIgnoreCase);
            var line = Assert.Single(privateOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
            using var diagnostic = JsonDocument.Parse(line);
            var value = diagnostic.RootElement;
            Assert.Equal("CRITICAL", value.GetProperty("severity").GetString());
            Assert.Equal(5102, value.GetProperty("eventId").GetInt32());
            Assert.Equal("StartupFailure", value.GetProperty("EventName").GetString());
            Assert.Equal("HostInitialization", value.GetProperty("Operation").GetString());
            if (failure == "argument") Assert.Equal("System.FormatException", value.GetProperty("exceptionType").GetString());
            Assert.Equal(assembly.GetName().Name, value.GetProperty("service").GetString());
            Assert.False(value.TryGetProperty("exceptionMessage", out _));
            Assert.False(value.TryGetProperty("stackTrace", out _));
        }
        catch (Exception exception)
        {
            custody.Primary = exception;
        }
        finally
        {
            await SettleStartupCustodyAsync(custody);
            try
            {
                output.WriteLine("NOTIFICATION_STARTUP_RESOURCE=" + JsonSerializer.Serialize(new
                {
                    owner = "notification-startup-boundary",
                    custody.Id,
                    custody.Pid,
                    custody.Birth,
                    custody.Executable,
                    timeoutSeconds = 20,
                    stopTimeoutSeconds = 5,
                    readerSettlementSeconds = 5,
                    streamCharacterBound = OutputCharacterBound,
                    custody.ExitVerified,
                    stdoutSettled = custody.Stdout?.IsCompleted ?? true,
                    stderrSettled = custody.Stderr?.IsCompleted ?? true,
                    custody.ProcessDisposed,
                    custody.ReadersDisposed,
                    custody.StdoutReaderDisposed,
                    custody.StderrReaderDisposed,
                    custody.DirectoryRemoved,
                    recoveryRequired = !custody.Released,
                    custody.LeaseExpiresUtc,
                    primaryFailureType = custody.Primary?.GetType().Name,
                    cleanupFailureTypes = custody.CleanupFailures.Select(exception => exception.GetType().Name).ToArray(),
                    persistentData = false,
                }));
            }
            catch (Exception exception) { custody.CleanupFailures.Add(exception); }
        }
        if (custody.FailureForCaller() is { } failureForCaller)
            ExceptionDispatchInfo.Capture(failureForCaller).Throw();
    }

    [Fact]
    public async Task EmbeddedNormalProgram_PreservesOriginalInitializationFailureAndCallerExitCode()
    {
        var previous = Environment.ExitCode;
        string[] arguments = ["-PRIVATE-STARTUP-SENTINEL=invalid"];
        var expected = Assert.Throws<FormatException>(() => new ConfigurationBuilder().AddCommandLine(arguments).Build());
        var entry = typeof(NotificationProgram).Assembly.EntryPoint!;
        var exception = await Assert.ThrowsAsync<FormatException>(async () =>
        {
            try
            {
                var result = entry.Invoke(null, [arguments]);
                if (result is Task pending) await pending;
            }
            catch (System.Reflection.TargetInvocationException wrapper) when (wrapper.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(wrapper.InnerException).Throw();
            }
        });
        Assert.Equal(expected.Message, exception.Message);
        Assert.Equal(previous, Environment.ExitCode);
    }

    private const int OutputCharacterBound = 65536;
    // Fixture-local recovery custody, never a detached worker or a provider lookup.
    private static readonly Dictionary<string, StartupCustody> RetainedStartupChildren = [];

    [Theory]
    [InlineData(OutputCharacterBound, true)]
    [InlineData(OutputCharacterBound + 1, false)]
    public async Task StreamingReader_EnforcesBoundWithoutTruncatingAssertions(int count, bool accepted)
    {
        using var reader = new StringReader(new string('x', count));
        if (accepted) Assert.Equal(count, (await ReadBoundedAsync(reader, CancellationToken.None)).Length);
        else await Assert.ThrowsAsync<InvalidDataException>(() => ReadBoundedAsync(reader, CancellationToken.None));
    }

    [Fact]
    public async Task PartialStartUncertainty_RetainsExactHandleDirectoryAndOriginalFailure()
    {
        var custody = ControlCustody();
        custody.StartAttempted = true;
        // No Start is actually called in this control: emulate failure before metadata is available.
        custody.StartResult = null;
        var primary = new IOException("synthetic primary startup failure");
        custody.Primary = primary;
        try
        {
            await SettleStartupCustodyAsync(custody);
            Assert.False(custody.Released);
            Assert.False(custody.ProcessDisposed);
            Assert.True(Directory.Exists(custody.Directory));
            Assert.Same(custody, RetainedStartupChildren[custody.Id]);
            var aggregate = Assert.IsType<AggregateException>(custody.FailureForCaller());
            Assert.Same(primary, aggregate.InnerExceptions[0]);
            Assert.NotEmpty(custody.CleanupFailures);
        }
        finally
        {
            // This control never called Start; that local evidence allows releasing its unstarted handle.
            custody.StartAttempted = false;
            custody.StartResult = false;
            await SettleStartupCustodyAsync(custody);
        }
        Assert.True(custody.Released);
        Assert.False(RetainedStartupChildren.ContainsKey(custody.Id));
    }

    [Fact]
    public async Task UnsettledReaders_PreventDisposalAndRemovalUntilBothActuallyFinish()
    {
        var custody = ControlCustody();
        var stdout = new GateReader();
        var stderr = new GateReader();
        custody.StdoutReader = stdout;
        custody.StderrReader = stderr;
        custody.Stdout = ReadBoundedAsync(stdout, custody.Readers.Token);
        custody.Stderr = ReadBoundedAsync(stderr, custody.Readers.Token);
        try
        {
            await SettleStartupCustodyAsync(custody);
            Assert.False(custody.ProcessDisposed);
            Assert.False(stdout.IsDisposed);
            Assert.False(stderr.IsDisposed);
            Assert.True(Directory.Exists(custody.Directory));
            Assert.Contains(custody.CleanupFailures, exception => exception is TimeoutException);
            stdout.Release();
            await custody.Stdout;
            await SettleStartupCustodyAsync(custody);
            Assert.False(custody.ProcessDisposed);
            Assert.True(Directory.Exists(custody.Directory));
            Assert.False(custody.Stderr.IsCompleted);
        }
        finally
        {
            stdout.Release();
            stderr.Release();
            await Task.WhenAll(custody.Stdout, custody.Stderr).WaitAsync(TimeSpan.FromSeconds(5));
            await SettleStartupCustodyAsync(custody);
        }
        Assert.True(custody.Released);
        Assert.True(custody.ProcessDisposed);
        Assert.True(stdout.IsDisposed);
        Assert.True(stderr.IsDisposed);
        Assert.False(Directory.Exists(custody.Directory));
    }

    [Fact]
    public async Task DirectoryCleanupFailure_PreservesPrimaryAndEveryCleanupFailureForRecovery()
    {
        var custody = ControlCustody();
        var primary = new IOException("synthetic primary failure");
        var removal = new IOException("synthetic private-directory cleanup failure");
        var secondRemoval = new IOException("synthetic second private-directory cleanup failure");
        custody.Primary = primary;
        custody.DirectoryRemovalFault = removal;
        try
        {
            await SettleStartupCustodyAsync(custody);
            Assert.True(custody.ProcessDisposed); // Exit/readers already settled; handle release is safe.
            Assert.True(Directory.Exists(custody.Directory));
            Assert.False(custody.Released);
            Assert.Same(custody, RetainedStartupChildren[custody.Id]);
            var aggregate = Assert.IsType<AggregateException>(custody.FailureForCaller());
            Assert.Same(primary, aggregate.InnerExceptions[0]);
            Assert.Contains(removal, aggregate.InnerExceptions);
            custody.DirectoryRemovalFault = secondRemoval;
            await SettleStartupCustodyAsync(custody);
            Assert.Equal(new Exception[] { primary, removal, secondRemoval },
                Assert.IsType<AggregateException>(custody.FailureForCaller()).InnerExceptions);
        }
        finally
        {
            custody.DirectoryRemovalFault = null;
            await SettleStartupCustodyAsync(custody);
        }
        Assert.True(custody.Released);
        Assert.Same(primary, Assert.IsType<AggregateException>(custody.FailureForCaller()).InnerExceptions[0]);
    }

    private static async Task<string> ReadBoundedAsync(TextReader reader, CancellationToken token)
    {
        var text = new StringBuilder();
        var buffer = new char[1024];
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), token);
            if (count == 0) return text.ToString();
            if (text.Length + count > OutputCharacterBound)
                throw new InvalidDataException("Startup diagnostic stream exceeded its fixed bound.");
            text.Append(buffer, 0, count);
        }
    }

    private static async Task SettleStartupCustodyAsync(StartupCustody custody)
    {
        if (!custody.ProcessDisposed)
        {
            try
            {
                custody.ExitVerified = !custody.StartAttempted || custody.StartResult == false || custody.Process.HasExited;
                if (!custody.ExitVerified)
                {
                    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try { custody.Process.CloseMainWindow(); } // Graceful attempt on the retained owned handle first.
                    catch (Exception exception) { custody.CleanupFailures.Add(exception); }
                    using var graceful = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                    graceful.CancelAfter(TimeSpan.FromSeconds(1));
                    try { await custody.Process.WaitForExitAsync(graceful.Token); }
                    catch (OperationCanceledException) when (graceful.IsCancellationRequested && !stop.IsCancellationRequested) { }
                    if (!custody.Process.HasExited)
                    {
                        // Recover metadata through this same handle if an earlier metadata read failed.
                        custody.Pid ??= custody.Process.Id;
                        custody.Birth ??= custody.Process.StartTime.ToUniversalTime();
                        custody.Executable ??= custody.Process.MainModule!.FileName;
                        if (custody.Process.StartTime.ToUniversalTime() != custody.Birth)
                            throw new InvalidOperationException("Owned startup child birth changed; termination withheld.");
                        custody.Process.Kill(); // No PID lookup, names or recursive tree selector.
                        await custody.Process.WaitForExitAsync(stop.Token);
                    }
                    custody.ExitVerified = custody.Process.HasExited;
                }
            }
            catch (Exception exception) { custody.CleanupFailures.Add(exception); }
            finally
            {
                if (custody.StartAttempted && custody.StartResult != false)
                {
                    try { custody.ExitVerified = custody.Process.HasExited; }
                    catch (Exception exception) { custody.ExitVerified = false; custody.CleanupFailures.Add(exception); }
                }
            }
            try { custody.Readers.Cancel(); }
            catch (Exception exception) { custody.CleanupFailures.Add(exception); }
            var readers = new[] { custody.Stdout, custody.Stderr }.OfType<Task<string>>().ToArray();
            try { await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception exception)
            {
                if (exception is not OperationCanceledException || readers.Any(task => !task.IsCompleted))
                    custody.CleanupFailures.Add(exception);
            }
            foreach (var task in readers.Where(task => task.IsFaulted))
                foreach (var exception in task.Exception!.InnerExceptions)
                    if (!custody.CleanupFailures.Contains(exception)) custody.CleanupFailures.Add(exception);
            if (custody.ExitVerified && readers.All(task => task.IsCompleted))
            {
                if (!custody.StdoutReaderDisposed)
                {
                    try { custody.StdoutReader?.Dispose(); custody.StdoutReaderDisposed = true; }
                    catch (Exception exception) { custody.CleanupFailures.Add(exception); }
                }
                if (!custody.StderrReaderDisposed)
                {
                    try { custody.StderrReader?.Dispose(); custody.StderrReaderDisposed = true; }
                    catch (Exception exception) { custody.CleanupFailures.Add(exception); }
                }
                try
                {
                    if (custody.StdoutReaderDisposed && custody.StderrReaderDisposed)
                    {
                        custody.Process.Dispose();
                        custody.ProcessDisposed = true;
                    }
                }
                catch (Exception exception) { custody.CleanupFailures.Add(exception); }
            }
        }
        if (custody.ExitVerified && (custody.Stdout?.IsCompleted ?? true) && (custody.Stderr?.IsCompleted ?? true) && !custody.ReadersDisposed)
        {
            try { custody.Readers.Dispose(); custody.ReadersDisposed = true; }
            catch (Exception exception) { custody.CleanupFailures.Add(exception); }
        }
        if (custody.ProcessDisposed && custody.ReadersDisposed && !custody.DirectoryRemoved)
        {
            try
            {
                if (custody.DirectoryRemovalFault is { } fault) throw fault;
                Directory.Delete(custody.Directory, recursive: true);
                custody.DirectoryRemoved = !Directory.Exists(custody.Directory);
            }
            catch (Exception exception) { custody.CleanupFailures.Add(exception); }
        }
        if (custody.Released) RetainedStartupChildren.Remove(custody.Id);
        else RetainedStartupChildren[custody.Id] = custody;
    }

    private static StartupCustody ControlCustody()
    {
        var directory = Path.Combine(Path.GetTempPath(), "notification-startup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return new StartupCustody(new Process(), directory) { StartResult = false };
    }

    private sealed class StartupCustody(Process process, string directory)
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public Process Process { get; } = process;
        public string Directory { get; } = directory;
        public CancellationTokenSource Readers { get; } = new(TimeSpan.FromSeconds(20));
        public DateTimeOffset LeaseExpiresUtc { get; } = DateTimeOffset.UtcNow.AddSeconds(30);
        public bool StartAttempted { get; set; }
        public bool? StartResult { get; set; }
        public int? Pid { get; set; }
        public DateTime? Birth { get; set; }
        public string? Executable { get; set; }
        public Task<string>? Stdout { get; set; }
        public Task<string>? Stderr { get; set; }
        public TextReader? StdoutReader { get; set; }
        public TextReader? StderrReader { get; set; }
        public bool StdoutReaderDisposed { get; set; }
        public bool StderrReaderDisposed { get; set; }
        public Exception? Primary { get; set; }
        public List<Exception> CleanupFailures { get; } = [];
        public Exception? DirectoryRemovalFault { get; set; } // Only deterministic fixture controls inject this.
        public bool ExitVerified { get; set; }
        public bool ProcessDisposed { get; set; }
        public bool ReadersDisposed { get; set; }
        public bool DirectoryRemoved { get; set; }
        public bool Released => ProcessDisposed && ReadersDisposed && DirectoryRemoved;
        public Exception? FailureForCaller()
        {
            var failures = (Primary is null ? Enumerable.Empty<Exception>() : new[] { Primary! }).Concat(CleanupFailures).ToArray();
            return failures.Length switch { 0 => null, 1 => failures[0], _ => new AggregateException("Startup fixture failure and owned cleanup evidence.", failures) };
        }
    }

    private sealed class GateReader : TextReader
    {
        private readonly TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsDisposed { get; private set; }
        public void Release() => gate.TrySetResult();
        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            // Deliberately ignores cancellation, proving that a cancellation request is not settlement.
            await gate.Task;
            return 0;
        }
        protected override void Dispose(bool disposing) { IsDisposed = true; base.Dispose(disposing); }
    }

}
