using OnCallApi.Data;

namespace OnCallApi.Services;

/// <summary>
/// Background service that batch-processes audit log entries from the Channel queue.
/// Writes to the database every 5 seconds or every 100 entries, whichever comes first.
/// </summary>
public class AuditBackgroundService : BackgroundService
{
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
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error flushing audit log batch");
                batch.Clear();
            }
        }

        _logger.LogInformation("Audit background service stopped");
    }
}
