using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OnCallApi.Configuration;
using OnCallApi.Data;
using OnCallApi.Models;
using OnCallApi.Services;

namespace BackendTests.Services;

/// <summary>
/// The rule that decides whether somebody stays in the on-call directory.
///
/// A delta response contains only CHANGES, so "absent from the response" means "unchanged" —
/// except for one case: an enumeration that started from nothing and reached the end. The old
/// code read a single Graph page, treated everyone on the pages it never read as departed, and
/// deactivated them. Deactivated staff are staff a code call cannot reach.
///
/// These fix that rule in place, in both directions: an incremental run must deactivate nobody
/// by absence, and a complete full run still must.
/// </summary>
public class AdDirectorySyncInterlockTests
{
    private const int TenantId = 1;
    private const string Directory = "11111111-2222-3333-4444-555555555555";
    private const string StoredCursor = "https://graph.microsoft.com/v1.0/users/delta?$deltatoken=stored";

    private static AppDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new AppDbContext(options);
        db.Tenants.Add(new Tenant { Id = TenantId, Name = "Northwood Medical", IsActive = true });
        db.SaveChanges();
        return db;
    }

    private static AdDirectorySyncService CreateService(
        AppDbContext db, IGraphApiService graph, params (string Key, string Value)[] config)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(config.Select(c => new KeyValuePair<string, string?>(c.Key, c.Value)))
            .Build();

        return new AdDirectorySyncService(
            db,
            graph,
            Options.Create(new GraphApiOptions { TenantId = "home-directory" }),
            configuration,
            NullLogger<AdDirectorySyncService>.Instance);
    }

    /// <summary>A person already in the directory, owned by the AD sync.</summary>
    private static Employee Stored(string objectId, string? mobile = null, string source = "Ad", int? tenantId = TenantId) =>
        new()
        {
            Id = Guid.NewGuid(),
            AzureAdObjectId = objectId,
            FirstName = "Dana",
            LastName = objectId,
            Email = $"{objectId}@northwood.test",
            MobilePhone = mobile,
            Source = source,
            TenantId = tenantId,
            IsActive = true,
        };

    /// <summary>A person as Graph just reported them.</summary>
    private static Employee FromGraph(string objectId, string? mobile = null, bool accountEnabled = true) =>
        new()
        {
            AzureAdObjectId = objectId,
            FirstName = "Dana",
            LastName = objectId,
            Email = $"{objectId}@northwood.test",
            MobilePhone = mobile,
            IsActive = accountEnabled,
        };

    private static async Task<Employee> Reload(AppDbContext db, string objectId) =>
        await db.Employees.AsNoTracking().FirstAsync(e => e.AzureAdObjectId == objectId);

    private static async Task<string?> StoredCursorValue(AppDbContext db) =>
        (await db.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == $"AdDeltaToken:{TenantId}"))?.Value;

    // ── The interlock ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnIncrementalRunDeactivatesNobodyForBeingAbsent()
    {
        using var db = CreateDb();
        db.Employees.Add(Stored("alice"));
        db.Employees.Add(Stored("bob"));
        await db.SaveChangesAsync();

        // Only Alice changed. Bob is absent because nothing about him changed — not because he left.
        var graph = new FakeGraphApiService(DeltaResults.IncrementalComplete([FromGraph("alice")]));
        var result = await CreateService(db, graph).SyncAsync(TenantId, Directory, StoredCursor);

        result.Deactivated.Should().Be(0);
        (await Reload(db, "bob")).IsActive.Should().BeTrue("absence from an incremental delta means unchanged");
    }

    /// <summary>The control for the test above: without it, that one passes by doing nothing at all.</summary>
    [Fact]
    public async Task ACompleteFullRunStillDeactivatesSomebodyWhoIsGone()
    {
        using var db = CreateDb();
        db.Employees.Add(Stored("alice"));
        db.Employees.Add(Stored("bob"));
        await db.SaveChangesAsync();

        var graph = new FakeGraphApiService(DeltaResults.FullComplete([FromGraph("alice")]));
        var result = await CreateService(db, graph).SyncAsync(TenantId, Directory, deltaLink: null);

        result.Deactivated.Should().Be(1);
        (await Reload(db, "bob")).IsActive.Should().BeFalse();
        (await Reload(db, "alice")).IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task APartlyReadDirectoryDeactivatesNobodyAndStoresNoCursor()
    {
        using var db = CreateDb();
        db.Employees.Add(Stored("alice"));
        db.Employees.Add(Stored("bob"));
        await db.SaveChangesAsync();

        var graph = new FakeGraphApiService(DeltaResults.FullIncomplete([FromGraph("alice")], pages: 3));
        var result = await CreateService(db, graph).SyncAsync(TenantId, Directory, deltaLink: null);

        result.Deactivated.Should().Be(0);
        result.Succeeded.Should().BeFalse("an incomplete read is not a successful sync");
        (await Reload(db, "bob")).IsActive.Should().BeTrue();
        (await StoredCursorValue(db)).Should().BeNull("a cursor from an incomplete read would skip the rest forever");
    }

    [Fact]
    public async Task ACompleteFullRunThatReturnsNobodyRefusesToEmptyTheDirectory()
    {
        using var db = CreateDb();
        db.Employees.Add(Stored("alice"));
        await db.SaveChangesAsync();

        var graph = new FakeGraphApiService(DeltaResults.FullComplete(users: []));
        var result = await CreateService(db, graph).SyncAsync(TenantId, Directory, deltaLink: null);

        result.Deactivated.Should().Be(0, "a directory that answers with nobody is a fault, not an empty hospital");
        (await Reload(db, "alice")).IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task AFailedReadChangesNothingAndKeepsTheCursor()
    {
        using var db = CreateDb();
        db.Employees.Add(Stored("alice"));
        await db.SaveChangesAsync();

        var graph = new FakeGraphApiService(DeltaResults.Failed());
        var result = await CreateService(db, graph).SyncAsync(TenantId, Directory, StoredCursor);

        result.Succeeded.Should().BeFalse();
        result.Deactivated.Should().Be(0);
        (await Reload(db, "alice")).IsActive.Should().BeTrue();
        (await StoredCursorValue(db)).Should().BeNull("nothing was read, so nothing about the cursor changed");
    }

    // ── Departures Graph reports outright ────────────────────────────────────────────────

    [Fact]
    public async Task AReportedRemovalDeactivatesThatPersonEvenInAnIncrementalRun()
    {
        using var db = CreateDb();
        db.Employees.Add(Stored("alice"));
        db.Employees.Add(Stored("bob"));
        db.Employees.Add(Stored("carol", source: "Local"));
        db.Employees.Add(Stored("dave", tenantId: 2));
        await db.SaveChangesAsync();

        // Graph names bob, carol and dave as gone. Only bob is this directory's to deactivate.
        var graph = new FakeGraphApiService(
            DeltaResults.IncrementalComplete([FromGraph("alice")], removed: ["bob", "carol", "dave"]));
        var result = await CreateService(db, graph).SyncAsync(TenantId, Directory, StoredCursor);

        result.DeactivatedByRemoval.Should().Be(1);
        (await Reload(db, "bob")).IsActive.Should().BeFalse();
        (await Reload(db, "carol")).IsActive.Should().BeTrue("a locally managed record is not the directory's to retire");
        (await Reload(db, "dave")).IsActive.Should().BeTrue("another tenant's staff are not in this directory's gift");
    }

    [Fact]
    public async Task ADisabledAccountCountsAsADeparture()
    {
        using var db = CreateDb();
        db.Employees.Add(Stored("alice"));
        await db.SaveChangesAsync();

        // Most directories disable a leaver rather than deleting them.
        var graph = new FakeGraphApiService(
            DeltaResults.IncrementalComplete([FromGraph("alice", accountEnabled: false)]));
        await CreateService(db, graph).SyncAsync(TenantId, Directory, StoredCursor);

        (await Reload(db, "alice")).IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task ADisabledAccountIsIgnoredWhenThatIsSwitchedOff()
    {
        using var db = CreateDb();
        db.Employees.Add(Stored("alice"));
        await db.SaveChangesAsync();

        var graph = new FakeGraphApiService(
            DeltaResults.IncrementalComplete([FromGraph("alice", accountEnabled: false)]));
        await CreateService(db, graph, ("Sync:DeactivateOnDisabledAccount", "false"))
            .SyncAsync(TenantId, Directory, StoredCursor);

        (await Reload(db, "alice")).IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task ObjectIdCasingDifferencesDoNotDeactivateAPresentColleague()
    {
        using var db = CreateDb();
        db.Employees.Add(Stored("AAAA-BBBB"));
        await db.SaveChangesAsync();

        // Graph echoes back its own casing, which need not match what we stored.
        var graph = new FakeGraphApiService(DeltaResults.FullComplete([FromGraph("aaaa-bbbb")]));
        var result = await CreateService(db, graph).SyncAsync(TenantId, Directory, deltaLink: null);

        result.Deactivated.Should().Be(0);
        (await Reload(db, "AAAA-BBBB")).IsActive.Should().BeTrue();
    }

    // ── The safety valve ─────────────────────────────────────────────────────────────────

    [Theory]
    // proposed, active, expected allowed
    [InlineData(0, 100, true)]    // nothing to do
    [InlineData(2, 4, true)]      // below the floor: small teams turn over in large fractions
    [InlineData(25, 100, true)]   // exactly at the limit
    [InlineData(26, 100, false)]  // over it
    [InlineData(400, 500, false)] // what the page-one bug looked like
    public void TheValveRefusesImplausibleBatches(int proposed, int active, bool expected)
    {
        var (allowed, reason) = AdDirectorySyncService.EvaluateDeactivationBatch(
            active, proposed, maxShare: 0.25, guardFloor: 10);

        allowed.Should().Be(expected);
        if (!expected) reason.Should().Contain(proposed.ToString()).And.Contain(active.ToString());
    }

    [Fact]
    public async Task ARunThatWouldRetireMostOfTheStaffIsRefusedAndLeavesTheCursorAlone()
    {
        using var db = CreateDb();
        for (var i = 0; i < 100; i++) db.Employees.Add(Stored($"person-{i}"));
        await db.SaveChangesAsync();

        // A complete full enumeration that somehow returns only 10 of 100 people.
        var present = Enumerable.Range(0, 10).Select(i => FromGraph($"person-{i}"));
        var graph = new FakeGraphApiService(DeltaResults.FullComplete(present));
        var result = await CreateService(db, graph).SyncAsync(TenantId, Directory, deltaLink: null);

        result.Deactivated.Should().Be(0);
        result.DeactivationsRefused.Should().Be(90);
        result.NeedsAttention.Should().BeTrue();
        result.Skipped.Should().ContainSingle(s => s.Contains("Refused to deactivate 90 of 100"));
        (await Reload(db, "person-50")).IsActive.Should().BeTrue();
        (await StoredCursorValue(db)).Should()
            .BeNull("advancing the cursor after a refusal would turn the alarm off next run");
    }

    [Fact]
    public async Task ASmallTenantIsReconciledWithoutTheValve()
    {
        using var db = CreateDb();
        db.Employees.Add(Stored("alice"));
        db.Employees.Add(Stored("bob"));
        db.Employees.Add(Stored("carol"));
        await db.SaveChangesAsync();

        var graph = new FakeGraphApiService(DeltaResults.FullComplete([FromGraph("alice")]));
        var result = await CreateService(db, graph).SyncAsync(TenantId, Directory, deltaLink: null);

        result.Deactivated.Should().Be(2, "two of three is 67%, but a three-person tenant has no meaningful share");
        result.DeactivationsRefused.Should().Be(0);
    }

    // ── The cursor ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheStoredCursorIsSentBackToGraph()
    {
        using var db = CreateDb();
        var graph = new FakeGraphApiService(DeltaResults.IncrementalComplete([FromGraph("alice")]));

        await CreateService(db, graph).SyncAsync(TenantId, Directory, StoredCursor);

        graph.DeltaCalls.Should().ContainSingle();
        graph.DeltaCalls[0].DeltaLink.Should().Be(StoredCursor);
        graph.DeltaCalls[0].EntraTenantId.Should().Be(Directory);
    }

    [Fact]
    public async Task ACompletedRunStoresTheNewCursor()
    {
        using var db = CreateDb();
        var graph = new FakeGraphApiService(DeltaResults.FullComplete([FromGraph("alice")]));

        await CreateService(db, graph).SyncAsync(TenantId, Directory, deltaLink: null);

        (await StoredCursorValue(db)).Should().Be(DeltaResults.DeltaLink);
    }

    // ── Phase 2: Graph fills blanks, it does not erase ───────────────────────────────────

    [Fact]
    public async Task AGraphBlankDoesNotEraseALocallyEnteredNumber()
    {
        using var db = CreateDb();
        db.Employees.Add(Stored("alice", mobile: "+12025550134"));
        await db.SaveChangesAsync();

        // Entra holds no mobile number for Alice. It used to overwrite hers with null.
        var graph = new FakeGraphApiService(DeltaResults.IncrementalComplete([FromGraph("alice", mobile: null)]));
        await CreateService(db, graph).SyncAsync(TenantId, Directory, StoredCursor);

        (await Reload(db, "alice")).MobilePhone.Should()
            .Be("+12025550134", "a clinician with no number is a clinician a code call cannot reach");
    }

    [Fact]
    public async Task AGraphNumberStillReplacesAStaleOne()
    {
        using var db = CreateDb();
        db.Employees.Add(Stored("alice", mobile: "+12025550134"));
        await db.SaveChangesAsync();

        var graph = new FakeGraphApiService(DeltaResults.IncrementalComplete([FromGraph("alice", mobile: "+12025559999")]));
        await CreateService(db, graph).SyncAsync(TenantId, Directory, StoredCursor);

        (await Reload(db, "alice")).MobilePhone.Should().Be("+12025559999");
    }
}
