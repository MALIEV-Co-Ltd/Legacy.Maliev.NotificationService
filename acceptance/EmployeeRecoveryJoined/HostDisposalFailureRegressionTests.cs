using Xunit;

public sealed class HostDisposalFailureRegressionTests
{
    [Fact]
    public async Task DependentsReleaseOnlyAfterEverySuccessfulHostDisposal()
    {
        var events = new List<string>();
        var gate = new HostDisposalGate(() => throw new InvalidOperationException("Unexpected failed disposal"));
        Task Dispose(string name) { events.Add(name); return Task.CompletedTask; }
        var released = await gate.DisposeBeforeDependentsAsync(
            new (string, Func<Task>)[] { ("bff", () => Dispose("bff")), ("notification", () => Dispose("notification")), ("auth", () => Dispose("auth")) },
            TimeSpan.FromMilliseconds(20), () =>
            {
                Assert.True(gate.AllHostsCompletedSuccessfully);
                events.AddRange(new[] { "pools", "redis", "postgres" });
                return Task.CompletedTask;
            });
        Assert.True(released);
        Assert.True(gate.DependentsReleased);
        Assert.Equal(new[] { "bff", "notification", "auth", "pools", "redis", "postgres" }, events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrTimedOutDisposalPreservesPoolsAndContainers(bool timeout)
    {
        var poisonCount = 0;
        var dependentReleases = 0;
        var hostAttempts = new List<string>();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new HostDisposalGate(() => poisonCount++);
        Task Dispose(string name)
        {
            hostAttempts.Add(name);
            return name != "auth" ? Task.CompletedTask : timeout ? pending.Task
                : Task.FromException(new InvalidOperationException("controlled host disposal failure"));
        }
        var released = await gate.DisposeBeforeDependentsAsync(
            new (string, Func<Task>)[] { ("bff", () => Dispose("bff")), ("notification", () => Dispose("notification")), ("auth", () => Dispose("auth")) },
            TimeSpan.FromMilliseconds(20), () => { dependentReleases++; return Task.CompletedTask; });
        Assert.Equal(new[] { "bff", "notification", "auth" }, hostAttempts);
        Assert.False(released);
        Assert.False(gate.DependentsReleased);
        Assert.False(gate.AllHostsCompletedSuccessfully);
        Assert.Equal(0, dependentReleases);
        Assert.True(poisonCount > 0);
        Assert.Single(gate.Failures);
        if (timeout)
        {
            Assert.False(pending.Task.IsCompleted); // WaitAsync did not cancel the disposal.
            pending.SetResult();
            await pending.Task;
            Assert.Equal(0, dependentReleases); // Late completion cannot trigger deferred removal.
        }
        // This tests the actual shared gate, not worker substitutes or runtime absence.
    }
}
