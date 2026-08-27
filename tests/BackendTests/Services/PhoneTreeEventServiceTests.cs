using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OnCallApi.Data;
using OnCallApi.Models;
using OnCallApi.Services;

namespace BackendTests.Services;

/// <summary>
/// Covers the on-call code-call event log: reporter captured and tree loaded on
/// create (so the right code type dispatches), and notified-person + end time set
/// on resolve.
/// </summary>
public class PhoneTreeEventServiceTests
{
    private static AppDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new AppDbContext(options);
        db.PhoneTrees.Add(new PhoneTree { Id = 1, Name = "Code Blue", TreeType = "code-blue" });
        db.SaveChanges();
        return db;
    }

    [Fact]
    public async Task CreateEventAsync_PersistsReporterAndLoadsCodeType()
    {
        var db = CreateDb();
        var service = new PhoneTreeEventService(db, TestTenantScopes.Unrestricted, NullLogger<PhoneTreeEventService>.Instance);

        var created = await service.CreateEventAsync(new PhoneTreeEvent
        {
            PhoneTreeId = 1,
            RequestedByName = "RN Smith",
            Location = "ER",
            StartedAt = DateTime.UtcNow,
        });

        created.Id.Should().BeGreaterThan(0);
        created.Status.Should().Be("active");
        created.PhoneTree.Should().NotBeNull();
        created.PhoneTree!.TreeType.Should().Be("code-blue");

        var reloaded = await db.PhoneTreeEvents.FindAsync(created.Id);
        reloaded!.RequestedByName.Should().Be("RN Smith");
    }

    [Fact]
    public async Task ResolveEventAsync_StoresNotifiedNameAndEndsEvent()
    {
        var db = CreateDb();
        var service = new PhoneTreeEventService(db, TestTenantScopes.Unrestricted, NullLogger<PhoneTreeEventService>.Instance);

        var created = await service.CreateEventAsync(new PhoneTreeEvent
        {
            PhoneTreeId = 1,
            StartedAt = DateTime.UtcNow.AddHours(-1),
        });

        var resolved = await service.ResolveEventAsync(created.Id, "Received", "Charge Nurse Jones");

        resolved.Status.Should().Be("completed");
        resolved.NotifiedByName.Should().Be("Charge Nurse Jones");
        resolved.EndedAt.Should().NotBeNull();
        resolved.ResponseTimeSeconds.Should().BeGreaterThan(0);
    }
}