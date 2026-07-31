using System.Threading.Channels;

namespace OnCallApi.Services.Dispatch;

/// <summary>
/// Bounded channel that queues emergency dispatch jobs for the
/// <see cref="DispatchBackgroundService"/> to process.
///
/// Uses <see cref="BoundedChannelFullMode.Wait"/> so enqueueing never drops a
/// job: if the queue is full the producer awaits space (backpressure). This is
/// deliberate — dispatch is safety-critical and a lost code-call is worse than
/// a briefly delayed response.
///
/// Registered as a singleton; producers (controllers/services) enqueue, and the
/// single background consumer reads jobs sequentially.
/// </summary>
public sealed class DispatchJobQueue
{
    public const int DefaultCapacity = 512;

    private readonly Channel<DispatchJob> _channel;

    public DispatchJobQueue()
    {
        _channel = Channel.CreateBounded<DispatchJob>(new BoundedChannelOptions(DefaultCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    /// <summary>Enqueue a dispatch job. Awaits capacity if the queue is full (never drops).</summary>
    public ValueTask EnqueueAsync(DispatchJob job, CancellationToken cancellationToken = default) =>
        _channel.Writer.WriteAsync(job, cancellationToken);

    /// <summary>Stream of jobs for the single background consumer.</summary>
    public IAsyncEnumerable<DispatchJob> ReadAllAsync(CancellationToken cancellationToken = default) =>
        _channel.Reader.ReadAllAsync(cancellationToken);

    /// <summary>Number of jobs waiting to be processed.</summary>
    public int PendingCount => _channel.Reader.Count;

    /// <summary>Maximum number of jobs the queue can hold.</summary>
    public int Capacity => DefaultCapacity;
}
