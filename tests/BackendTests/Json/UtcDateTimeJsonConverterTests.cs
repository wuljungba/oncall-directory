using System.Text.Json;
using FluentAssertions;
using OnCallApi.Json;

namespace BackendTests.Json;

public class UtcDateTimeJsonConverterTests
{
    private static readonly JsonSerializerOptions Options = new();
    static UtcDateTimeJsonConverterTests() => Options.Converters.Add(new UtcDateTimeJsonConverter());

    [Fact]
    public void Write_AlwaysEmitsUtcZ()
    {
        var value = new DateTime(2026, 8, 8, 19, 0, 0, DateTimeKind.Utc);
        var json = JsonSerializer.Serialize(value, Options);
        json.Should().Be("\"2026-08-08T19:00:00.0000000Z\"");
    }

    [Theory]
    [InlineData("2026-08-08T19:00:00")]
    [InlineData("2026-08-08T19:00:00Z")]
    public void Read_TreatsValueAsUtc(string raw)
    {
        var dt = JsonSerializer.Deserialize<DateTime>($"\"{raw}\"", Options);
        dt.Kind.Should().Be(DateTimeKind.Utc);
    }
}