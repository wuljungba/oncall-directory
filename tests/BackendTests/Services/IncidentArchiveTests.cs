using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OnCallApi.Data;
using OnCallApi.Models;
using OnCallApi.Services;

namespace BackendTests.Services;

/// <summary>
/// The incident archive, and the one property it exists to guarantee.
///
/// It is the sibling of <see cref="AuditArchiveService"/> with one deliberate difference: that
/// one moves rows out of SQL, this one only copies them. Code-call history has to stay readable
/// in the console that is required to keep it for seven years, so an archive that quietly
/// evicted incidents would defeat its own purpose.
///
/// "Deletes nothing" is easy to state and easy to break — one stray <c>ExecuteDeleteAsync</c>
/// added later by someone matching the audit service's shape would do it silently, and the
/// symptom would be missing history nobody notices for months. So it is asserted directly here
/// rather than trusted to a comment.
/// </summary>
public class IncidentArchiveTests
{
    /// <summary>
    /// The service with its storage replaced. Records what would have been written so a pass
    /// can be run end to end without Azure.
    /// </summary>
    private sealed class FakeStoreArchiveService : IncidentArchiveService
    {
        public readonly Dictionary<string, string> Written = new();
        public readonly HashSet<string> AlreadyPresent = new();
        public int ExistenceChecks;

        public FakeStoreArchiveService(IServiceScopeFactory scopes, IConfiguration config)
            : base(scopes, config, NullLogger<IncidentArchiveService>.Instance) { }

        protected override Task PrepareStoreAsync(string endpoint, string container, CancellationToken ct)
            => Task.CompletedTask;

        protected override Task<bool> ArchiveExistsAsync(string blobName, CancellationToken ct)
        {
            ExistenceChecks++;
            return Task.FromResult(AlreadyPresent.Contains(blobName));
        }

        protected override Task WriteArchiveAsync(string blobName, string payload, CancellationToken ct)
        {
            Written[blobName] = payload;
            return Task.CompletedTask;
        }
    }

    private static ServiceProvider BuildProvider(out AppDbContext db)
    {
        var dbName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
        var provider = services.BuildServiceProvider();
        db = provider.GetRequiredService<AppDbContext>();
        return provider;
    }

    private static IConfiguration Config() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();

    private static void SeedIncident(
        AppDbContext db, DateTime startedAt, int treeId = 1, int? tenantId = 500, string location = "3 N")
    {
        if (tenantId != null && !db.Departments.Any(d => d.Id == tenantId))
        {
            db.Departments.Add(new Department
            {
                Id = tenantId.Value, Name = $"Dept {tenantId}", TenantId = tenantId, IsActive = true,
            });
        }

        if (!db.PhoneTrees.Any(t => t.Id == treeId))
        {
            db.PhoneTrees.Add(new PhoneTree
            {
                Id = treeId, Name = "Code Blue", TreeType = "code-blue",
                DepartmentId = tenantId, IsActive = true,
            });
        }

        db.PhoneTreeEvents.Add(new PhoneTreeEvent
        {
            PhoneTreeId = treeId,
            StartedAt = startedAt,
            EndedAt = startedAt.AddMinutes(4),
            Status = "completed",
            Location = location,
            InitiatedByName = "Divine Yisa",
            InitiatedByEmail = "divine@hospital.test",
        });
        db.SaveChanges();
    }

    // ── The invariant ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The whole reason this class is separate from the audit archive. If this ever fails, the
    /// seven-year code-call record is being deleted from the product that has to show it.
    /// </summary>
    [Fact]
    public async Task APassDeletesNothingFromSql()
    {
        using var provider = BuildProvider(out var db);
        SeedIncident(db, DateTime.UtcNow.AddMonths(-8));
        SeedIncident(db, DateTime.UtcNow.AddDays(-2));

        var before = await db.PhoneTreeEvents.CountAsync();
        before.Should().Be(2);

        var service = new FakeStoreArchiveService(
            provider.GetRequiredService<IServiceScopeFactory>(), Config());
        await service.RunOnePassAsync("UseDevelopmentStorage=true");

        (await db.PhoneTreeEvents.CountAsync()).Should().Be(before,
            "the archive copies incidents, it must never evict them");
        service.Written.Should().NotBeEmpty("but it should still have written the archive");
    }

