using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OnCallApi.Data;
using OnCallApi.Hubs;
using OnCallApi.Models;
using OnCallApi.Services;

namespace BackendTests.Controllers;

/// <summary>
/// Real-time notifications must reach one tenant, never all of them.
///
/// Every controller used to call Clients.All.SendAsync, so one customer's staff changes,
/// schedule changes and live code-call incidents were pushed to every other customer's
/// connected clients. Group membership was already gated correctly — it was the broadcast
/// side that ignored it.
///
/// The old fallback, when a tenant could not be resolved, was to send to everyone. That is
/// a cross-tenant leak dressed as a delivery guarantee: dropping the message is the safe
/// direction, and the drop must be reported rather than swallowed.
/// </summary>
public class TenantBroadcastTests
{
    /// <summary>Records which group was addressed, and fails loudly if "all" is ever used.</summary>
    private sealed class RecordingClients : IHubClients
    {
        public List<string> AddressedGroups { get; } = new();
        public bool AllWasUsed { get; private set; }

        public IClientProxy All
        {
            get { AllWasUsed = true; return new NoopProxy(); }
        }

        public IClientProxy Group(string groupName)
        {
            AddressedGroups.Add(groupName);
            return new NoopProxy();
        }

        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => new NoopProxy();
        public IClientProxy Client(string connectionId) => new NoopProxy();
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => new NoopProxy();
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => new NoopProxy();
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => new NoopProxy();
        public IClientProxy User(string userId) => new NoopProxy();
        public IClientProxy Users(IReadOnlyList<string> userIds) => new NoopProxy();

        private sealed class NoopProxy : IClientProxy
        {
            public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
                => Task.CompletedTask;
        }
    }

    private sealed class StubHubContext : IHubContext<OnCallNotificationHub>
    {
        public RecordingClients Recording { get; } = new();
        public IHubClients Clients => Recording;
        public IGroupManager Groups => throw new NotSupportedException();
    }

    private sealed class RecordingAudit : IAuditService
    {
        public List<AuditLog> Logs { get; } = new();
        public void Enqueue(AuditLog log) => Logs.Add(log);
    }

    private static AppDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"broadcast-{Guid.NewGuid():N}")
            .Options;
        return new AppDbContext(options);
    }

    private static (TenantBroadcaster Broadcaster, StubHubContext Hub, RecordingAudit Audit, AppDbContext Db) Build()
    {
        var hub = new StubHubContext();
        var audit = new RecordingAudit();
        var db = NewDb();
        var broadcaster = new TenantBroadcaster(hub, db, audit, NullLogger<TenantBroadcaster>.Instance);
        return (broadcaster, hub, audit, db);
    }

    [Fact]
    public async Task SendsToTheTenantsOwnGroup()
    {
        var (broadcaster, hub, _, _) = Build();

        await broadcaster.ToTenantAsync(7, "IncidentCreated", new { id = 1 });

        hub.Recording.AddressedGroups.Should().BeEquivalentTo("tenant-7");
        hub.Recording.AllWasUsed.Should().BeFalse();
    }

    [Fact]
    public async Task NeverBroadcastsToEveryClientWhenTheTenantIsUnknown()
    {
        var (broadcaster, hub, _, _) = Build();

        await broadcaster.ToTenantAsync(null, "IncidentCreated", new { id = 1 });

        hub.Recording.AllWasUsed.Should().BeFalse("an unresolved tenant must never fan out to other customers");
        hub.Recording.AddressedGroups.Should().BeEmpty();
    }

    [Fact]
    public async Task RecordsAnUndeliverableSafetyCriticalNotification()
    {
        var (broadcaster, _, audit, _) = Build();

        await broadcaster.ToTenantAsync(null, "IncidentCreated", new { id = 1 }, safetyCritical: true);

        audit.Logs.Should().ContainSingle()
            .Which.Action.Should().Be("NotificationUndeliverable");
    }

    [Fact]
    public async Task ResolvesTheTenantThroughAPhoneTreesDepartment()
    {
        var (broadcaster, _, _, db) = Build();
        db.Tenants.Add(new Tenant { Id = 3, Name = "North Campus", IsActive = true });
        db.Departments.Add(new Department { Id = 30, Name = "Neurology", TenantId = 3, IsActive = true });
        db.PhoneTrees.Add(new PhoneTree { Id = 300, Name = "Code Blue", DepartmentId = 30, IsActive = true });
        db.PhoneTreeEvents.Add(new PhoneTreeEvent { Id = 3000, PhoneTreeId = 300, Status = "active" });
        await db.SaveChangesAsync();

        (await broadcaster.TenantForPhoneTreeAsync(300)).Should().Be(3);
        (await broadcaster.TenantForEventAsync(3000)).Should().Be(3);
    }

    [Fact]
    public async Task ReportsNoTenantForAnUnknownEntityRatherThanGuessing()
    {
        var (broadcaster, _, _, _) = Build();

        (await broadcaster.TenantForEventAsync(999999)).Should().BeNull();
        (await broadcaster.TenantForDepartmentAsync(null)).Should().BeNull();
    }
}
