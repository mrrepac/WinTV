namespace WinTV;

/// <summary>A timeout quarantines the player: it never permits concurrent native calls.</summary>
public sealed class PlayerCommandQueue
{
    readonly object gate = new();
    readonly TimeSpan timeout;
    Task tail = Task.CompletedTask;
    volatile bool faulted;
    public bool IsFaulted => faulted;
    public event Action? Failed;
    public PlayerCommandQueue(TimeSpan? timeout = null) => this.timeout = timeout ?? TimeSpan.FromSeconds(10);

    public Task Run(string name, Action action)
    {
        lock (gate)
        {
            var previous = tail;
            return tail = Task.Run(async () =>
            {
                await previous.ConfigureAwait(false);
                if (faulted) return;
                var operation = Task.Run(action);
                try { await operation.WaitAsync(timeout).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    faulted = true;
                    AppLog.Error("Player operation: " + name, ex);
                    // Observe a late native exception without touching the quarantined player.
                    _ = operation.ContinueWith(t => AppLog.Error("Late player failure", t.Exception!),
                        TaskContinuationOptions.OnlyOnFaulted);
                    Failed?.Invoke();
                }
            });
        }
    }
}
