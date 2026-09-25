using FluentAssertions;
using Microsoft.Extensions.Logging;
using OnCallApi.Models;
using OnCallApi.Services;

namespace BackendTests.Services;

/// <summary>
/// What happens to a PHI-access record when the audit queue cannot take it.
///
/// The queue is bounded at 2000 and used to be <c>DropOldest</c> with no counter and no log:
/// under load the audit trail simply developed holes, and nothing anywhere said so. A gap you
/// can measure is a completely different thing from a gap you cannot see — the first is an
/// incident, the second is an audit finding years later with no way to reconstruct what was
/// lost.
///
/// So the contract is: never throw (failing to record an access must not also fail the request
/// that made it), never drop silently, and drop the newest rather than evicting something
/// already accepted.
/// </summary>
public class AuditQueueLossTests
{
    /// <summary>Captures what was logged, so "it said something" can be asserted.</summary>
    private sealed class CapturingLogger : ILogger<AuditService>
    {
        public readonly List<(LogLevel Level, string Message)> Entries = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    private static AuditLog Row(string action = "Read") => new()
    {
        Action = action,
        ResourceType = "Employee",
        ResourceId = "42",
        PrincipalId = "oid-1",
        UserName = "Someone",
        Timestamp = DateTime.UtcNow,
    };

    [Fact]
    public void AnOrdinaryEnqueueIsSilentAndQueued()
    {
        var logger = new CapturingLogger();
        var service = new AuditService(logger);

        service.Enqueue(Row());

        logger.Entries.Should().BeEmpty("a normal write must not generate log noise");
        service.Reader.TryRead(out var read).Should().BeTrue();
        read!.ResourceId.Should().Be("42");
    }

    /// <summary>
    /// Fills the queue past capacity with nothing draining it. The overflow must be reported,
    /// and reported as an Error — a dropped row is a HIPAA record that no longer exists and
    /// that no later process can reconstruct.
    /// </summary>
    [Fact]
    public void OverflowIsLoggedAsAnErrorRatherThanDroppedSilently()
    {
        var logger = new CapturingLogger();
        var service = new AuditService(logger);

        for (var i = 0; i < 2050; i++)
        {
            service.Enqueue(Row($"Read-{i}"));
        }

        var errors = logger.Entries.Where(e => e.Level == LogLevel.Error).ToList();
        errors.Should().NotBeEmpty("dropping a PHI-access record must never be silent");
        errors[0].Message.Should().Contain("Audit queue full");
        errors[0].Message.Should().Contain("audit trail is incomplete");
    }

    /// <summary>
    /// Recording an access must never be able to fail the request that performed it. A throw
    /// here would turn a full queue into a user-facing outage.
    /// </summary>
    [Fact]
    public void OverflowNeverThrows()
    {
        var service = new AuditService(new CapturingLogger());

        var act = () =>
        {
            for (var i = 0; i < 3000; i++) service.Enqueue(Row());
        };

        act.Should().NotThrow();
    }

    /// <summary>
    /// DropWrite, not DropOldest: the record already accepted stays, and the one that could not
    /// be taken is the one reported. Evicting an older entry loses a row nobody can name.
    /// </summary>
    [Fact]
    public void TheEarliestRecordSurvivesAnOverflow()
    {
        var service = new AuditService(new CapturingLogger());

        service.Enqueue(Row("FIRST"));
        for (var i = 0; i < 2500; i++) service.Enqueue(Row($"filler-{i}"));

        service.Reader.TryRead(out var first).Should().BeTrue();
        first!.Action.Should().Be("FIRST", "an accepted record must not be evicted by later ones");
    }
}
