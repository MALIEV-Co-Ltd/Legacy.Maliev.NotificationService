using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using YamlDotNet.RepresentationModel;

namespace Legacy.Maliev.NotificationService.Tests.Workflows;

public sealed class ProviderResourceGuardCallerTests
{
    private const string Revision = "7a449b36fe5f4f7b9e436d2c6063321480aa4a3f";
    private const string Marker = "synthetic-resource-value";
    private static readonly string[] Scripts = ["Invoke-JwtSigningResourceScan.ps1", "JwtSigningResourceScanner.ps1"];
    private static readonly string[] ScriptBlobs = ["81f8c805a5a96166faa7e8668cea6ec6515cb802", "2574befb3da295bf30b792e5af106d2d9a1a7dde"];

    [Theory]
    [InlineData("JwtSigningKey", ".resx")]
    [InlineData("JwtSigningKey", ".Designer.cs")]
    [InlineData("JwtSigningKey", ".xml")]
    [InlineData("BrevoApiKey", ".resx")]
    [InlineData("BrevoApiKey", ".Designer.cs")]
    [InlineData("BrevoApiKey", ".xml")]
    public async Task ActualCaller_RejectsTrackedRootResourceFromNestedDirectory(string name, string extension)
    {
        await WithFixture(async fixture =>
        {
            await fixture.Track("Properties/Resources" + extension, Resource(name, extension));
            var result = await fixture.Scan();
            Assert.Equal(1, result.ExitCode);
            Assert.Contains("[jwt-resource-scan] FAILED:", result.Output, StringComparison.Ordinal);
            Assert.DoesNotContain(Marker, result.Output, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task ActualCaller_AcceptsEmptyCredentialsAndOrdinaryNonCredentialResources()
    {
        await WithFixture(async fixture =>
        {
            await fixture.Track("safe.resx", "<root><data name=\"BrevoApiKey\"><value> </value></data>" +
                "<data name=\"JwtSigningKey\"><value /></data><data name=\"Welcome\"><value>Hello</value></data></root>");
            Assert.Equal(0, (await fixture.Scan()).ExitCode);
        });
    }

    [Fact]
    public async Task ActualCaller_IgnoresUntrackedResource()
    {
        await WithFixture(async fixture =>
        {
            await File.WriteAllTextAsync(Path.Combine(fixture.Root, "untracked.resx"), Resource("BrevoApiKey", ".resx"));
            Assert.Equal(0, (await fixture.Scan()).ExitCode);
        });
    }

    [Fact]
    public async Task ActualCaller_RejectsMalformedTrackedResourceWithoutEchoingContents()
    {
        await WithFixture(async fixture =>
        {
            await fixture.Track("malformed.resx", "<root><" + Marker);
            var result = await fixture.Scan();
            Assert.Equal(1, result.ExitCode);
            Assert.Contains("details redacted", result.Output, StringComparison.Ordinal);
            Assert.DoesNotContain(Marker, result.Output, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task ActualCaller_RejectsDeletedTrackedRootResource()
    {
        await WithFixture(async fixture =>
        {
            await fixture.Track("deleted.resx", Resource("BrevoApiKey", ".resx"));
            File.Delete(Path.Combine(fixture.Root, "deleted.resx"));
            Assert.Equal(1, (await fixture.Scan()).ExitCode);
        });
    }

    [Fact]
    public async Task ActualCaller_ExcludesSeparateUntrackedRepository()
    {
        await WithFixture(async fixture =>
        {
            var other = Directory.CreateDirectory(Path.Combine(fixture.Root, "untracked other repository")).FullName;
            Assert.Equal(0, (await fixture.Run("git", other, ["init", "--quiet"])).ExitCode);
            await File.WriteAllTextAsync(Path.Combine(other, "credential.resx"), Resource("BrevoApiKey", ".resx"));
            Assert.Equal(0, (await fixture.Run("git", other, ["add", "--", "credential.resx"])).ExitCode);
            Assert.Equal(0, (await fixture.Scan()).ExitCode);
        });
    }

    [Theory]
    [InlineData("Invoke-JwtSigningResourceScan.ps1")]
    [InlineData("JwtSigningResourceScanner.ps1")]
    public async Task ActualCaller_FailsClosedWhenTrustedHelperIsMissing(string script)
    {
        await WithFixture(async fixture =>
        {
            File.Delete(Path.Combine(fixture.Root, ".dependencies", "Legacy.Maliev.Workflows", "scripts", script));
            var result = await fixture.Scan();
            Assert.Equal(1, result.ExitCode);
            Assert.Contains("trusted scanner unavailable; details redacted", result.Output, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task MetadataChild_TerminalBeforeExecutableObservationJoinsWithoutInventedIdentity()
    {
        await WithFixture(async fixture =>
        {
            var result = await fixture.Run("git", fixture.Root, ["--version"], observeExecutable: async (child, token) =>
            {
                await child.WaitForExitAsync(token);
                throw new Win32Exception("synthetic terminal metadata race");
            });
            Assert.Equal(0, result.ExitCode);
            Assert.StartsWith("git version ", result.Output, StringComparison.Ordinal);
            Assert.True(fixture.TerminalMetadataUnavailable);
            Assert.False(fixture.SignalAttempted);
            Assert.True(fixture.CleanupVerified);
        });
    }

    [Fact]
    public async Task MetadataChild_LiveUnknownIdentityNeverSignalsAndJoinsAfterOwnedInputCloses()
    {
        await WithFixture(async fixture =>
        {
            var failure = new Win32Exception("synthetic live metadata refusal");
            var observed = await Assert.ThrowsAsync<Win32Exception>(() => fixture.Run("pwsh", fixture.Root,
                ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "[Console]::In.ReadLine() | Out-Null"],
                observeExecutable: (child, token) =>
                {
                    Assert.False(child.HasExited);
                    token.Register(() => child.StandardInput.Close());
                    return Task.FromException<string?>(failure);
                }));
            Assert.Same(failure, observed);
            Assert.True(fixture.LiveMetadataUnavailable);
            Assert.False(fixture.SignalAttempted);
            Assert.True(fixture.TerminalWithoutSignalObserved);
            Assert.True(fixture.CleanupVerified);
        });
    }

    private static string Resource(string name, string extension) => extension switch
    {
        ".resx" => $"<root><data name=\"{name}\"><value>{Marker}</value></data></root>",
        ".Designer.cs" => $"/// Looks up a localized string similar to {Marker}.\ninternal static string {name} {{ get {{ return null; }} }}\n",
        ".xml" => $"<doc><members><member name=\"P:Fixture.Resources.{name}\"><summary>Looks up a localized string similar to {Marker}.</summary></member></members></doc>",
        _ => throw new ArgumentOutOfRangeException(nameof(extension)),
    };

    private static async Task WithFixture(Func<CallerFixture, Task> assertion)
    {
        var root = Path.Combine(Path.GetTempPath(), "notification-provider-guard-" + Guid.NewGuid().ToString("N") + " workspace");
        Directory.CreateDirectory(root);
        var fixture = new CallerFixture(root);
        try
        {
            Assert.Equal(0, (await fixture.Run("git", root, ["init", "--quiet"])).ExitCode);
            await fixture.Track("tracked.txt", "safe");
            var repository = FindRepository();
            var trusted = Path.Combine(repository, ".dependencies", "Legacy.Maliev.Workflows");
            Assert.Equal(Revision, (await fixture.Run("git", trusted, ["rev-parse", "HEAD"])).Output.Trim());
            var destination = Directory.CreateDirectory(Path.Combine(root, ".dependencies", "Legacy.Maliev.Workflows", "scripts")).FullName;
            for (var index = 0; index < Scripts.Length; index++)
            {
                var relative = "scripts/" + Scripts[index];
                Assert.Equal(ScriptBlobs[index], (await fixture.Run("git", trusted, ["rev-parse", "HEAD:" + relative])).Output.Trim());
                Assert.Equal(0, (await fixture.Run("git", trusted, ["diff", "--exit-code", "HEAD", "--", relative])).ExitCode);
                File.Copy(Path.Combine(trusted, "scripts", Scripts[index]), Path.Combine(destination, Scripts[index]));
            }
            await assertion(fixture);
        }
        finally
        {
            // Unknown child/reader custody retains the exact directory and ownership receipt for recovery.
            if (fixture.CleanupVerified)
            {
                var full = Path.GetFullPath(root);
                Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(full));
                Assert.StartsWith("notification-provider-guard-", Path.GetFileName(full), StringComparison.Ordinal);
                foreach (var file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(full, recursive: true);
                Assert.False(Directory.Exists(full));
            }
        }
    }

    private static string FindRepository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, ".github", "workflows", "_build-and-test.yml"))) return directory.FullName;
        throw new InvalidOperationException("Notification caller source is unavailable.");
    }

    private sealed record ProcessResult(int ExitCode, string Output);

    private sealed class CallerFixture(string root)
    {
        private static readonly ConcurrentDictionary<Guid, object> RetainedCustody = new();
        private readonly List<object> resources = [];
        public string Root { get; } = root;
        public bool CleanupVerified { get; private set; } = true;
        public bool TerminalMetadataUnavailable { get; private set; }
        public bool LiveMetadataUnavailable { get; private set; }
        public bool SignalAttempted { get; private set; }
        public bool TerminalWithoutSignalObserved { get; private set; }

        public async Task Track(string relative, string content)
        {
            var path = Path.Combine(Root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, content);
            Assert.Equal(0, (await Run("git", Root, ["add", "--", relative])).ExitCode);
        }

        public Task<ProcessResult> Scan()
        {
            var source = File.ReadAllText(Path.Combine(FindRepository(), ".github", "workflows", "_build-and-test.yml"));
            WorkflowContractValidator.Validate(source);
            var yaml = new YamlStream();
            yaml.Load(new StringReader(source));
            var document = (YamlMappingNode)yaml.Documents.Single().RootNode;
            var jobs = (YamlMappingNode)document.Children[new YamlScalarNode("jobs")];
            var job = (YamlMappingNode)jobs.Children[new YamlScalarNode("validate")];
            var steps = (YamlSequenceNode)job.Children[new YamlScalarNode("steps")];
            var step = Assert.Single(steps.Children.Cast<YamlMappingNode>(), node =>
                node.Children.TryGetValue(new YamlScalarNode("name"), out var name) &&
                ((YamlScalarNode)name).Value == "Scan tracked JWT and provider resources");
            var command = ((YamlScalarNode)step.Children[new YamlScalarNode("run")]).Value!;
            var nested = Directory.CreateDirectory(Path.Combine(Root, "nested", "deeper")).FullName;
            return Run("pwsh", nested, ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", command], Root);
        }

        public async Task<ProcessResult> Run(string executable, string directory, string[] arguments, string? workspace = null,
            Func<Process, CancellationToken, Task<string?>>? observeExecutable = null)
        {
            TerminalMetadataUnavailable = false;
            LiveMetadataUnavailable = false;
            TerminalWithoutSignalObserved = false;
            var start = new ProcessStartInfo(executable)
            {
                WorkingDirectory = directory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            if (workspace is not null) start.Environment["GITHUB_WORKSPACE"] = workspace;
            var process = new Process { StartInfo = start };
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            Task<string>? stdout = null, stderr = null;
            int? pid = null;
            DateTime? birth = null;
            string? identity = null;
            var ownerStartedUtc = DateTime.UtcNow;
            var ownerExpiresUtc = ownerStartedUtc.AddSeconds(70);
            var startAttempted = false;
            bool? startResult = null;
            var normalExit = false;
            var forcedTermination = false;
            var cleanup = false;
            Exception? primary = null;
            try
            {
                startAttempted = true;
                startResult = process.Start();
                if (startResult != true) throw new InvalidOperationException("Owned metadata command did not start.");
                _ = process.SafeHandle; // Retain the direct child handle before metadata observation.
                stdout = ReadBounded(process.StandardOutput, deadline.Token);
                stderr = ReadBounded(process.StandardError, deadline.Token);
                pid = process.Id;
                try
                {
                    birth = process.StartTime.ToUniversalTime();
                    if (observeExecutable is not null)
                    {
                        identity = await observeExecutable(process, deadline.Token);
                    }
                    else
                    {
                        // The retained child may not have an observable module immediately after Start.
                        // Wait for an actual identity or natural exit; never invent one or signal an unknown child.
                        while (identity is null && !process.HasExited)
                        {
                            try { identity = process.MainModule?.FileName; }
                            catch (Win32Exception) when (!process.HasExited) { }
                            if (identity is null && !process.HasExited) await Task.Delay(10, deadline.Token);
                        }
                    }
                    if (identity is null) throw new InvalidOperationException("Actual metadata executable is unavailable.");
                }
                catch (Exception error) when (error is InvalidOperationException or Win32Exception)
                {
                    if (!process.HasExited)
                    {
                        LiveMetadataUnavailable = true;
                        throw;
                    }
                    // A terminal retained child needs no signal; actual missing metadata stays null.
                    TerminalMetadataUnavailable = true;
                }
                await process.WaitForExitAsync(deadline.Token);
                var output = await stdout;
                var errorOutput = await stderr;
                normalExit = true;
                // Trusted pwsh uses synchronous Git invocations only; normal script return joins them.
                return new ProcessResult(process.ExitCode, output + errorOutput);
            }
            catch (Exception error)
            {
                primary = error;
                throw;
            }
            finally
            {
                var cleanupFailures = new List<Exception>();
                try { deadline.Cancel(); }
                catch (Exception error) { cleanupFailures.Add(error); }
                try
                {
                    if (startResult == true && !process.HasExited)
                    {
                        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        if (pid is null || birth is null || identity is null)
                        {
                            // The owned stdin gate may close naturally; unknown identity never grants signal authority.
                            await process.WaitForExitAsync(stop.Token);
                        }
                        else
                        {
                            if (HasSignalIdentity(process, pid.Value, birth.Value, identity))
                            {
                                SignalAttempted = true;
                                try { process.CloseMainWindow(); }
                                catch (InvalidOperationException) when (process.HasExited) { }
                                catch (Exception error) { cleanupFailures.Add(error); }
                                using var graceful = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                                try { await process.WaitForExitAsync(graceful.Token); }
                                catch (OperationCanceledException) when (graceful.IsCancellationRequested) { }
                                if (HasSignalIdentity(process, pid.Value, birth.Value, identity))
                                {
                                    try
                                    {
                                        process.Kill(); // Retained exact child only; no recursive selector or process-name lookup.
                                        forcedTermination = true;
                                    }
                                    catch (InvalidOperationException) when (process.HasExited) { }
                                    await process.WaitForExitAsync(stop.Token);
                                }
                            }
                        }
                    }
                }
                catch (Exception error) { cleanupFailures.Add(error); }
                var readers = new[] { stdout, stderr }.OfType<Task<string>>().ToArray();
                try { await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (OperationCanceledException) when (readers.All(task => task.IsCompleted)) { }
                catch (Exception error)
                {
                    if (!ReferenceEquals(error, primary)) cleanupFailures.Add(error);
                }
                try
                {
                    var terminal = startResult == true && process.HasExited;
                    TerminalWithoutSignalObserved |= terminal && !SignalAttempted;
                    cleanup = !startAttempted || startResult == false ||
                        (terminal && !forcedTermination && readers.Length == 2 && readers.All(task => task.IsCompleted));
                }
                catch (Exception error) { cleanupFailures.Add(error); }
                if (cleanup)
                {
                    try { process.Dispose(); }
                    catch (Exception error) { cleanup = false; cleanupFailures.Add(error); }
                }
                if (!cleanup) RetainedCustody.TryAdd(Guid.NewGuid(), new
                {
                    directory = Root,
                    process,
                    stdout,
                    stderr,
                    pid,
                    birth,
                    identity,
                    ownerExpiresUtc,
                    primary,
                    cleanupFailures,
                    purpose = "Unverified metadata-child or reader cleanup; retained for owner recovery",
                });
                CleanupVerified &= cleanup;
                resources.Add(new
                {
                    pid,
                    birthUtc = birth,
                    executable = identity,
                    ownerStartedUtc,
                    expiresUtc = ownerExpiresUtc,
                    startAttempted,
                    startResult,
                    normalExit,
                    terminalMetadataUnavailable = TerminalMetadataUnavailable,
                    liveMetadataUnavailable = LiveMetadataUnavailable,
                    signalAttempted = SignalAttempted,
                    forcedTermination,
                    cleanupVerified = cleanup,
                    primaryFailureType = primary?.GetType().Name,
                    cleanupFailureTypes = cleanupFailures.Select(error => error.GetType().Name).ToArray(),
                    synchronousMetadataChildrenJoined = cleanup && startResult == true,
                    persistentData = false
                });
                try { await File.WriteAllTextAsync(Path.Combine(Root, "ownership.json"), JsonSerializer.Serialize(resources)); }
                catch (Exception error) { cleanupFailures.Add(error); }
                if (cleanupFailures.Count > 0)
                {
                    var failures = new List<Exception>();
                    if (primary is not null) failures.Add(primary);
                    failures.AddRange(cleanupFailures);
                    throw new AggregateException("Owned metadata cleanup failed.", failures);
                }
            }
        }

        private static bool HasSignalIdentity(Process process, int pid, DateTime birth, string executable)
        {
            if (process.HasExited) return false;
            try
            {
                if (process.Id != pid || process.StartTime.ToUniversalTime() != birth || process.MainModule?.FileName != executable)
                    throw new InvalidOperationException("Metadata identity unavailable; termination withheld.");
                return true;
            }
            catch (Exception error) when (error is InvalidOperationException or Win32Exception)
            {
                if (process.HasExited) return false;
                throw;
            }
        }

        private static async Task<string> ReadBounded(TextReader reader, CancellationToken token)
        {
            var result = new StringBuilder();
            var buffer = new char[1024];
            while (true)
            {
                var count = await reader.ReadAsync(buffer.AsMemory(), token);
                if (count == 0) return result.ToString();
                if (result.Length + count > 65536) throw new InvalidDataException("Metadata output exceeded its fixed bound.");
                result.Append(buffer, 0, count);
            }
        }
    }
}
