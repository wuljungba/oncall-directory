using Microsoft.EntityFrameworkCore;
using OnCallApi.Data;

namespace OnCallApi.Services;

/// <summary>
/// Background service that periodically refreshes Teams presence for all active employees.
/// Runs every 2 minutes by default. Each cycle queries Graph API for current presence
/// and updates the local database, which the frontend picks up on the next page load.
/// </summary>
public class PresenceSyncService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<PresenceSyncService> _logger;
    private readonly int _intervalSeconds;

    public PresenceSyncService(IServiceProvider services, IConfiguration config, ILogger<PresenceSyncService> logger)
    {
        _services = services;
        _logger = logger;
        var intervalMin = config.GetValue<int>("Sync:PresenceSyncIntervalMinutes", 2);
        _intervalSeconds = Math.Max(intervalMin, 1) * 60;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait 10 seconds before first sync to let the app initialize
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_intervalSeconds));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await SyncPresenceAsync(stoppingToken);
        }
    }

    private async Task SyncPresenceAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _services.CreateScope();
            var graphApi = scope.ServiceProvider.GetRequiredService<IGraphApiService>();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // Get active employees with an Azure AD object ID
            var employees = await db.Employees
                .Where(e => e.IsActive && !string.IsNullOrEmpty(e.AzureAdObjectId))
                .ToListAsync(ct);

            if (employees.Count == 0) return;

            var updated = 0;
            foreach (var employee in employees)
            {
                if (string.IsNullOrEmpty(employee.AzureAdObjectId)) continue;

                try
                {
                    var presence = await graphApi.GetUserPresenceAsync(employee.AzureAdObjectId, ct);
                    if (presence != null && presence != employee.Presence)
                    {
                        employee.Presence = presence;
                        employee.UpdatedAt = DateTime.UtcNow;
                        updated++;
                    }
                }
                catch
                {
                    // Individual failures shouldn't block the whole batch
                }
            }

            if (updated > 0)
            {
                await db.SaveChangesAsync(ct);
                _logger.LogDebug("Presence sync: {Updated}/{Total} employees updated", updated, employees.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Presence sync cycle failed");
        }
    }
}
