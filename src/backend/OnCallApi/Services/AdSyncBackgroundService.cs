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
            // Incremental: the timer resumes from each directory's stored cursor. The manual
            // trigger is the one that forces a full re-enumeration.
            var results = await sync.SyncAllAsync(forceFull: false, ct);

            foreach (var failed in results.Where(r => !r.Succeeded))
            {
                _logger.LogWarning(
                    "Directory sync did not complete for tenant {TenantId} ({TenantName}): read {Pages} page(s)",
                    failed.TenantId, failed.TenantName, failed.PagesRead);
            }

            // A refusal is not a failure — the run completed and deliberately declined to apply
            // what it found. It still needs a person, so it is logged at Error on its own.
            foreach (var refused in results.Where(r => r.DeactivationsRefused > 0))
            {
                _logger.LogError(
                    "Directory sync for tenant {TenantId} ({TenantName}) refused to deactivate {Count} staff; "
                    + "nobody was deactivated and the cursor was not advanced",
                    refused.TenantId, refused.TenantName, refused.DeactivationsRefused);
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
