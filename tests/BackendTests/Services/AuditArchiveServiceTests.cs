using System.Text.Json;
using FluentAssertions;
using OnCallApi.Models;
using OnCallApi.Services;

namespace BackendTests.Services;

/// <summary>
/// Guards the two properties that make deleting audit rows safe.
///
/// Blob names must be a pure function of the id range they hold, because that is what makes
/// a retry after a crashed pass rewrite the same blob instead of duplicating or orphaning
/// one. And serialization must lose nothing: once the delete runs, the archive is the only
/// remaining copy of a HIPAA record.
/// </summary>
public class AuditArchiveServiceTests
{
    private static AuditLog Log(long id, string action = "Read") => new()
    {
        Id = id,
        Action = action,
        ResourceType = "directory",
        UserName = "clinician@example.org",
        PrincipalId = "principal-1",
        IpAddress = "203.0.113.10",
        StatusCode = 200,
        TenantId = 3,
        Timestamp = new DateTime(2026, 4, 17, 9, 30, 0, DateTimeKind.Utc),
    };

    [Fact]
    public void BuildBlobName_IsDeterministicForTheSameIdRange()
    {
        var newest = new DateTime(2026, 4, 17, 9, 30, 0, DateTimeKind.Utc);

        var first = AuditArchiveService.BuildBlobName(newest, 100, 5099);
        var second = AuditArchiveService.BuildBlobName(newest, 100, 5099);

        second.Should().Be(first, "a retried pass must rewrite the same blob, not create a second one");
    }

    [Fact]
    public void BuildBlobName_FoldersByYearAndMonthAndCarriesTheIdRange()
    {
        var name = AuditArchiveService.BuildBlobName(
            new DateTime(2026, 4, 17, 9, 30, 0, DateTimeKind.Utc), 100, 5099);

        name.Should().StartWith("2026/04/");
        name.Should().EndWith(".jsonl");
        // Zero-padded so lexical blob listing orders the same way the ids do.
        name.Should().Contain("0000000000000000100").And.Contain("0000000000000005099");
    }

    [Fact]
    public void BuildBlobName_DistinctRangesDoNotCollide()
    {
        var newest = new DateTime(2026, 4, 17, 9, 30, 0, DateTimeKind.Utc);

        AuditArchiveService.BuildBlobName(newest, 1, 5000)
            .Should().NotBe(AuditArchiveService.BuildBlobName(newest, 5001, 10000));
    }

    [Fact]
    public void SerializeBatch_WritesOneParseableLinePerRow()
    {
        var batch = new[] { Log(1), Log(2, "Updated"), Log(3, "Deleted") };

        var lines = AuditArchiveService.SerializeBatch(batch)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        lines.Should().HaveCount(3);
        lines.Select(l => JsonSerializer.Deserialize<AuditLog>(l)!.Id)
            .Should().Equal(1, 2, 3);
    }

    [Fact]
    public void SerializeBatch_PreservesTheFieldsAnAuditorNeeds()
    {
        var line = AuditArchiveService.SerializeBatch([Log(42)]).TrimEnd('\n');

        var restored = JsonSerializer.Deserialize<AuditLog>(line)!;

        restored.Id.Should().Be(42);
        restored.Action.Should().Be("Read");
        restored.ResourceType.Should().Be("directory");
        restored.PrincipalId.Should().Be("principal-1");
        restored.UserName.Should().Be("clinician@example.org");
        restored.IpAddress.Should().Be("203.0.113.10");
        restored.StatusCode.Should().Be(200);
        restored.TenantId.Should().Be(3);
        restored.Timestamp.Should().Be(new DateTime(2026, 4, 17, 9, 30, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void SerializeBatch_EmptyBatchProducesNothing()
    {
        AuditArchiveService.SerializeBatch([]).Should().BeEmpty();
    }

    [Fact]
    public void ResolveCutoff_LeavesTheHotWindowInSql()
    {
        var now = new DateTime(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);

        AuditArchiveService.ResolveCutoff(now, 90)
            .Should().Be(new DateTime(2026, 6, 7, 12, 0, 0, DateTimeKind.Utc));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-30)]
    public void ResolveCutoff_NonPositiveHotDaysStillKeepsADay(int hotDays)
    {
        // A misconfigured zero or negative window must not become "archive everything,
        // including the rows being written right now".
        var now = new DateTime(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);

        AuditArchiveService.ResolveCutoff(now, hotDays)
            .Should().Be(now.AddDays(-1));
    }
}
