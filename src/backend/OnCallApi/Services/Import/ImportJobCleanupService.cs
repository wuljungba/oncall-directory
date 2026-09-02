using Microsoft.EntityFrameworkCore;
using OnCallApi.Data;
using OnCallApi.Models;

namespace OnCallApi.Services.Import;

/// <summary>
/// Clears out staged import rows once they can no longer be useful.
///
/// Staging an upload keeps a copy of every row exactly as the file wrote it, which is what
/// makes the mapping correctable and the error report honest. It also means an abandoned
/// upload leaves a full copy of somebody's staff list sitting in the database
/// indefinitely — names, phone numbers and addresses that nobody chose to import and
/// nobody will ever look at again.
///
/// Two windows, for two different reasons:
///
/// - A DRAFT is deleted outright after a week. Nobody returns to a half-finished import
///   after seven days; they upload the file again.
/// - A COMMITTED job keeps its header — who imported what, when, and how many — and loses
///   its staged rows after a month. The header is the record; the rows are a duplicate of
///   data that now lives in the directory proper.
/// </summary>
public class ImportJobCleanupService : BackgroundService
{
    /// <summary>How long an unfinished upload is kept before it is discarded.</summary>
    private static readonly TimeSpan DraftRetention = TimeSpan.FromDays(7);

    /// <summary>How long a committed job's staged rows are kept.</summary>
    private static readonly TimeSpan CommittedRowRetention = TimeSpan.FromDays(30);

    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    /// <summary>
    /// Long enough that startup, schema creation and seeding are done. This is
    /// housekeeping; nothing waits on it.
    /// </summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ImportJobCleanupService> _logger;

    public ImportJobCleanupService(
        IServiceScopeFactory scopeFactory, ILogger<ImportJobCleanupService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CleanupAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Housekeeping must never take the application down. It is reported and
                // retried on the next pass.
                _logger.LogError(ex, "Import job cleanup failed; will retry on the next pass");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task CleanupAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var now = DateTime.UtcNow;

        // ── Abandoned uploads ──
        var staleDrafts = await db.ImportJobs
            .Where(j => j.Status != ImportJobStatus.Committed && j.UpdatedAt < now - DraftRetention)
            .ToListAsync(ct);

        if (staleDrafts.Count > 0)
        {
            // The rows go with the job: ImportJobRows cascades on the foreign key.
            db.ImportJobs.RemoveRange(staleDrafts);
            _logger.LogInformation(
                "Discarding {Count} unfinished import(s) older than {Days} days",
                staleDrafts.Count, DraftRetention.TotalDays);
        }

        // ── Staged rows of finished imports ──
        var settledJobIds = await db.ImportJobs
            .Where(j => j.Status == ImportJobStatus.Committed
                        && j.CommittedAt != null
                        && j.CommittedAt < now - CommittedRowRetention)
            .Select(j => j.Id)
            .ToListAsync(ct);

        if (settledJobIds.Count > 0)
        {
            var rows = await db.ImportJobRows
                .Where(r => settledJobIds.Contains(r.ImportJobId))
                .ToListAsync(ct);

            if (rows.Count > 0)
            {
                db.ImportJobRows.RemoveRange(rows);
                _logger.LogInformation(
                    "Clearing {Rows} staged row(s) from {Jobs} import(s) committed more than "
                    + "{Days} days ago; the job records themselves are kept",
                    rows.Count, settledJobIds.Count, CommittedRowRetention.TotalDays);
            }
        }

        if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(ct);
    }
}
