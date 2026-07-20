using Microsoft.EntityFrameworkCore;
using OnCallApi.Data;

namespace OnCallApi.Services;

public class AdSyncBackgroundService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<AdSyncBackgroundService> _logger;
    private int _intervalMinutes;

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
        await SyncUsersAsync(stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(_intervalMinutes));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await SyncUsersAsync(stoppingToken);
        }
    }

    private async Task SyncUsersAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _services.CreateScope();
            var graphApi = scope.ServiceProvider.GetRequiredService<IGraphApiService>();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // Get delta token from last sync
            var lastSync = await db.Employees.MaxAsync(e => (DateTime?)e.LastSyncedAt, ct);

            // Sync all users (would use delta in production for efficiency)
            var users = await graphApi.SyncUsersAsync(ct);
            var adObjectIds = users.Select(u => u.AzureAdObjectId).ToHashSet();

            // Update existing, add new
            foreach (var user in users)
            {
                var existing = await db.Employees
                    .FirstOrDefaultAsync(e => e.AzureAdObjectId == user.AzureAdObjectId, ct);

                if (existing != null)
                {
                    existing.FirstName = user.FirstName;
                    existing.LastName = user.LastName;
                    existing.Title = user.Title;
                    existing.Email = user.Email;
                    existing.OfficePhone = user.OfficePhone;
                    existing.MobilePhone = user.MobilePhone;
                    existing.OfficeLocation = user.OfficeLocation;
                    existing.LastSyncedAt = DateTime.UtcNow;
                }
                else
                {
                    user.LastSyncedAt = DateTime.UtcNow;
                    db.Employees.Add(user);
                }
            }

            // Mark users not in AD as inactive
            var activeUsers = await db.Employees
                .Where(e => e.IsActive)
                .ToListAsync(ct);

            foreach (var active in activeUsers)
            {
                if (!string.IsNullOrEmpty(active.AzureAdObjectId) && !adObjectIds.Contains(active.AzureAdObjectId))
                {
                    active.IsActive = false;
                    active.UpdatedAt = DateTime.UtcNow;
                }
            }

            await db.SaveChangesAsync(ct);
            _logger.LogInformation("AD sync completed: {Count} users processed", users.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AD sync cycle failed");
        }
    }
}
