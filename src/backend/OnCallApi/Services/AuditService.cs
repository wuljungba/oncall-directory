using System.Threading.Channels;
using OnCallApi.Models;

namespace OnCallApi.Services;

/// <summary>
/// Channel-based audit log producer. Enqueues entries for batch processing
/// by AuditBackgroundService, keeping the request pipeline fast.
/// </summary>
public class AuditService : IAuditService
{
    private readonly ILogger<AuditService> _logger;

    /// <summary>
    /// How many PHI-access records this process has dropped because the queue was full.
    ///
    /// It should be zero. It is reported rather than inferred because the alternative — the
    /// state this replaced — was a queue that silently discarded its oldest entries and left
    /// no trace that it had: the audit trail simply had holes in it, and nothing anywhere
    /// said so. A gap you can measure is a different thing from a gap you cannot see.
    /// </summary>
    private long _dropped;

    public AuditService(ILogger<AuditService> logger) => _logger = logger;

    private readonly Channel<AuditLog> _channel = Channel.CreateBounded<AuditLog>(new BoundedChannelOptions(2000)
    {
        // DropWrite, not DropOldest. Both lose a record when the queue is full, but this one
        // loses the newest — the write that is still on the stack and can be reported with its
        // own context — rather than quietly evicting an older record that was already accepted.
        FullMode = BoundedChannelFullMode.DropWrite
    });

    public ChannelReader<AuditLog> Reader => _channel.Reader;

    public void Enqueue(AuditLog log)
    {
        if (_channel.Writer.TryWrite(log)) return;

        // Never throws: failing to record an access must not also fail the request that made
        // it. But it is an Error, because a dropped row is a HIPAA record that no longer
        // exists and no later process can reconstruct it.
        var total = Interlocked.Increment(ref _dropped);
        _logger.LogError(
            "Audit queue full — dropped an access record for {Action} on {ResourceType} ({Dropped} dropped this process). "
            + "The audit trail is incomplete from here.",
            log.Action, log.ResourceType, total);
    }
}
