using RaceConditions.CheckThenAct;
using RaceConditions.Counter;
using RaceConditions.GetOrAdd;
using RaceConditions.LazyTask;
using RaceConditions.ReadAwaitWrite;
using RaceConditions.TwoInstances;

namespace RaceConditions.Tests;

// Every race below has two tests:
//   "race reproduced" passes because the naive code really loses data, on every run;
//   "fix holds" runs the same scenario against the fixed code.
// A gate between the check and the act forces the interleaving, so nothing depends on luck.

public sealed class CounterTests
{
    private static long Hammer(IPageViewCounter counter, int threads = 8, int incrementsPerThread = 1_000_000)
    {
        using var start = new Barrier(threads);
        var workers = Enumerable.Range(0, threads).Select(_ => Task.Factory.StartNew(() =>
        {
            start.SignalAndWait();
            for (var i = 0; i < incrementsPerThread; i++) counter.Increment();
        }, TaskCreationOptions.LongRunning)).ToArray();
        Task.WaitAll(workers);
        return (long)threads * incrementsPerThread;
    }

    [Fact(DisplayName = "1. Counter: race reproduced, _views++ loses increments")]
    public void Plus_plus_loses_increments()
    {
        var counter = new PageViewCounter();
        var expected = Hammer(counter);
        Assert.True(counter.Views < expected, $"{counter.Views:N0} of {expected:N0} views counted");
    }

    [Fact(DisplayName = "1. Counter: fix holds, Interlocked.Increment counts every view")]
    public void Interlocked_counts_every_increment()
    {
        var counter = new SafePageViewCounter();
        var expected = Hammer(counter);
        Assert.Equal(expected, counter.Views);
    }
}

public sealed class ReadAwaitWriteTests
{
    /// <summary>A store whose reads wait at a gate, so two concurrent callers both read before either writes.</summary>
    private sealed class GatedStore(Gate gate) : IPointsStore
    {
        private readonly Dictionary<string, int> _points = new() { ["ana"] = 100 };
        public async Task<int> GetAsync(string customer)
        {
            var value = _points[customer];
            await gate.ArriveAsync();
            return value;
        }
        public Task SaveAsync(string customer, int points) { _points[customer] = points; return Task.CompletedTask; }
        public int Points(string customer) => _points[customer];
    }

    [Fact(DisplayName = "2. Read, await, write: race reproduced without any extra thread")]
    public async Task Two_awaiting_callers_lose_an_addition()
    {
        var store = new GatedStore(new Gate(callers: 2));
        var service = new LoyaltyService(store);

        await Task.WhenAll(service.AddAsync("ana", 50), service.AddAsync("ana", 50));

        Assert.Equal(150, store.Points("ana")); // 100 + 50 + 50 should be 200
    }

    [Fact(DisplayName = "2. Read, await, write: fix holds, SemaphoreSlim keeps both additions")]
    public async Task Semaphore_keeps_both_additions()
    {
        var store = new GatedStore(new Gate(callers: 2));
        var service = new SafeLoyaltyService(store);

        await Task.WhenAll(service.AddAsync("ana", 50), service.AddAsync("ana", 50));

        Assert.Equal(200, store.Points("ana"));
    }
}

public sealed class CheckThenActTests
{
    /// <summary>The discount booking takes I/O time; the gate holds both callers inside it.</summary>
    private sealed class GatedLedger(Gate gate) : IDiscountLedger
    {
        public int Grants;
        public async Task GrantAsync(string code, string customer)
        {
            await gate.ArriveAsync();
            Interlocked.Increment(ref Grants);
        }
    }

    [Fact(DisplayName = "3. Check-then-act: race reproduced, a single-use coupon is granted twice")]
    public async Task Single_use_coupon_is_granted_twice()
    {
        var ledger = new GatedLedger(new Gate(callers: 2));
        var coupons = new CouponRedemption(ledger);

        var results = await Task.WhenAll(coupons.RedeemAsync("WELCOME10", "ana"), coupons.RedeemAsync("WELCOME10", "dino"));

        Assert.Equal([true, true], results);
        Assert.Equal(2, ledger.Grants);
    }

    [Fact(DisplayName = "3. Check-then-act: fix holds, TryAdd lets exactly one customer redeem")]
    public async Task TryAdd_lets_exactly_one_customer_redeem()
    {
        var ledger = new GatedLedger(new Gate(callers: 2));
        var coupons = new SafeCouponRedemption(ledger);

        var results = await Task.WhenAll(coupons.RedeemAsync("WELCOME10", "ana"), coupons.RedeemAsync("WELCOME10", "dino"));

        Assert.Single(results, r => r);
        Assert.Equal(1, ledger.Grants);
    }
}

