using OnCallApi.Models;

namespace OnCallApi.Services;

/// <summary>
/// Runs the AD directory sync on a timer. The sync itself lives in
/// <see cref="AdDirectorySyncService"/> so the manual trigger runs exactly the same code —
/// it previously had its own path that read from Graph and wrote nothing.
/// </summary>
public class AdSyncBackgroundService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<AdSyncBackgroundService> _logger;
    private readonly int _intervalMinutes;

    public AdSyncBackgroundService(IServiceProvider services, IConfiguration config, ILogger<AdSyncBackgroundService> logger)
    {
        _services = services;
        _logger = logger;
        _intervalMinutes = config.GetValue<int>("Sync:AdSyncIntervalMinutes", 15);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AD Sync background service started (interval: {Interval}m)", _intervalMinutes);

        if (_intervalMinutes <= 0)
        {
            _logger.LogInformation("AD Sync is disabled (Sync:AdSyncIntervalMinutes <= 0)");
            return;
        }

        // Run initial sync immediately
        await RunOnceAsync(stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(_intervalMinutes));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunOnceAsync(stoppingToken);
        }
    }

    /// <summary>
    /// One cycle across every connected directory. Delta tokens are read per tenant inside
    /// SyncAllAsync, which is why this no longer fetches one up front — a single token
    /// could only ever have been right for one directory.
    /// </summary>
    private async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _services.CreateScope();
            var sync = scope.ServiceProvider.GetRequiredService<IAdDirectorySyncService>();
            var results = await sync.SyncAllAsync(ct);

            foreach (var failed in results.Where(r => !r.Succeeded))
            {
                _logger.LogWarning(
                    "Directory sync did not complete for tenant {TenantId} ({TenantName})",
                    failed.TenantId, failed.TenantName);
            }
        }
        catch (Exception ex)
        {
            // A scheduled run has nobody to report to, so this stays a log. What it must not
            // do is hide a whole batch failing over one unusable record — the sync now skips
            // those individually and names them, rather than rolling everything back here.
            _logger.LogError(ex, "AD delta sync cycle failed");
        }
    }

    /// <inheritdoc cref="AdDirectorySyncService.SelectEmployeesToDeactivate"/>
    internal static List<Employee> SelectEmployeesToDeactivate(
        IEnumerable<Employee> activeEmployees, HashSet<string> adObjectIds) =>
        AdDirectorySyncService.SelectEmployeesToDeactivate(activeEmployees, adObjectIds);
}
