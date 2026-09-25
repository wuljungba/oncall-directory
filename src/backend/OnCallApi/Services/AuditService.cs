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

    private readonly Channel<AuditLog> _channel;

    public AuditService(ILogger<AuditService> logger)
    {
        _logger = logger;

        // The itemDropped callback is the whole point, and the reason the channel is built here
        // rather than in a field initializer.
        //
        // With DropWrite or DropOldest, Writer.TryWrite returns TRUE even when the item was
        // discarded — the channel considers "dropped it as configured" a successful write. So
        // checking its return value detects nothing, and a first attempt at this logged nothing
        // at all while believing it logged everything. This overload hands back the item that
        // was actually dropped, which is the only reliable signal.
        _channel = Channel.CreateBounded<AuditLog>(
            new BoundedChannelOptions(2000)
            {
                // DropWrite, not DropOldest: lose the newest write, which is still on the stack
                // and can be reported with its own context, rather than silently evicting a
                // record that was already accepted.
                FullMode = BoundedChannelFullMode.DropWrite,
            },
            OnDropped);
    }

    private void OnDropped(AuditLog dropped)
    {
        var total = Interlocked.Increment(ref _dropped);
        _logger.LogError(
            "Audit queue full — dropped an access record for {Action} on {ResourceType} "
            + "({Dropped} dropped this process). The audit trail is incomplete from here.",
            dropped.Action, dropped.ResourceType, total);
    }

    public ChannelReader<AuditLog> Reader => _channel.Reader;

    /// <summary>
    /// Never throws. Failing to record an access must not also fail the request that made it —
    /// a full queue is a degraded audit trail, not an outage. Anything discarded is reported
    /// through <see cref="OnDropped"/>.
    /// </summary>
    public void Enqueue(AuditLog log) => _channel.Writer.TryWrite(log);
}
