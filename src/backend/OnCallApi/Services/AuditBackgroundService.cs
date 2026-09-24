using OnCallApi.Data;

namespace OnCallApi.Services;

/// <summary>
/// Background service that batch-processes audit log entries from the Channel queue.
/// Writes to the database every 5 seconds or every 100 entries, whichever comes first.
/// </summary>
public class AuditBackgroundService : BackgroundService
{
    /// <summary>How many times a failing flush is retried before its rows are declared lost.</summary>
    private const int MaxFlushAttempts = 3;

    private readonly IServiceProvider _services;
    private readonly ILogger<AuditBackgroundService> _logger;

    public AuditBackgroundService(IServiceProvider services, ILogger<AuditBackgroundService> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Audit background service started");

        var auditService = _services.GetRequiredService<AuditService>();
        var reader = auditService.Reader;
        var batch = new List<OnCallApi.Models.AuditLog>(100);
        var attempt = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Wait for first item with a 5-second timeout
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                cts.CancelAfter(TimeSpan.FromSeconds(5));

                try
                {
                    var item = await reader.ReadAsync(cts.Token);
                    batch.Add(item);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // Timeout with no items — flush any existing batch
                }

                // Drain any additional available items (up to 100)
                while (batch.Count < 100 && reader.TryRead(out var additional))
                {
                    batch.Add(additional);
                }

                if (batch.Count == 0)
                    continue;

                // Batch insert
                using var scope = _services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                db.AuditLogs.AddRange(batch);
                await db.SaveChangesAsync(stoppingToken);

                _logger.LogDebug("Flushed {Count} audit log entries", batch.Count);
                batch.Clear();
                attempt = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A failed write used to clear the batch, so up to 100 PHI-access records
                // vanished on one transient SQL error and nothing recorded which. Retry first:
                // a briefly unreachable or throttled database is the common case, and it
                // recovers on its own.
                attempt++;
                if (attempt <= MaxFlushAttempts)
                {
                    _logger.LogWarning(
                        ex, "Could not flush {Count} audit row(s) (attempt {Attempt}/{Max}); retrying",
                        batch.Count, attempt, MaxFlushAttempts);

                    try { await Task.Delay(TimeSpan.FromSeconds(2 * attempt), stoppingToken); }
                    catch (OperationCanceledException) { break; }

                    continue;   // keep the batch and try it again
                }

                // Out of retries. The rows are gone from SQL either way, so write what each one
                // WAS into the log stream: a line someone can reconstruct beats a silent hole.
                // Identifiers only — never the audited values, which may be PHI.
                _logger.LogCritical(
                    ex, "Giving up on {Count} audit row(s) after {Max} attempts. The audit trail is incomplete.",
                    batch.Count, MaxFlushAttempts);

                foreach (var lost in batch)
                {
                    _logger.LogCritical(
                        "Lost audit row: {Timestamp:o} principal={PrincipalId} action={Action} "
                        + "resource={ResourceType}/{ResourceId} tenant={TenantId}",
                        lost.Timestamp, lost.PrincipalId, lost.Action,
                        lost.ResourceType, lost.ResourceId, lost.TenantId);
                }

                batch.Clear();
                attempt = 0;
            }
        }

        _logger.LogInformation("Audit background service stopped");
    }
}
