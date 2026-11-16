namespace RaceConditions.Tests;

/// <summary>
/// The tool that makes a race reproducible: every caller that reaches the gate waits until the
/// expected number of callers has arrived, then all continue together. Put it between the check
/// and the act, and two callers are guaranteed to interleave there on every run.
/// If fewer callers arrive within the timeout (because the code under test serializes them, which
/// is exactly what a fix does), the gate opens anyway, so a correct implementation never hangs.
/// </summary>
public sealed class Gate(int callers, TimeSpan? timeout = null)
{
    private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromMilliseconds(300);
    private int _arrived;

    public int Arrived => Volatile.Read(ref _arrived);

    public async Task ArriveAsync()
    {
        if (Interlocked.Increment(ref _arrived) >= callers) _open.TrySetResult();
        await Task.WhenAny(_open.Task, Task.Delay(_timeout));
    }

    public void Arrive()
    {
        if (Interlocked.Increment(ref _arrived) >= callers) _open.TrySetResult();
        _open.Task.Wait(_timeout);
    }
}
