namespace OnCallApi.Services.Dispatch;

/// <summary>
/// A queued emergency dispatch request.
///
/// Carries only the identifiers needed to reload fresh data at processing time —
/// the dispatch background service re-fetches the event from the database rather
/// than trusting a possibly-stale entity captured at enqueue time.
/// </summary>
public sealed record DispatchJob(int EventId, string CodeType);

/// <summary>
/// Snapshot of the dispatch queue for observability.
/// Exposed via GET /api/phone-trees/dispatch/status.
/// </summary>
public sealed record DispatchQueueStatus(
    int PendingCount,
    int Capacity,
    int ProcessingCount,
    long TotalProcessed,
    int LastProcessedEventId,
    DateTime? LastProcessedAtUtc);
