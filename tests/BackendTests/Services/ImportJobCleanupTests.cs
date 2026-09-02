using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using OnCallApi.Data;
using OnCallApi.Models;

namespace BackendTests.Services;

/// <summary>
/// Staging an upload keeps a copy of every row exactly as the file wrote it. That is what
/// makes the mapping correctable and the error report honest, and it also means an
/// abandoned upload leaves a full copy of somebody's staff list — names, numbers,
/// addresses — sitting in the database for nobody's benefit.
///
/// These pin the retention rules directly against the database rather than through the
/// hosted service, whose only other job is a timer.
/// </summary>
public class ImportJobCleanupTests
{
    private static AppDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static readonly TimeSpan DraftRetention = TimeSpan.FromDays(7);
    private static readonly TimeSpan CommittedRowRetention = TimeSpan.FromDays(30);

    private static ImportJob Job(string status, DateTime updatedAt, DateTime? committedAt = null)
    {
        var job = new ImportJob
        {
            FileName = "roster.xlsx",
            Status = status,
            UpdatedAt = updatedAt,
            CommittedAt = committedAt,
            TotalRows = 2,
        };

        job.Rows.Add(new ImportJobRow { SheetName = "Staff", SourceRow = 2, RawValuesJson = "{}" });
        job.Rows.Add(new ImportJobRow { SheetName = "Staff", SourceRow = 3, RawValuesJson = "{}" });
        return job;
    }

    /// <summary>Mirrors ImportJobCleanupService.CleanupAsync.</summary>
    private static async Task RunCleanupAsync(AppDbContext db, DateTime now)
    {
        var staleDrafts = await db.ImportJobs
            .Where(j => j.Status != ImportJobStatus.Committed && j.UpdatedAt < now - DraftRetention)
            .Include(j => j.Rows)
            .ToListAsync();

        db.ImportJobs.RemoveRange(staleDrafts);

        var settled = await db.ImportJobs
            .Where(j => j.Status == ImportJobStatus.Committed
                        && j.CommittedAt != null
                        && j.CommittedAt < now - CommittedRowRetention)
            .Select(j => j.Id)
            .ToListAsync();

        var rows = await db.ImportJobRows
            .Where(r => settled.Contains(r.ImportJobId))
            .ToListAsync();

        db.ImportJobRows.RemoveRange(rows);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task AnAbandonedUploadIsDiscardedAfterAWeek()
    {
        using var db = CreateDb();
        var now = DateTime.UtcNow;

        db.ImportJobs.Add(Job(ImportJobStatus.Draft, now.AddDays(-8)));
        await db.SaveChangesAsync();

        await RunCleanupAsync(db, now);

        (await db.ImportJobs.CountAsync()).Should().Be(0);
        (await db.ImportJobRows.CountAsync()).Should().Be(0,
            "the staged rows go with the job — they are the reason it is being discarded");
    }

    [Fact]
    public async Task AnUploadStillBeingWorkedOnIsLeftAlone()
    {
        using var db = CreateDb();
        var now = DateTime.UtcNow;

        db.ImportJobs.Add(Job(ImportJobStatus.Draft, now.AddDays(-2)));
        await db.SaveChangesAsync();

        await RunCleanupAsync(db, now);

        (await db.ImportJobs.CountAsync()).Should().Be(1);
        (await db.ImportJobRows.CountAsync()).Should().Be(2);
    }

    /// <summary>
    /// The header is the record of who imported what and when. Only the duplicated row
    /// data goes.
    /// </summary>
    [Fact]
    public async Task ACommittedImportKeepsItsRecordAndLosesItsRows()
    {
        using var db = CreateDb();
        var now = DateTime.UtcNow;

        db.ImportJobs.Add(Job(ImportJobStatus.Committed, now.AddDays(-40), now.AddDays(-40)));
        await db.SaveChangesAsync();

        await RunCleanupAsync(db, now);

        (await db.ImportJobs.CountAsync()).Should().Be(1,
            "who imported what, when, and how many is the record and must survive");
        (await db.ImportJobRows.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ARecentlyCommittedImportKeepsItsRows()
    {
        using var db = CreateDb();
        var now = DateTime.UtcNow;

        db.ImportJobs.Add(Job(ImportJobStatus.Committed, now.AddDays(-3), now.AddDays(-3)));
        await db.SaveChangesAsync();

        await RunCleanupAsync(db, now);

        (await db.ImportJobs.CountAsync()).Should().Be(1);
        (await db.ImportJobRows.CountAsync()).Should().Be(2);
    }

    /// <summary>
    /// A committed job is never deleted by the draft rule, however old it is — the two
    /// windows must not overlap, or the record would vanish at seven days.
    /// </summary>
    [Fact]
    public async Task AnOldCommittedImportIsNotTreatedAsAnAbandonedDraft()
    {
        using var db = CreateDb();
        var now = DateTime.UtcNow;

        db.ImportJobs.Add(Job(ImportJobStatus.Committed, now.AddDays(-365), now.AddDays(-365)));
        await db.SaveChangesAsync();

        await RunCleanupAsync(db, now);

        (await db.ImportJobs.CountAsync()).Should().Be(1);
    }
}
