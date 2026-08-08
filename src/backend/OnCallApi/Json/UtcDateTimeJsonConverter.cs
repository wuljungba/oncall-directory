using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OnCallApi.Json;

/// <summary>
/// Serializes every <see cref="DateTime"/> as a UTC instant (always with a trailing 'Z').
///
/// Stored datetimes are UTC by construction (the SPA has always sent toISOString(); the
/// backend uses DateTime.UtcNow), but EF Core rehydrates SQL Server columns with
/// Kind=Unspecified, and System.Text.Json emits those WITHOUT a 'Z'. The browser then
/// re-parses the naive value as LOCAL time, so every clock display was shifted by the
/// viewer's UTC offset (e.g. a 4–8pm shift showing a UTC clock time). Emitting 'Z' keeps
/// the instant correct and lets the frontend's new Date(...)/toLocale*() render true local time.
/// </summary>
public sealed class UtcDateTimeJsonConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var raw = reader.GetString() ?? throw new JsonException("Expected a DateTime.");
        if (!DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value))
            throw new JsonException($"Invalid DateTime: '{raw}'");

        // Unspecified input (e.g. a bare date "2026-08-08" or a naive UTC time) is treated as UTC.
        return value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : value;
    }

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
}