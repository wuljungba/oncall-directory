using FluentAssertions;
using OnCallApi.Services;

namespace BackendTests.Services;

/// <summary>
/// Guards the invariant that makes batched presence safe: a person Graph did not answer
/// for keeps the presence we already had. The per-user path this replaced returned
/// "unknown" from its catch block, so any throttled lookup silently marked a reachable
/// clinician unreachable — the opposite of what the directory is for.
/// </summary>
public class PresenceSyncServiceTests
{
    private static PresenceSyncService.PresenceRow Row(string oid, string presence) =>
        new(Guid.NewGuid(), oid, presence);

    [Fact]
    public void SelectPresenceChanges_ReturnsOnlyEmployeesWhosePresenceMoved()
    {
        var changed = Row("oid-1", "offline");
        var unchanged = Row("oid-2", "available");

        var result = PresenceSyncService.SelectPresenceChanges(
            [changed, unchanged],
            new Dictionary<string, string>
            {
                ["oid-1"] = "available", // moved  -> update
                ["oid-2"] = "available", // same   -> skip
            });

        result.Should().ContainSingle();
        result[0].Id.Should().Be(changed.Id);
        result[0].Presence.Should().Be("available");
    }

    [Fact]
    public void SelectPresenceChanges_LeavesUnansweredEmployeesAlone()
    {
        // Graph answered for one of the two — a failed or throttled chunk must not be read
        // as "everyone in it went offline".
        var answered = Row("oid-1", "offline");
        var unanswered = Row("oid-2", "available");

        var result = PresenceSyncService.SelectPresenceChanges(
            [answered, unanswered],
            new Dictionary<string, string> { ["oid-1"] = "busy" });

        result.Should().ContainSingle();
        result[0].Id.Should().Be(answered.Id);
    }

    [Fact]
    public void SelectPresenceChanges_EmptyGraphResponse_ChangesNothing()
    {
        var result = PresenceSyncService.SelectPresenceChanges(
            [Row("oid-1", "available"), Row("oid-2", "busy")],
            new Dictionary<string, string>());

        result.Should().BeEmpty();
    }

    [Fact]
    public void SelectPresenceChanges_SkipsEmployeesWithoutAnObjectId()
    {
        var result = PresenceSyncService.SelectPresenceChanges(
            [Row("", "available")],
            new Dictionary<string, string> { [""] = "busy" });

        result.Should().BeEmpty();
    }

    [Fact]
    public void SelectPresenceChanges_MatchesObjectIdsCaseInsensitively()
    {
        // Graph echoes back the id casing it holds, which need not match what the directory
        // sync stored. The lookup map is built case-insensitively so this still resolves.
        var employee = Row("OID-ABC", "offline");

        var result = PresenceSyncService.SelectPresenceChanges(
            [employee],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["oid-abc"] = "available" });

        result.Should().ContainSingle();
        result[0].Presence.Should().Be("available");
    }
}
