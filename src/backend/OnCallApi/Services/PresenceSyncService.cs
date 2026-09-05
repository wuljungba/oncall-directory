using Microsoft.EntityFrameworkCore;
using OnCallApi.Data;
using OnCallApi.Models;

namespace OnCallApi.Services;

/// <summary>
/// Background service that periodically refreshes Teams presence for all active employees.
/// Runs every 2 minutes by default.
///
/// One cycle is a single projected read, a handful of batched Graph calls, and an UPDATE
/// per person whose presence actually moved. It previously issued one Graph request per
/// employee in sequence, which at directory scale could not finish inside its own interval:
/// 5,000 staff meant 5,000 requests every two minutes, so presence was permanently stale
/// and Graph throttled the account for the privilege.
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

    /// <summary>The three columns a cycle needs to decide whether anything changed.</summary>
    internal sealed record PresenceRow(Guid Id, string AzureAdObjectId, string Presence);

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

    /// <summary>
    /// The employees whose stored presence differs from what Graph just reported.
    ///
    /// An id Graph did not answer for is left alone. That is the whole point of returning a
    /// partial map: a throttled or failed lookup means we learned nothing about that person,
    /// and writing "unknown" over a good value — which the per-user path used to do on every
    /// caught exception — turns a transient Graph error into a directory that tells clinical
    /// staff a reachable colleague is unreachable.
    /// </summary>
    internal static List<(Guid Id, string Presence)> SelectPresenceChanges(
        IEnumerable<PresenceRow> employees,
        IReadOnlyDictionary<string, string> presences)
    {
        var changes = new List<(Guid, string)>();

        foreach (var employee in employees)
        {
            if (string.IsNullOrEmpty(employee.AzureAdObjectId)) continue;
            if (!presences.TryGetValue(employee.AzureAdObjectId, out var latest)) continue;
            if (string.Equals(latest, employee.Presence, StringComparison.Ordinal)) continue;

            changes.Add((employee.Id, latest));
        }

        return changes;
    }

    private async Task SyncPresenceAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _services.CreateScope();
            var graphApi = scope.ServiceProvider.GetRequiredService<IGraphApiService>();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // Projected rather than materialized as entities: a cycle only ever reads three
            // columns, and change-tracking every active employee every two minutes was most
            // of what this service cost the database.
            var employees = await db.Employees
                .Where(e => e.IsActive && !string.IsNullOrEmpty(e.AzureAdObjectId))
                .Select(e => new PresenceRow(e.Id, e.AzureAdObjectId, e.Presence))
                .ToListAsync(ct);

            if (employees.Count == 0) return;

            var presences = await graphApi.GetPresencesAsync(
                employees.Select(e => e.AzureAdObjectId).ToList(), ct);

            if (presences.Count == 0)
            {
                // Every batch failed, or the directory answered for nobody. Worth saying out
                // loud — silence here reads identically to "nobody's presence changed".
                _logger.LogWarning(
                    "Presence sync: Graph returned no presence for any of {Total} employee(s)",
                    employees.Count);
                return;
            }

            var changes = SelectPresenceChanges(employees, presences);
            if (changes.Count == 0) return;

            var now = DateTime.UtcNow;
            foreach (var (id, presence) in changes)
            {
                // Attached as a stub with two properties flagged, so the UPDATE touches only
                // Presence and UpdatedAt and we never load the row we are overwriting.
                var stub = new Employee { Id = id, Presence = presence, UpdatedAt = now };
                db.Employees.Attach(stub);
                db.Entry(stub).Property(e => e.Presence).IsModified = true;
                db.Entry(stub).Property(e => e.UpdatedAt).IsModified = true;
            }

            await db.SaveChangesAsync(ct);
            _logger.LogDebug("Presence sync: {Updated}/{Total} employees updated",
                changes.Count, employees.Count);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // Shutdown, not a failure.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Presence sync cycle failed");
        }
    }
}
