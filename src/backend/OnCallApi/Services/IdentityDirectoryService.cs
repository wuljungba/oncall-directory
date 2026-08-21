using System.Collections.Concurrent;
using System.Threading.Channels;

namespace OnCallApi.Services;

/// <summary>A sign-in observation, queued for the background writer.</summary>
public record SignInObservation(
    string Provider,
    string ExternalObjectId,
    string? Email,
    string? DisplayName,
    string? TenantIdClaim,
    DateTime SeenAt);

public interface IIdentityDirectoryService
{
    /// <summary>
    /// Records that a principal was seen. Cheap and non-blocking — safe to call on every
    /// authenticated request. Repeat sightings inside the throttle window are dropped.
    /// </summary>
    void Observe(SignInObservation observation);
}

/// <summary>
/// Channel-based producer for sign-in observations, mirroring <see cref="AuditService"/>.
/// Keeps the request pipeline free of database work: the middleware enqueues, and
/// <see cref="IdentityDirectoryBackgroundService"/> batches the upserts.
/// </summary>
public class IdentityDirectoryService : IIdentityDirectoryService
{
    /// <summary>
    /// How long to ignore repeat sightings of the same principal. Every authenticated
    /// request passes through here, so without this a single active session would enqueue
    /// hundreds of writes for a row whose only changing field is LastSeenAt.
    /// </summary>
    public static readonly TimeSpan ThrottleWindow = TimeSpan.FromMinutes(10);

    private readonly Channel<SignInObservation> _channel =
        Channel.CreateBounded<SignInObservation>(new BoundedChannelOptions(1000)
        {
            // Identity records are convenience data, never authorization data. Dropping one
            // under load costs at most a stale LastSeenAt, so it must not block a request.
            FullMode = BoundedChannelFullMode.DropOldest,
        });

    private readonly ConcurrentDictionary<string, DateTime> _lastEnqueued = new();

    public ChannelReader<SignInObservation> Reader => _channel.Reader;

    public void Observe(SignInObservation observation)
    {
        if (string.IsNullOrEmpty(observation.ExternalObjectId)) return;

        var key = $"{observation.Provider}|{observation.ExternalObjectId}";
        var now = observation.SeenAt;

        if (_lastEnqueued.TryGetValue(key, out var last) && now - last < ThrottleWindow)
            return;

        _lastEnqueued[key] = now;
        _channel.Writer.TryWrite(observation);
    }

    /// <summary>
    /// Drops throttle entries older than the window so the map cannot grow without bound
    /// on a long-running instance. Called periodically by the background service.
    /// </summary>
    public void PruneThrottleCache(DateTime utcNow)
    {
        foreach (var (key, seen) in _lastEnqueued)
        {
            if (utcNow - seen >= ThrottleWindow)
                _lastEnqueued.TryRemove(key, out _);
        }
    }

    /// <summary>Exposed for tests: how many principals are currently throttled.</summary>
    public int ThrottledPrincipalCount => _lastEnqueued.Count;
}
