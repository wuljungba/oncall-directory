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
}