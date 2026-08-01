namespace Lyrictified.Server;

public sealed class LrclibCacheAutoCleanService : IHostedService, IDisposable
{
    private readonly TimeSpan _checkInterval;
    private readonly TimeSpan _maxAge;

    private readonly LrclibCacheStore _cacheStore;
    private readonly ILogger<LrclibCacheAutoCleanService> _logger;
    private System.Threading.Timer? _timer;

    public LrclibCacheAutoCleanService(LyrictifiedSettings settings, LrclibCacheStore cacheStore, ILogger<LrclibCacheAutoCleanService> logger)
    {
        var maxAgeDays = Math.Max(1, settings.LrclibCacheAutoCleanMaxAgeDays);
        var checkIntervalHours = Math.Max(1, settings.LrclibCacheAutoCleanCheckIntervalHours);

        _maxAge = TimeSpan.FromDays(maxAgeDays);
        _checkInterval = TimeSpan.FromHours(checkIntervalHours);
        _cacheStore = cacheStore;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("LRCLIB cache auto-clean started. Tracks older than {MaxAge} will be removed.", _maxAge);
        _timer = new System.Threading.Timer(RunCleanup, null, TimeSpan.Zero, _checkInterval);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    private void RunCleanup(object? state)
    {
        try
        {
            var cleaned = _cacheStore.CleanExpired(_maxAge);
            if (cleaned > 0)
            {
                _logger.LogInformation("LRCLIB cache auto-clean finished: {Cleaned} track(s) removed.", cleaned);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LRCLIB cache auto-clean failed.");
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
    }
}
