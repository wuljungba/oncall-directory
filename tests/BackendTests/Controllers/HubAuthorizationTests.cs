using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using OnCallApi.Authorization;
using OnCallApi.Data;
using OnCallApi.Hubs;
using OnCallApi.Models;
using OnCallApi.Services;

namespace BackendTests.Controllers;

/// <summary>
/// Hub group membership is authorization, not routing.
///
/// JoinTenant and JoinDepartment took an id straight from the caller and added them to
/// that group unchecked, so any authenticated user could invoke
/// connection.invoke("JoinTenant", 7) and receive another tenant's live code-call feed —
/// including patient location and incident notes.
/// </summary>
public class HubAuthorizationTests
{
    private const int MyTenant = 1;
    private const int OtherTenant = 2;
    private const string MyObjectId = "user-with-tenant-1";

    private static AppDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new AppDbContext(options);
        db.Tenants.AddRange(
            new Tenant { Id = MyTenant, Name = "Main Hospital", IsActive = true, CreatedAt = DateTime.UtcNow },
            new Tenant { Id = OtherTenant, Name = "North Campus", IsActive = true, CreatedAt = DateTime.UtcNow });
        db.Departments.AddRange(
            new Department { Id = 10, Name = "Cardiology", TenantId = MyTenant, IsActive = true },
            new Department { Id = 20, Name = "Neurology", TenantId = OtherTenant, IsActive = true });
        db.TenantAdmins.Add(new TenantAdmin
        {
            TenantId = MyTenant,
            AzureAdObjectId = MyObjectId,
            Role = "DepartmentAdmin",
            CreatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
        return db;
    }

    /// <summary>Records which groups the hub asked to join.</summary>
    private sealed class RecordingGroupManager : IGroupManager
    {
        public List<string> Added { get; } = [];
        public List<string> Removed { get; } = [];

        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        {
            Added.Add(groupName);
            return Task.CompletedTask;
        }

        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        {
            Removed.Add(groupName);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeHubContext : HubCallerContext
    {
        private readonly ClaimsPrincipal _user;
        public FakeHubContext(ClaimsPrincipal user) => _user = user;
        public override string ConnectionId => "test-connection";
        public override string? UserIdentifier => "test-user";
        public override ClaimsPrincipal? User => _user;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() { }
    }

    private static (OnCallNotificationHub Hub, RecordingGroupManager Groups) CreateHub(AppDbContext db, bool superAdmin = false)
    {
        var claims = new List<Claim>
        {
            new("oid", MyObjectId),
            new(ClaimTypes.NameIdentifier, MyObjectId),
        };
        if (superAdmin) claims.Add(new Claim(Permissions.ClaimType, Permissions.AdminFull));

        var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { RequestServices = services },
        };

        var hub = new OnCallNotificationHub(
            new TenantContextService(db, accessor),
            NullLogger<OnCallNotificationHub>.Instance);

        var groups = new RecordingGroupManager();
        hub.Groups = groups;
        hub.Context = new FakeHubContext(new ClaimsPrincipal(new ClaimsIdentity(claims, "test")));
        return (hub, groups);
    }

    [Fact]
    public async Task JoinTenant_OwnTenant_IsAllowed()
    {
        var (hub, groups) = CreateHub(CreateDb());

        await hub.JoinTenant(MyTenant);

        groups.Added.Should().ContainSingle().Which.Should().Be($"tenant-{MyTenant}");
    }

    [Fact]
    public async Task JoinTenant_AnotherTenant_IsRejected()
    {
        var (hub, groups) = CreateHub(CreateDb());

        var act = () => hub.JoinTenant(OtherTenant);

        await act.Should().ThrowAsync<HubException>();
        groups.Added.Should().BeEmpty("the caller must never be subscribed to another tenant's feed");
    }

    [Fact]
    public async Task JoinDepartment_InOwnTenant_IsAllowed()
    {
        var (hub, groups) = CreateHub(CreateDb());

        await hub.JoinDepartment(10);

        groups.Added.Should().ContainSingle().Which.Should().Be("dept-10");
    }

    [Fact]
    public async Task JoinDepartment_InAnotherTenant_IsRejected()
    {
        var (hub, groups) = CreateHub(CreateDb());

        var act = () => hub.JoinDepartment(20);

        await act.Should().ThrowAsync<HubException>();
        groups.Added.Should().BeEmpty();
    }

    [Fact]
    public async Task SuperAdmin_MayJoinAnyTenant()
    {
        var (hub, groups) = CreateHub(CreateDb(), superAdmin: true);

        await hub.JoinTenant(OtherTenant);

        groups.Added.Should().ContainSingle().Which.Should().Be($"tenant-{OtherTenant}");
    }

    [Fact]
    public async Task Leaving_IsAlwaysPermitted()
    {
        var (hub, groups) = CreateHub(CreateDb());

        await hub.LeaveTenant(OtherTenant);
        await hub.LeaveDepartment(20);

        groups.Removed.Should().BeEquivalentTo([$"tenant-{OtherTenant}", "dept-20"]);
    }

    [Fact]
    public async Task OnConnected_JoinsOnlyTheCallersOwnTenants()
    {
        var (hub, groups) = CreateHub(CreateDb());

        await hub.OnConnectedAsync();

        groups.Added.Should().Contain($"tenant-{MyTenant}");
        groups.Added.Should().NotContain($"tenant-{OtherTenant}");
    }
}
