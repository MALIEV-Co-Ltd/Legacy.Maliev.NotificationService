public sealed class HostDisposalGate(Action poisonAdmission)
{
    private readonly List<(string Name, Task Disposal)> attempts = [];
    public List<string> Failures { get; } = [];
    public bool DependentsReleased { get; private set; }
    public bool AllHostsCompletedSuccessfully => attempts.All(item => item.Disposal.IsCompletedSuccessfully);
    public object[] Snapshot() => attempts.Select(item => (object)new
    {
        host = item.Name,
        status = item.Disposal.Status.ToString(),
        disposalCompletedSuccessfully = item.Disposal.IsCompletedSuccessfully,
    }).ToArray();

    public async Task<bool> DisposeBeforeDependentsAsync(
        IEnumerable<(string Name, Func<Task> Dispose)> hosts,
        TimeSpan timeout,
        Func<Task> releaseDependents)
    {
        foreach (var host in hosts)
        {
            Task disposal;
            try { disposal = host.Dispose(); }
            catch (Exception exception) { disposal = Task.FromException(exception); }
            attempts.Add((host.Name, disposal));
            try { await disposal.WaitAsync(timeout); }
            catch (Exception exception)
            {
                poisonAdmission();
                Failures.Add(host.Name + ":" + exception.GetType().Name);
                // Observe a later failure without claiming the underlying disposal was cancelled.
                _ = disposal.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        if (Failures.Count != 0 || !AllHostsCompletedSuccessfully)
        {
            poisonAdmission();
            return false;
        }
        await releaseDependents();
        DependentsReleased = true;
        return true;
    }
}
