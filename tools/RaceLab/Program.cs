// How often does an ordinary test catch the race? Each experiment runs the NAIVE code the way a typical
// test does (start the calls in parallel, check the result, no gate) and counts the runs that show the bug.
//   dotnet run -c Release --project tools/RaceLab
using System.Globalization;
using RaceConditions.CheckThenAct;
using RaceConditions.Counter;
using RaceConditions.GetOrAdd;
using RaceConditions.ReadAwaitWrite;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
const int Runs = 1_000;

Console.WriteLine($".NET {Environment.Version}, {Environment.ProcessorCount} logical cores, {Runs:N0} runs per experiment");
Console.WriteLine();
Console.WriteLine($"{"Experiment",-62} {"Race caught",12}");
Console.WriteLine(new string('-', 75));

async Task Report(string name, Func<Task<bool>> raceShowsUp)
{
    int caught = 0, crashed = 0;
    for (var i = 0; i < Runs; i++)
    {
        try
        {
            if (await raceShowsUp()) caught++;
        }
        catch (InvalidOperationException)
        {
            // A non-concurrent collection (HashSet, Dictionary) noticed it was changed by two threads
            // at once and refused to continue. That is the same race, surfacing as a crash.
            caught++;
            crashed++;
        }
    }
    var note = crashed > 0 ? $"  ({crashed} as a corrupted collection)" : string.Empty;
    Console.WriteLine($"{name,-62} {caught,5} / {Runs:N0}{note}");
}

// 1. Counter: two threads, 1,000 increments each.
await Report("1. _views++, 2 threads x 1,000 increments", () =>
{
    var counter = new PageViewCounter();
    Parallel.For(0, 2, _ => { for (var i = 0; i < 1_000; i++) counter.Increment(); });
    return Task.FromResult(counter.Views < 2_000);
});

// 2. Read, await, write: the usual in-memory fake completes synchronously.
await Report("2. Read-await-write, in-memory fake (Task.FromResult)", async () =>
{
    var store = new MemoryStore(yieldOnRead: false);
    var service = new LoyaltyService(store);
    await Task.WhenAll(service.AddAsync("ana", 50), service.AddAsync("ana", 50));
    return store.Points < 200;
});

await Report("2. Read-await-write, fake with one await Task.Yield()", async () =>
{
    var store = new MemoryStore(yieldOnRead: true);
    var service = new LoyaltyService(store);
    await Task.WhenAll(service.AddAsync("ana", 50), service.AddAsync("ana", 50));
    return store.Points < 200;
});

// 3. Check-then-act coupon.
await Report("3. Coupon check-then-act, in-memory ledger (Task.CompletedTask)", async () =>
{
    var ledger = new CountingLedger(yieldOnGrant: false);
    var coupons = new CouponRedemption(ledger);
    await Task.WhenAll(coupons.RedeemAsync("WELCOME10", "ana"), coupons.RedeemAsync("WELCOME10", "dino"));
    return ledger.Grants > 1;
});

await Report("3. Coupon check-then-act, ledger with one await Task.Yield()", async () =>
{
    var ledger = new CountingLedger(yieldOnGrant: true);
    var coupons = new CouponRedemption(ledger);
    await Task.WhenAll(coupons.RedeemAsync("WELCOME10", "ana"), coupons.RedeemAsync("WELCOME10", "dino"));
    return ledger.Grants > 1;
});

// 4. GetOrAdd factory: two threads ask for the same missing key.
await Report("4. GetOrAdd, two threads, fast factory", () =>
{
    var source = new CountingRateSource();
    var cache = new ExchangeRateCache(source);
    Parallel.For(0, 2, _ => cache.Get("EUR"));
    return Task.FromResult(source.Calls > 1);
});

Console.WriteLine();
Console.WriteLine("The gated tests in tests/RaceConditions.Tests catch every one of these races on every run.");

sealed class MemoryStore(bool yieldOnRead) : IPointsStore
{
    private int _points = 100;
    public int Points => _points;
    public async Task<int> GetAsync(string customer)
    {
        var value = _points;
        if (yieldOnRead) await Task.Yield();
        return value;
    }
    public Task SaveAsync(string customer, int points) { _points = points; return Task.CompletedTask; }
}

sealed class CountingLedger(bool yieldOnGrant) : IDiscountLedger
{
    private int _grants;
    public int Grants => _grants;
    public async Task GrantAsync(string code, string customer)
    {
        if (yieldOnGrant) await Task.Yield();
        Interlocked.Increment(ref _grants);
    }
}

sealed class CountingRateSource : IRateSource
{
    private int _calls;
    public int Calls => _calls;
    public decimal Load(string currency) { Interlocked.Increment(ref _calls); return 1.9558m; }
}
