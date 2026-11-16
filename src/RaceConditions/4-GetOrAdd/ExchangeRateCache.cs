using System.Collections.Concurrent;

namespace RaceConditions.GetOrAdd;

/// <summary>An expensive, rate-limited call to an exchange rate provider.</summary>
public interface IRateSource
{
    decimal Load(string currency);
}

public interface IExchangeRateCache
{
    decimal Get(string currency);
}

/// <summary>
/// Race: ConcurrentDictionary is thread-safe, but GetOrAdd is not atomic. Two threads that miss
/// the same key at the same time both run the factory; only one result is kept, but the provider
/// was called twice.
/// </summary>
public sealed class ExchangeRateCache(IRateSource source) : IExchangeRateCache
{
    private readonly ConcurrentDictionary<string, decimal> _rates = new();

    public decimal Get(string currency) => _rates.GetOrAdd(currency, source.Load);
}

/// <summary>
/// Fix: store a Lazy. Two threads may still create two Lazy objects, but only the stored one is
/// ever evaluated, and Lazy runs its factory once.
/// </summary>
public sealed class SafeExchangeRateCache(IRateSource source) : IExchangeRateCache
{
    private readonly ConcurrentDictionary<string, Lazy<decimal>> _rates = new();

    public decimal Get(string currency) =>
        _rates.GetOrAdd(currency, key => new Lazy<decimal>(() => source.Load(key))).Value;
}
