namespace RaceConditions.ReadAwaitWrite;

/// <summary>Where the points live: a database, a cache, an HTTP service. Every call is I/O.</summary>
public interface IPointsStore
{
    Task<int> GetAsync(string customer);
    Task SaveAsync(string customer, int points);
}

public interface ILoyaltyService
{
    Task AddAsync(string customer, int points);
}

/// <summary>
/// Race without threads: while the first call is awaiting the read, the second call reads the same
/// balance. Both write "balance + points" and one addition is lost.
/// </summary>
public sealed class LoyaltyService(IPointsStore store) : ILoyaltyService
{
    public async Task AddAsync(string customer, int points)
    {
        var balance = await store.GetAsync(customer);
        await store.SaveAsync(customer, balance + points);
    }
}

/// <summary>
/// Fix: an async lock around the whole read-modify-write. <c>lock</c> does not compile here
/// (error CS1996: cannot await in the body of a lock statement), so it is a SemaphoreSlim.
/// </summary>
public sealed class SafeLoyaltyService(IPointsStore store) : ILoyaltyService
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task AddAsync(string customer, int points)
    {
        await _gate.WaitAsync();
        try
        {
            var balance = await store.GetAsync(customer);
            await store.SaveAsync(customer, balance + points);
        }
        finally
        {
            _gate.Release();
        }
    }
}
