using FluentAssertions;
using OnCallApi.Services;

namespace BackendTests.Services;

/// <summary>
/// The sign-in directory is what makes a newly signed-in user visible to an administrator.
/// It runs on every authenticated request, so the throttle matters: without it an active
/// session would enqueue a write per request for a row whose only changing field is
/// LastSeenAt.
/// </summary>
public class IdentityDirectoryTests
{
    private static SignInObservation Observation(
        string objectId = "google-abc", string provider = "google", DateTime? seenAt = null) =>
        new(provider, objectId, "user@example.test", "Test User", null, seenAt ?? DateTime.UtcNow);

    [Fact]
    public void Observe_FirstSighting_IsQueued()
    {
        var service = new IdentityDirectoryService();

        service.Observe(Observation());

        service.Reader.TryRead(out var queued).Should().BeTrue();
        queued!.ExternalObjectId.Should().Be("google-abc");
    }

    [Fact]
    public void Observe_RepeatWithinThrottleWindow_IsDropped()
    {
        var service = new IdentityDirectoryService();
        var now = DateTime.UtcNow;

        service.Observe(Observation(seenAt: now));
        service.Observe(Observation(seenAt: now.AddMinutes(1)));
        service.Observe(Observation(seenAt: now.AddMinutes(2)));

        service.Reader.TryRead(out _).Should().BeTrue();
        service.Reader.TryRead(out _).Should().BeFalse("repeat sightings inside the window must not queue writes");
    }

    [Fact]
    public void Observe_AfterThrottleWindow_IsQueuedAgain()
    {
        var service = new IdentityDirectoryService();
        var now = DateTime.UtcNow;

        service.Observe(Observation(seenAt: now));
        service.Reader.TryRead(out _).Should().BeTrue();

        service.Observe(Observation(seenAt: now.Add(IdentityDirectoryService.ThrottleWindow).AddSeconds(1)));

        service.Reader.TryRead(out var second).Should().BeTrue();
        second!.SeenAt.Should().BeAfter(now);
    }

    [Fact]
    public void Observe_DifferentPrincipals_AreTrackedIndependently()
    {
        var service = new IdentityDirectoryService();
        var now = DateTime.UtcNow;

        service.Observe(Observation("google-abc", seenAt: now));
        service.Observe(Observation("google-xyz", seenAt: now));
        // Same subject id on a different provider is a different person.
        service.Observe(Observation("google-abc", provider: "microsoft", seenAt: now));

        var drained = 0;
        while (service.Reader.TryRead(out _)) drained++;
        drained.Should().Be(3);
    }

    [Fact]
    public void Observe_WithoutObjectId_IsIgnored()
    {
        var service = new IdentityDirectoryService();

        service.Observe(Observation(objectId: ""));

        service.Reader.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public void PruneThrottleCache_DropsOnlyExpiredEntries()
    {
        var service = new IdentityDirectoryService();
        var now = DateTime.UtcNow;

        service.Observe(Observation("old-principal", seenAt: now.Subtract(IdentityDirectoryService.ThrottleWindow).AddMinutes(-1)));
        service.Observe(Observation("recent-principal", seenAt: now));
        service.ThrottledPrincipalCount.Should().Be(2);

        service.PruneThrottleCache(now);

        // Bounded growth matters: this map lives for the process lifetime.
        service.ThrottledPrincipalCount.Should().Be(1);
    }
}
