using Microsoft.EntityFrameworkCore;
using OnCallApi.Data;
using OnCallApi.Models;

namespace OnCallApi.Services;

/// <summary>
/// Batch-writes sign-in observations from the channel queue, mirroring
/// <see cref="AuditBackgroundService"/>. Flushes every 5 seconds or every 100 entries,
/// whichever comes first.
/// </summary>
public class IdentityDirectoryBackgroundService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<IdentityDirectoryBackgroundService> _logger;

    public IdentityDirectoryBackgroundService(
        IServiceProvider services, ILogger<IdentityDirectoryBackgroundService> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Identity directory background service started");

        var directory = _services.GetRequiredService<IdentityDirectoryService>();
        var reader = directory.Reader;
        var batch = new List<SignInObservation>(100);
        var lastPrune = DateTime.UtcNow;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                cts.CancelAfter(TimeSpan.FromSeconds(5));

                try
                {
                    batch.Add(await reader.ReadAsync(cts.Token));
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // Timeout with no items — fall through to the prune below.
                }

                while (batch.Count < 100 && reader.TryRead(out var additional))
                    batch.Add(additional);

                if (DateTime.UtcNow - lastPrune > IdentityDirectoryService.ThrottleWindow)
                {
                    directory.PruneThrottleCache(DateTime.UtcNow);
                    lastPrune = DateTime.UtcNow;
                }

                if (batch.Count == 0)
                    continue;

                await FlushAsync(batch, stoppingToken);
                batch.Clear();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Never fatal: this is a convenience directory, not an authorization store.
                _logger.LogError(ex, "Error flushing sign-in identity batch");
                batch.Clear();
            }
        }

        _logger.LogInformation("Identity directory background service stopped");
    }

    private async Task FlushAsync(List<SignInObservation> batch, CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Collapse duplicates within the batch, keeping the most recent sighting.
        var latest = batch
            .GroupBy(o => (o.Provider, o.ExternalObjectId))
            .Select(g => g.OrderByDescending(o => o.SeenAt).First())
            .ToList();

        var keys = latest.Select(o => o.ExternalObjectId).ToList();
        var existing = await db.SignInIdentities
            .Where(i => keys.Contains(i.ExternalObjectId))
            .ToListAsync(ct);

        foreach (var obs in latest)
        {
            var row = existing.FirstOrDefault(i =>
                i.Provider == obs.Provider && i.ExternalObjectId == obs.ExternalObjectId);

            if (row == null)
            {
                db.SignInIdentities.Add(new SignInIdentity
                {
                    Provider = obs.Provider,
                    ExternalObjectId = obs.ExternalObjectId,
                    Email = obs.Email,
                    DisplayName = obs.DisplayName,
                    LastTenantIdClaim = obs.TenantIdClaim,
                    FirstSeenAt = obs.SeenAt,
                    LastSeenAt = obs.SeenAt,
                });
            }
            else
            {
                if (obs.SeenAt > row.LastSeenAt) row.LastSeenAt = obs.SeenAt;
                // Refresh the display fields: people change names and email addresses.
                if (!string.IsNullOrWhiteSpace(obs.Email)) row.Email = obs.Email;
                if (!string.IsNullOrWhiteSpace(obs.DisplayName)) row.DisplayName = obs.DisplayName;
                if (!string.IsNullOrWhiteSpace(obs.TenantIdClaim)) row.LastTenantIdClaim = obs.TenantIdClaim;
            }
        }

        await db.SaveChangesAsync(ct);
        _logger.LogDebug("Flushed {Count} sign-in identity records", latest.Count);
    }
}
