using BackendTests.Services;
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
/// What group membership may and may not decide about a person's department.
///
/// Membership of an M365 group is evidence, not an instruction. Somebody can be in several
/// groups, so assigning from every one of them meant whichever synced last owned them — a
/// clinician's department could change by itself every cycle — and it overwrote departments
/// admins had set by hand with no record that it had.
///
/// Department decides who a code call pages, so these pin the rule: it fills a blank, and
/// nothing else.
/// </summary>
public class DepartmentSyncAssignmentTests
{
    private const int TenantId = 7001;
    private const string GroupId = "group-cardiology";
    private const string OtherGroupId = "group-icu";

    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"dept-sync-{Guid.NewGuid():N}")
            .Options);

    private static DepartmentSyncService NewService() =>
        new(new ServiceCollection().BuildServiceProvider(),
            new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["Sync:DepartmentSyncIntervalMinutes"] = "360" }).Build(),
            NullLogger<DepartmentSyncService>.Instance);

    private static Employee Person(Guid id, string objectId, int? departmentId) => new()
    {
        Id = id,
        FirstName = "Case",
        LastName = "Person",
        Email = $"{objectId}@main.test",
        AzureAdObjectId = objectId,
        TenantId = TenantId,
        DepartmentId = departmentId,
        IsActive = true,
    };

    /// <summary>A member as Graph hands it back: an object id and little else.</summary>
    private static Employee Member(string objectId) => new()
    {
        Id = Guid.NewGuid(),
        AzureAdObjectId = objectId,
        FirstName = "From",
        LastName = "Graph",
    };

    private static async Task<AppDbContext> RunAsync(
        FakeGraphApiService graph, Action<AppDbContext> seed)
    {
        var db = NewDb();
        seed(db);
        await db.SaveChangesAsync();

        await NewService().SyncDirectoryAsync(
            graph, db, new DirectoryTarget(TenantId, "dir-1", "Main Hospital"), CancellationToken.None);

        return db;
    }

    private static FakeGraphApiService GraphWith(string groupId, string groupName, params string[] memberObjectIds)
    {
        var graph = new FakeGraphApiService
        {
            Groups = new GraphGroupsResult([new GroupInfo(groupId, groupName, null)], true, null),
        };
        graph.Members[groupId] = new GraphMembersResult(
            memberObjectIds.Select(Member).ToList(), true, null);
        return graph;
    }

    [Fact]
    public async Task SomebodyWithNoDepartmentGetsTheGroupsDepartment()
    {
        var personId = Guid.NewGuid();
        var graph = GraphWith(GroupId, "Cardiology", "oid-1");

        using var db = await RunAsync(graph, d => d.Employees.Add(Person(personId, "oid-1", departmentId: null)));

        var person = await db.Employees.SingleAsync(e => e.Id == personId);
        person.DepartmentId.Should().NotBeNull();

        var department = await db.Departments.SingleAsync(x => x.AzureAdGroupId == GroupId);
        person.DepartmentId.Should().Be(department.Id);
    }

    /// <summary>
    /// The rule that matters. An admin moved this person deliberately; a group they happen to
    /// still belong to must not move them back on the next cycle.
    /// </summary>
    [Fact]
    public async Task ADepartmentSomebodyAlreadyHasIsLeftAlone()
    {
        var personId = Guid.NewGuid();
        var graph = GraphWith(GroupId, "Cardiology", "oid-1");

        using var db = await RunAsync(graph, d =>
        {
            d.Departments.Add(new Department { Id = 9100, Name = "Emergency", TenantId = TenantId, IsActive = true });
            d.Employees.Add(Person(personId, "oid-1", departmentId: 9100));
        });

        (await db.Employees.SingleAsync(e => e.Id == personId)).DepartmentId
            .Should().Be(9100, "an assignment a person made outranks one inferred from a group");
    }

    /// <summary>
    /// Two groups, one person. Before the rule, the second group processed simply took them —
    /// so the answer depended on the order Graph happened to return groups in.
    /// </summary>
    [Fact]
    public async Task BelongingToTwoGroupsDoesNotMakeTheLastOneWin()
    {
        var personId = Guid.NewGuid();
        var graph = new FakeGraphApiService
        {
            Groups = new GraphGroupsResult(
                [new GroupInfo(GroupId, "Cardiology", null), new GroupInfo(OtherGroupId, "ICU", null)], true, null),
        };
        graph.Members[GroupId] = new GraphMembersResult([Member("oid-1")], true, null);
        graph.Members[OtherGroupId] = new GraphMembersResult([Member("oid-1")], true, null);

        using var db = await RunAsync(graph, d => d.Employees.Add(Person(personId, "oid-1", departmentId: null)));

        var cardiology = await db.Departments.SingleAsync(x => x.AzureAdGroupId == GroupId);
        (await db.Employees.SingleAsync(e => e.Id == personId)).DepartmentId
            .Should().Be(cardiology.Id, "the first group to fill the blank keeps them");
    }

    /// <summary>
    /// Scoping still holds: this is the rule that stops one customer's group adopting another
    /// customer's staff, and filling only blanks must not have loosened it.
    /// </summary>
    [Fact]
    public async Task AnotherTenantsEmployeeIsNotTouchedEvenWhenBlank()
    {
        var outsiderId = Guid.NewGuid();
        var graph = GraphWith(GroupId, "Cardiology", "oid-outsider");

        using var db = await RunAsync(graph, d =>
        {
            var outsider = Person(outsiderId, "oid-outsider", departmentId: null);
            outsider.TenantId = TenantId + 1;
            d.Employees.Add(outsider);
        });

        (await db.Employees.SingleAsync(e => e.Id == outsiderId)).DepartmentId.Should().BeNull();
    }
}