public sealed class GetOrAddTests
{
    /// <summary>The provider call waits at a gate, so two threads that missed the key are both inside it.</summary>
    private sealed class GatedRateSource(Gate gate) : IRateSource
    {
        public int Calls;
        public decimal Load(string currency)
        {
            Interlocked.Increment(ref Calls);
            gate.Arrive();
            return 1.9558m;
        }
    }

    private static void TwoThreadsAsk(IExchangeRateCache cache)
    {
        using var start = new Barrier(2);
        var threads = Enumerable.Range(0, 2).Select(_ => Task.Factory.StartNew(() =>
        {
            start.SignalAndWait();
            cache.Get("EUR");
        }, TaskCreationOptions.LongRunning)).ToArray();
        Task.WaitAll(threads);
    }

    [Fact(DisplayName = "4. GetOrAdd: race reproduced, the factory calls the provider twice")]
    public void GetOrAdd_runs_the_factory_twice()
    {
        var source = new GatedRateSource(new Gate(callers: 2));

        TwoThreadsAsk(new ExchangeRateCache(source));

        Assert.Equal(2, source.Calls);
    }

    [Fact(DisplayName = "4. GetOrAdd: fix holds, a Lazy value calls the provider once")]
    public void Lazy_value_calls_the_provider_once()
    {
        var source = new GatedRateSource(new Gate(callers: 2));

        TwoThreadsAsk(new SafeExchangeRateCache(source));

        Assert.Equal(1, source.Calls);
    }
}

public sealed class LazyTaskTests
{
    /// <summary>The first load times out, the remote store is back a moment later.</summary>
    private sealed class FlakySource : ISettingsSource
    {
        public int Calls;
        public async Task<Settings> LoadAsync()
        {
            await Task.Yield();
            if (Interlocked.Increment(ref Calls) == 1) throw new TimeoutException("settings store timed out");
            return new Settings("https://payments.example.com");
        }
    }

    [Fact(DisplayName = "5. Lazy<Task>: race reproduced, one timeout is cached forever")]
    public async Task One_timeout_is_cached_forever()
    {
        var source = new FlakySource();
        var settings = new SettingsProvider(source);

        await Assert.ThrowsAsync<TimeoutException>(settings.GetAsync);
        await Assert.ThrowsAsync<TimeoutException>(settings.GetAsync); // the store is back, but nobody asks it

        Assert.Equal(1, source.Calls);
    }

    [Fact(DisplayName = "5. Lazy<Task>: fix holds, the next call retries after a failure")]
    public async Task Next_call_retries_after_a_failure()
    {
        var source = new FlakySource();
        var settings = new SafeSettingsProvider(source);

        await Assert.ThrowsAsync<TimeoutException>(settings.GetAsync);
        var loaded = await settings.GetAsync();

        Assert.Equal("https://payments.example.com", loaded.PaymentEndpoint);
        Assert.Equal(2, source.Calls);
    }

    [Fact(DisplayName = "5. Lazy<Task>: the fix still loads once for concurrent callers")]
    public async Task Concurrent_callers_share_one_load()
    {
        var source = new FlakySource();
        var settings = new SafeSettingsProvider(source);
        await Assert.ThrowsAsync<TimeoutException>(settings.GetAsync);

        var all = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => settings.GetAsync()));

        Assert.All(all, s => Assert.Equal("https://payments.example.com", s.PaymentEndpoint));
        Assert.Equal(2, source.Calls);
    }
}

public sealed class TwoInstancesTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"coupons-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_file);
    }

    private async Task<(bool A, bool B, int Rows)> TwoInstancesRedeem(Func<CouponDb> openDb)
    {
        await using (var db = openDb()) await db.Database.EnsureCreatedAsync();

        var gate = new Gate(callers: 2);
        var instanceA = new CouponService(openDb, gate.ArriveAsync);
        var instanceB = new CouponService(openDb, gate.ArriveAsync);

        var results = await Task.WhenAll(instanceA.RedeemAsync("WELCOME10", "ana"), instanceB.RedeemAsync("WELCOME10", "dino"));

        await using var check = openDb();
        return (results[0], results[1], check.Redemptions.Count());
    }

    [Fact(DisplayName = "6. Two instances: race reproduced, in-process claims cannot see each other")]
    public async Task Two_instances_both_redeem_the_coupon()
    {
        var (a, b, rows) = await TwoInstancesRedeem(() => CouponDb.Open(_file));

        Assert.True(a && b);
        Assert.Equal(2, rows);
    }

    [Fact(DisplayName = "6. Two instances: fix holds, a unique index lets one instance win")]
    public async Task Unique_index_lets_one_instance_win()
    {
        var (a, b, rows) = await TwoInstancesRedeem(() => CouponDbWithUniqueCode.Open(_file));

        Assert.True(a ^ b);
        Assert.Equal(1, rows);
    }
}
