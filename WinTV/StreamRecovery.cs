namespace WinTV;

// Owned by the window dispatcher. All deadlines use monotonic milliseconds.
public sealed class StreamRecovery
{
    public const int MaxRetries = 3;
    enum Phase { Idle, Loading, Playing, Paused, RetryDelay, Finished }
    Phase phase;
    long since;
    bool started;

    public int Retries { get; private set; }
    public bool WaitingToRetry => phase == Phase.RetryDelay;
    public bool CanReselect => phase is Phase.RetryDelay or Phase.Finished;
    public bool AcceptEvents => phase is Phase.Loading or Phase.Playing or Phase.Paused;
    public bool LoadExpired(long now) => phase == Phase.Loading && now - since >= 20_000;
    public bool RetryDue(long now) => phase == Phase.RetryDelay && now - since >= 2_000;
    public void Reset() { phase = Phase.Idle; Retries = 0; }
    public void Begin(long now) { started = false; Set(Phase.Loading, now); }
    public void Playing(long now)
    {
        if (!AcceptEvents) return;
        started = true;
        if (phase != Phase.Playing) Set(Phase.Playing, now);
    }
    public void Buffering(bool complete, long now)
    {
        if (!AcceptEvents || phase == Phase.Paused) return;
        if (complete && started) Playing(now);
        if (!complete && phase == Phase.Playing)
        {
            CreditStablePlayback(now);
            Set(Phase.Loading, now);
        }
    }
    public void Pause(long now)
    {
        if (!AcceptEvents) return;
        CreditStablePlayback(now);
        Set(Phase.Paused, now);
    }
    public void Finish() => phase = Phase.Finished;
    public void Fail(bool retryLive, long now)
    {
        if (!AcceptEvents) return;
        CreditStablePlayback(now);
        if (retryLive && Retries < MaxRetries)
        {
            Retries++;
            Set(Phase.RetryDelay, now);
        }
        else Finish();
    }
    void CreditStablePlayback(long now)
    {
        if (phase == Phase.Playing && now - since >= 30_000) Retries = 0;
    }
    void Set(Phase next, long now) { phase = next; since = now; }
}
