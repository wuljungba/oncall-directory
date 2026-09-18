using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OnCallApi.Data;
using OnCallApi.Models;
using OnCallApi.Services;

namespace BackendTests.Services;

/// <summary>
/// Removing a tenant administrator because they were absent from a group read.
///
/// This is the same "absence means gone" mistake as the directory sync, on the table that grants
/// administrative access: group members page at 100, the read was unpaged, and every admin past
/// the first page would have been revoked on every cycle. It also asked OUR directory for a group
/// id that exists only in the customer's, so the read came back empty — and an empty-result guard
/// was the only thing preventing a wholesale revocation.
/// </summary>
public class TenantAdminSyncGuardTests
{
    private const int TenantId = 1;
    private const string CustomerDirectory = "aaaaaaaa-1111-2222-3333-444444444444";
    private const string AdminGroup = "group-admins";

    private static AppDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new AppDbContext(options);
        db.Tenants.Add(new Tenant
        {
            Id = TenantId,
            Name = "Northwood Medical",
            IsActive = true,
            AzureAdGroupId = AdminGroup,
            AzureAdTenantId = CustomerDirectory,
        });
        db.TenantAdmins.Add(new TenantAdmin
        {
            TenantId = TenantId, AzureAdObjectId = "stays", IsAutoAssigned = true, Role = "DepartmentAdmin",
        });
        db.TenantAdmins.Add(new TenantAdmin
        {
            TenantId = TenantId, AzureAdObjectId = "missing-from-this-read", IsAutoAssigned = true, Role = "DepartmentAdmin",
        });
        db.SaveChanges();
        return db;
    }

    private static Employee Member(string objectId) => new() { AzureAdObjectId = objectId };

    private static TenantSyncService CreateService(AppDbContext db, FakeGraphApiService graph) =>
        new(db, graph, NullLogger<TenantSyncService>.Instance);

    [Fact]
    public async Task ATruncatedMembershipReadRevokesNobody()
    {
        using var db = CreateDb();
        var graph = new FakeGraphApiService();
        graph.Members[AdminGroup] = new GraphMembersResult(
            [Member("stays")], Completed: false, "Graph stopped answering half way through.");

        await CreateService(db, graph).SyncAllAsync();

        db.TenantAdmins.Any(a => a.AzureAdObjectId == "missing-from-this-read").Should()
            .BeTrue("absence from a partial read is not evidence that somebody's access should end");
    }

    /// <summary>The control: without it, the test above passes by never revoking anything at all.</summary>
    [Fact]
    public async Task ACompleteMembershipReadStillRevokesSomebodyWhoLeftTheGroup()
    {
        using var db = CreateDb();
        var graph = new FakeGraphApiService();
        graph.Members[AdminGroup] = new GraphMembersResult([Member("stays")], Completed: true, null);

        await CreateService(db, graph).SyncAllAsync();

        db.TenantAdmins.Any(a => a.AzureAdObjectId == "missing-from-this-read").Should().BeFalse();
        db.TenantAdmins.Any(a => a.AzureAdObjectId == "stays").Should().BeTrue();
    }

    [Fact]
    public async Task TheGroupIsReadFromTheCustomersOwnDirectory()
    {
        using var db = CreateDb();
        var graph = new FakeGraphApiService();
        graph.Members[AdminGroup] = new GraphMembersResult([Member("stays")], Completed: true, null);

        await CreateService(db, graph).SyncAllAsync();

        graph.MemberCalls.Should().ContainSingle();
        graph.MemberCalls[0].EntraTenantId.Should()
            .Be(CustomerDirectory, "a group id from the customer's directory does not exist in ours");
    }
}
