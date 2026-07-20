using System.Threading.Channels;
using OnCallApi.Models;

namespace OnCallApi.Services;

/// <summary>
/// Channel-based audit log producer. Enqueues entries for batch processing
/// by AuditBackgroundService, keeping the request pipeline fast.
/// </summary>
public class AuditService : IAuditService
{
    private readonly Channel<AuditLog> _channel = Channel.CreateBounded<AuditLog>(new BoundedChannelOptions(2000)
    {
        FullMode = BoundedChannelFullMode.DropOldest
    });

    public ChannelReader<AuditLog> Reader => _channel.Reader;

    public void Enqueue(AuditLog log)
    {
        _channel.Writer.TryWrite(log);
    }
}
