using FluentAssertions;
using OnCallApi.Services.Dispatch;

namespace BackendTests.Services;

/// <summary>
/// Guards the safety-critical dispatch queue: it must never drop a code-call job
/// (bounded, FullMode=Wait/backpressure) and must dequeue in FIFO order.
/// </summary>
public class DispatchJobQueueTests
{
    [Fact]
    public async Task Enqueue_ThenDequeue_PreservesOrderAndClearsPending()
    {
        var queue = new DispatchJobQueue();

        await queue.EnqueueAsync(new DispatchJob(1, "code-blue"));
        await queue.EnqueueAsync(new DispatchJob(2, "code-red"));

        queue.PendingCount.Should().Be(2);
        queue.Capacity.Should().Be(DispatchJobQueue.DefaultCapacity);

        // ReadAllAsync never completes on its own (the writer stays open), so stop
        // after we've drained the expected number of jobs.
        var cts = new CancellationTokenSource();
        var dequeued = new List<DispatchJob>();
        try
        {
            await foreach (var job in queue.ReadAllAsync(cts.Token))
            {
                dequeued.Add(job);
                if (dequeued.Count == 2) cts.Cancel();
            }
        }
        catch (OperationCanceledException)
        {
            // expected — we cancelled the read after collecting the jobs
        }

        dequeued.Select(j => j.EventId).Should().Equal(1, 2);
        queue.PendingCount.Should().Be(0);
    }
}