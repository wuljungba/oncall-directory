using OnCallApi.Data;
using OnCallApi.Models;

namespace OnCallApi.Services.Dispatch;

/// <summary>
/// Consumes <see cref="DispatchJob"/>s from the <see cref="DispatchJobQueue"/>
/// and runs each through the dispatch pipeline in a dedicated scope.
///
/// Registered both as a singleton (so the controller can report live queue
/// status) and as a hosted service (via the singleton factory).
/// </summary>
public sealed class DispatchBackgroundService : BackgroundService
{
    private readonly DispatchJobQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DispatchBackgroundService> _logger;

    private int _processingCount;
    private long _totalProcessed;
    private int _lastProcessedEventId;
    private DateTime? _lastProcessedAtUtc;

    public DispatchBackgroundService(
        DispatchJobQueue queue,
        IServiceScopeFactory scopeFactory,
        ILogger<DispatchBackgroundService> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>Snapshot of queue depth and processing progress for observability.</summary>
    public DispatchQueueStatus GetStatus() => new(
        PendingCount: _queue.PendingCount,
        Capacity: _queue.Capacity,
        ProcessingCount: _processingCount,
        TotalProcessed: _totalProcessed,
        LastProcessedEventId: _lastProcessedEventId,
        LastProcessedAtUtc: _lastProcessedAtUtc);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in _queue.ReadAllAsync(stoppingToken))
        {
            Interlocked.Increment(ref _processingCount);
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var dispatch = scope.ServiceProvider.GetRequiredService<ICodeCallDispatchService>();
                await dispatch.ProcessDispatchJobAsync(job.EventId, job.CodeType);

                Interlocked.Increment(ref _totalProcessed);
                Interlocked.Exchange(ref _lastProcessedEventId, job.EventId);
                _lastProcessedAtUtc = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                // Safety-critical path: never swallow a dispatch failure silently.
                _logger.LogError(ex, "Dispatch job for event {EventId} failed in pipeline", job.EventId);
                await TryRecordFailureStepAsync(job.EventId, ex.Message, stoppingToken);
            }
            finally
            {
                Interlocked.Decrement(ref _processingCount);
            }
        }
    }

    private async Task TryRecordFailureStepAsync(int eventId, string detail, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.DispatchSteps.Add(new DispatchStep
            {
                PhoneTreeEventId = eventId,
                StepKey = "pipeline_error",
                Status = "failed",
                CompletedAt = DateTime.UtcNow,
                Detail = $"Dispatch pipeline error: {detail}",
            });
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // Last resort — log loudly; there is nothing left to try.
            _logger.LogError(ex, "Failed to record pipeline error step for event {EventId}", eventId);
        }
    }
}
