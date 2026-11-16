namespace RaceConditions.LazyTask;

public sealed record Settings(string PaymentEndpoint);

/// <summary>Loads settings from a remote store that can have a short outage.</summary>
public interface ISettingsSource
{
    Task<Settings> LoadAsync();
}

public interface ISettingsProvider
{
    Task<Settings> GetAsync();
}

/// <summary>
/// The usual way to load something once for many concurrent callers. The trap: Lazy caches the
/// Task, including a faulted one. One timeout during startup and every later call fails until
/// the process restarts.
/// </summary>
public sealed class SettingsProvider(ISettingsSource source) : ISettingsProvider
{
    private readonly Lazy<Task<Settings>> _settings = new(source.LoadAsync);

    public Task<Settings> GetAsync() => _settings.Value;
}

/// <summary>
/// Fix: still one load for all concurrent callers, but a failed load is forgotten so the next
/// caller tries again.
/// </summary>
public sealed class SafeSettingsProvider(ISettingsSource source) : ISettingsProvider
{
    private readonly Lock _lock = new();
    private Task<Settings>? _settings;

    public async Task<Settings> GetAsync()
    {
        Task<Settings> current;
        lock (_lock)
        {
            current = _settings ??= source.LoadAsync();
        }

        try
        {
            return await current;
        }
        catch
        {
            lock (_lock)
            {
                if (ReferenceEquals(_settings, current)) _settings = null;
            }
            throw;
        }
    }
}
