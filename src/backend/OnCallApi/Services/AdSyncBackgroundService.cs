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
        await SyncUsersDeltaAsync(null, stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(_intervalMinutes));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            string? deltaToken = null;
            using (var scope = _services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var setting = await db.AppSettings
                    .FirstOrDefaultAsync(s => s.Key == "AdDeltaToken", stoppingToken);
                deltaToken = setting?.Value;
            }

            await SyncUsersDeltaAsync(deltaToken, stoppingToken);
        }
    }

    private async Task SyncUsersDeltaAsync(string? deltaToken, CancellationToken ct)
    {
        try
        {
            using var scope = _services.CreateScope();
            var graphApi = scope.ServiceProvider.GetRequiredService<IGraphApiService>();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // Use delta query for efficiency: first call returns all users,
            // subsequent calls with delta token return only changes
            var (users, newDeltaToken) = await graphApi.SyncUsersDeltaAsync(deltaToken, ct);

            if (users.Count == 0 && deltaToken != null)
            {
                // No changes since last sync — just update the token
                _logger.LogInformation("AD delta sync: no changes");
                await StoreDeltaTokenAsync(db, newDeltaToken, ct);
                return;
            }

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
                    existing.Source = "Ad";
                    existing.LastSyncedAt = DateTime.UtcNow;
                }
                else
                {
                    user.Source = "Ad";
                    user.LastSyncedAt = DateTime.UtcNow;
                    db.Employees.Add(user);
                }
            }

            // Deactivate AD-managed employees that disappeared from AD.
            //
            // Only records explicitly marked Source="Ad" (i.e. users this AD sync has itself
            // created/confirmed) are eligible. Locally-created records — CSV imports, manual
            // adds, and synthetic csv-import-* ids — are never deactivated by AD sync, no
            // matter what value their AzureAdObjectId happens to carry. This is what keeps a
            // CSV import from silently vanishing 15 minutes later.
            var activeUsers = await db.Employees
                .Where(e => e.IsActive && e.Source == "Ad")
                .ToListAsync(ct);

            foreach (var active in activeUsers)
            {
                if (adObjectIds.Contains(active.AzureAdObjectId)) continue;

                active.IsActive = false;
                active.UpdatedAt = DateTime.UtcNow;
            }

            await db.SaveChangesAsync(ct);

            // Store the delta token for next sync
            if (newDeltaToken != null)
            {
                await StoreDeltaTokenAsync(db, newDeltaToken, ct);
            }

            _logger.LogInformation("AD delta sync completed: {Count} users processed", users.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AD delta sync cycle failed");
        }
    }

    private static async Task StoreDeltaTokenAsync(AppDbContext db, string? deltaToken, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(deltaToken)) return;

        var setting = await db.AppSettings
            .FirstOrDefaultAsync(s => s.Key == "AdDeltaToken", ct);
        if (setting != null)
        {
            setting.Value = deltaToken;
        }
        else
        {
            db.AppSettings.Add(new Models.AppSetting
            {
                Key = "AdDeltaToken",
                Value = deltaToken,
                Description = "Azure AD Graph API delta token for incremental user sync"
            });
        }
        await db.SaveChangesAsync(ct);
    }
}
