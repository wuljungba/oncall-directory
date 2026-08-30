using FluentAssertions;
using OnCallApi.Models;
using OnCallApi.Services;

namespace BackendTests.Services;

/// <summary>
/// Guards the onboarding-standard invariant: the AD sync may deactivate ONLY
/// Source="Ad" records that are no longer in the directory. Locally/CSV-created
/// records must never be deactivated, no matter what their AzureAdObjectId is.
/// </summary>
public class AdSyncBackgroundServiceTests
{
    [Fact]
    public void SelectEmployeesToDeactivate_OnlyAdSourcedAndMissingFromAd()
    {
        var adObjectIds = new HashSet<string>
        {
            "00000000-0000-0000-0000-000000000001", // still in AD
        };

        var employees = new List<Employee>
        {
            new() { Source = "Ad", AzureAdObjectId = "00000000-0000-0000-0000-000000000001" }, // present -> keep
            new() { Source = "Ad", AzureAdObjectId = "removed-from-ad" },                        // Ad + gone  -> deactivate
            new() { Source = "Local", AzureAdObjectId = "00000000-0000-0000-0000-000000000099" },// local, not in AD -> keep
            new() { Source = "CsvImport", AzureAdObjectId = "csv-fake-uuid" },                   // import, not in AD -> keep
            new() { Source = "", AzureAdObjectId = "legacy-fake" },                              // unsourced -> keep
        };

        var result = AdSyncBackgroundService.SelectEmployeesToDeactivate(employees, adObjectIds);

        result.Should().ContainSingle();
        result[0].AzureAdObjectId.Should().Be("removed-from-ad");
    }

    [Fact]
    public void SelectEmployeesToDeactivate_ScopedToOneTenant_LeavesOtherTenantsAlone()
    {
        // A directory can only speak for its own tenant. Absence from tenant A's directory
        // says nothing whatever about tenant B's staff, and before this was scoped, syncing
        // one customer deactivated every other customer's people.
        var adObjectIds = new HashSet<string> { "tenant-a-present" };

        var employees = new List<Employee>
        {
            new() { Source = "Ad", TenantId = 1, AzureAdObjectId = "tenant-a-present" }, // keep
            new() { Source = "Ad", TenantId = 1, AzureAdObjectId = "tenant-a-gone" },    // deactivate
            new() { Source = "Ad", TenantId = 2, AzureAdObjectId = "tenant-b-person" },  // other tenant -> keep
            new() { Source = "Ad", TenantId = null, AzureAdObjectId = "home-person" },   // home -> keep
        };

        var result = AdDirectorySyncService.SelectEmployeesToDeactivate(employees, adObjectIds, tenantId: 1);

        result.Should().ContainSingle();
        result[0].AzureAdObjectId.Should().Be("tenant-a-gone");
    }

    [Fact]
    public void SelectEmployeesToDeactivate_HomeDirectory_OnlyTouchesUnownedRecords()
    {
        var adObjectIds = new HashSet<string>();

        var employees = new List<Employee>
        {
            new() { Source = "Ad", TenantId = null, AzureAdObjectId = "home-gone" },     // deactivate
            new() { Source = "Ad", TenantId = 7, AzureAdObjectId = "customer-person" },  // keep
        };

        var result = AdDirectorySyncService.SelectEmployeesToDeactivate(employees, adObjectIds, tenantId: null);

        result.Should().ContainSingle();
        result[0].AzureAdObjectId.Should().Be("home-gone");
    }
}