    [Fact]
    public async Task EachMonthIsWrittenToItsOwnFile()
    {
        using var provider = BuildProvider(out var db);
        var now = DateTime.UtcNow;
        SeedIncident(db, new DateTime(now.Year, 1, 15, 0, 0, 0, DateTimeKind.Utc));
        SeedIncident(db, new DateTime(now.Year, 2, 10, 0, 0, 0, DateTimeKind.Utc));

        var service = new FakeStoreArchiveService(
            provider.GetRequiredService<IServiceScopeFactory>(), Config());
        await service.RunOnePassAsync("UseDevelopmentStorage=true");

        service.Written.Keys.Should().Contain(k => k.EndsWith("-01.jsonl", StringComparison.Ordinal));
        service.Written.Keys.Should().Contain(k => k.EndsWith("-02.jsonl", StringComparison.Ordinal));
    }

    /// <summary>
    /// What keeps a pass cheap after seven years: a closed month already on disk is not
    /// rewritten. Without this the job's cost grows with the whole history, every day.
    /// </summary>
    [Fact]
    public async Task AClosedMonthAlreadyWrittenIsNotRewritten()
    {
        using var provider = BuildProvider(out var db);
        var old = DateTime.UtcNow.AddMonths(-10);
        SeedIncident(db, old);

        var service = new FakeStoreArchiveService(
            provider.GetRequiredService<IServiceScopeFactory>(), Config());
        service.AlreadyPresent.Add(IncidentArchiveService.BuildBlobName(500, old.Year, old.Month));

        await service.RunOnePassAsync("UseDevelopmentStorage=true");

        service.Written.Should().BeEmpty();
        service.ExistenceChecks.Should().Be(1);
    }

    /// <summary>
    /// A recent month IS rewritten even when present, because debrief notes are appended days
    /// after the incident — a file closed at the moment of resolution would miss them.
    /// </summary>
    [Fact]
    public async Task ARecentMonthIsRewrittenEvenWhenAlreadyPresent()
    {
        using var provider = BuildProvider(out var db);
        var recent = DateTime.UtcNow.AddDays(-3);
        SeedIncident(db, recent);

        var service = new FakeStoreArchiveService(
            provider.GetRequiredService<IServiceScopeFactory>(), Config());
        service.AlreadyPresent.Add(IncidentArchiveService.BuildBlobName(500, recent.Year, recent.Month));

        await service.RunOnePassAsync("UseDevelopmentStorage=true");

        service.Written.Should().HaveCount(1);
    }

    [Fact]
    public async Task TheArchiveCarriesWhoRaisedTheCode()
    {
        using var provider = BuildProvider(out var db);
        SeedIncident(db, DateTime.UtcNow.AddDays(-1));

        var service = new FakeStoreArchiveService(
            provider.GetRequiredService<IServiceScopeFactory>(), Config());
        await service.RunOnePassAsync("UseDevelopmentStorage=true");

        var payload = service.Written.Values.Single();
        payload.Should().Contain("divine@hospital.test");
        payload.Should().Contain("Divine Yisa");
        payload.Should().Contain("Code Blue", "the code name, not just its id — the archive must read without the database");
    }

    [Fact]
    public async Task AnEmptyDeploymentWritesNothingAndProbesNothing()
    {
        using var provider = BuildProvider(out _);

        var service = new FakeStoreArchiveService(
            provider.GetRequiredService<IServiceScopeFactory>(), Config());
        await service.RunOnePassAsync("UseDevelopmentStorage=true");

        service.Written.Should().BeEmpty();
        service.ExistenceChecks.Should().Be(0);
    }

