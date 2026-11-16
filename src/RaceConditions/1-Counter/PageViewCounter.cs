namespace RaceConditions.Counter;

public interface IPageViewCounter
{
    void Increment();
    long Views { get; }
}

/// <summary>
/// Race: <c>_views++</c> is three steps (read, add, write). Two threads read the same value and one
/// increment is lost.
/// </summary>
public sealed class PageViewCounter : IPageViewCounter
{
    private long _views;

    public void Increment() => _views++;

    public long Views => _views;
}

/// <summary>Fix: one atomic CPU instruction for the read, add and write.</summary>
public sealed class SafePageViewCounter : IPageViewCounter
{
    private long _views;

    public void Increment() => Interlocked.Increment(ref _views);

    public long Views => Interlocked.Read(ref _views);
}
