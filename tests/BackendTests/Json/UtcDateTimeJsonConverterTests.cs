using System.Text.Json;
using FluentAssertions;
using OnCallApi.Json;

namespace BackendTests.Json;

/// <summary>
/// Every datetime the API stores is UTC, and EF Core hands them back with
/// Kind=Unspecified. The converter must LABEL those as UTC, not CONVERT them: converting
/// treats Unspecified as local and shifts the instant by the server's UTC offset.
///
/// This was live. A code call written at 03:39:04Z was served back as 07:39:04Z on a UTC-4
/// host — start times, response times and audit ordering were all wrong by the offset.
/// Azure App Service runs in UTC, where the offset is zero, so production never showed it
/// and no test caught it.
/// </summary>
public class UtcDateTimeJsonConverterTests
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var o = new JsonSerializerOptions();
        o.Converters.Add(new UtcDateTimeJsonConverter());
        return o;
    }

    private static string Write(DateTime value) =>
        JsonSerializer.Serialize(value, Options).Trim('"');

    /// <summary>The database round-trip case: the regression this exists to prevent.</summary>
    [Fact]
    public void Unspecified_IsLabelledUtc_NotShiftedByServerOffset()
    {
        var fromDatabase = new DateTime(2026, 8, 23, 3, 39, 4, DateTimeKind.Unspecified);

        Write(fromDatabase).Should().StartWith("2026-08-23T03:39:04");
    }

    [Fact]
    public void Utc_IsUnchanged()
    {
        var utc = new DateTime(2026, 8, 23, 3, 39, 4, DateTimeKind.Utc);

        Write(utc).Should().StartWith("2026-08-23T03:39:04");
    }

    /// <summary>A genuinely local value still gets converted — that part was always right.</summary>
    [Fact]
    public void Local_IsConvertedToUtc()
    {
        var local = new DateTime(2026, 8, 23, 3, 39, 4, DateTimeKind.Local);

        Write(local).Should().Be(local.ToUniversalTime().ToString("O"));
    }

    [Fact]
    public void EveryKind_SerializesWithTrailingZ()
    {
        foreach (var kind in new[] { DateTimeKind.Utc, DateTimeKind.Unspecified, DateTimeKind.Local })
        {
            Write(new DateTime(2026, 8, 23, 3, 39, 4, kind))
                .Should().EndWith("Z", $"because {kind} must serialize as an explicit instant");
        }
    }

    /// <summary>
    /// The whole point: a value written and then read back must describe the same instant.
    /// Simulates EF Core dropping the Kind on the way out of the database.
    /// </summary>
    [Fact]
    public void RoundTrip_ThroughDatabaseKindLoss_PreservesTheInstant()
    {
        var written = new DateTime(2026, 8, 23, 3, 39, 4, DateTimeKind.Utc);
        var asWritten = Write(written);

        // What EF Core gives back: same clock reading, Kind erased.
        var rehydrated = DateTime.SpecifyKind(written, DateTimeKind.Unspecified);

        Write(rehydrated).Should().Be(asWritten);
    }
}