    // ── Pure helpers ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void BlobNamesArePartitionedByTenantThenMonth()
    {
        IncidentArchiveService.BuildBlobName(42, 2026, 9)
            .Should().Be("tenant-42/2026/09/incidents-2026-09.jsonl");
        IncidentArchiveService.BuildBlobName(42, 2026, 12)
            .Should().Be("tenant-42/2026/12/incidents-2026-12.jsonl");

        // An incident whose code tree has no department resolves to no subscription. It is
        // filed under its own prefix rather than dropped or folded into somebody else's.
        IncidentArchiveService.BuildBlobName(null, 2026, 9)
            .Should().StartWith("unassigned/");

        // Determinism is what makes an interrupted pass safe to repeat.
        IncidentArchiveService.BuildBlobName(42, 2026, 9)
            .Should().Be(IncidentArchiveService.BuildBlobName(42, 2026, 9));
    }

    /// <summary>
    /// The isolation property. The archive exists so a customer can be handed their own
    /// history — which is only safe if one file contains exactly one customer's incidents.
    /// A single commingled month file would leak every other hospital's code calls to whoever
    /// received it.
    /// </summary>
    [Fact]
    public async Task OneSubscriptionsIncidentsNeverShareAFileWithAnothers()
    {
        using var provider = BuildProvider(out var db);
        var when = DateTime.UtcNow.AddDays(-5);
        SeedIncident(db, when, treeId: 1, tenantId: 500, location: "NORTHWOOD-WARD");
        SeedIncident(db, when, treeId: 2, tenantId: 600, location: "SOUTHVALE-WARD");

        var service = new FakeStoreArchiveService(
            provider.GetRequiredService<IServiceScopeFactory>(), Config());
        await service.RunOnePassAsync("UseDevelopmentStorage=true");

        service.Written.Should().HaveCount(2, "one file per subscription per month");

        var northwood = service.Written.Single(w => w.Key.StartsWith("tenant-500/", StringComparison.Ordinal));
        northwood.Value.Should().Contain("NORTHWOOD-WARD");
        northwood.Value.Should().NotContain("SOUTHVALE-WARD",
            "handing this file to one customer must not disclose another's incidents");

        var southvale = service.Written.Single(w => w.Key.StartsWith("tenant-600/", StringComparison.Ordinal));
        southvale.Value.Should().Contain("SOUTHVALE-WARD");
        southvale.Value.Should().NotContain("NORTHWOOD-WARD");
    }

    [Fact]
    public void TheReconsolidationWindowIsTwoMonths()
    {
        var now = new DateTime(2026, 9, 24, 0, 0, 0, DateTimeKind.Utc);

        IncidentArchiveService.IsReconsolidating(now, 2026, 9).Should().BeTrue("this month");
        IncidentArchiveService.IsReconsolidating(now, 2026, 8).Should().BeTrue("last month");
        IncidentArchiveService.IsReconsolidating(now, 2026, 7).Should().BeTrue("two months back");
        IncidentArchiveService.IsReconsolidating(now, 2026, 6).Should().BeFalse("closed");

        // Across a year boundary, which naive month arithmetic gets wrong.
        var january = new DateTime(2027, 1, 5, 0, 0, 0, DateTimeKind.Utc);
        IncidentArchiveService.IsReconsolidating(january, 2026, 12).Should().BeTrue();
        IncidentArchiveService.IsReconsolidating(january, 2026, 11).Should().BeTrue();
        IncidentArchiveService.IsReconsolidating(january, 2026, 10).Should().BeFalse();
    }

    [Fact]
    public void SerializationIsOneRecordPerLine()
    {
        var payload = IncidentArchiveService.SerializeBatch(
            new object[] { new { Id = 1 }, new { Id = 2 } });

        var lines = payload.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(2);
        lines[0].Should().Be("{\"Id\":1}");
    }
}
