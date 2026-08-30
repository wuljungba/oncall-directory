using Microsoft.EntityFrameworkCore;
using OnCallApi.Data;
using OnCallApi.Models;

namespace OnCallApi.Services;

/// <summary>
/// What one AD directory sync did. Returned rather than only logged, so the caller that
/// triggered it can be told — the manual trigger previously reported a count of users
/// fetched from Graph and nothing about what, if anything, was written.
/// </summary>
public record AdSyncResult(
    int Fetched,
    int Created,
    int Updated,
    int Deactivated,
    IReadOnlyList<string> Skipped,
    string? DeltaToken)
{
    public bool AnythingWritten => Created > 0 || Updated > 0 || Deactivated > 0;
}

public interface IAdDirectorySyncService
{
    Task<AdSyncResult> SyncAsync(string? deltaToken, CancellationToken ct = default);
    Task<string?> GetStoredDeltaTokenAsync(CancellationToken ct = default);
}

/// <summary>
/// Upserts the staff directory from Microsoft Entra.
///
/// This lived inside <see cref="AdSyncBackgroundService"/>, which meant the only way to run
/// it was to wait for the timer. The "sync now" endpoint called a Graph read instead and
/// reported its count as though it had synced, so it always claimed success and never wrote
/// a row. Both paths now run this.
/// </summary>
public class AdDirectorySyncService : IAdDirectorySyncService
{
    private readonly AppDbContext _db;
    private readonly IGraphApiService _graphApi;
    private readonly ILogger<AdDirectorySyncService> _logger;

    public AdDirectorySyncService(
        AppDbContext db, IGraphApiService graphApi, ILogger<AdDirectorySyncService> logger)
    {
        _db = db;
        _graphApi = graphApi;
        _logger = logger;
    }

    public async Task<string?> GetStoredDeltaTokenAsync(CancellationToken ct = default)
    {
        var setting = await _db.AppSettings.FirstOrDefaultAsync(s => s.Key == "AdDeltaToken", ct);
        return setting?.Value;
    }

    public async Task<AdSyncResult> SyncAsync(string? deltaToken, CancellationToken ct = default)
    {
        var (users, newDeltaToken) = await _graphApi.SyncUsersDeltaAsync(deltaToken, ct);

        if (users.Count == 0 && deltaToken != null)
        {
            await StoreDeltaTokenAsync(newDeltaToken, ct);
            return new AdSyncResult(0, 0, 0, 0, [], newDeltaToken);
        }

        var skipped = new List<string>();
        var created = 0;
        var updated = 0;

        foreach (var user in users)
        {
            // Employee.Email is required and uniquely indexed with no filter for blanks.
            // An Entra account with no mailbox — every cloud-only account created in the
            // portal — yields an empty address, so a batch containing two of them violated
            // the index and rolled back the ENTIRE sync, silently. One unusable record must
            // not cost every usable one, so these are skipped and named instead.
            if (string.IsNullOrWhiteSpace(user.Email))
            {
                skipped.Add($"{Describe(user)} — no email address in Entra, so it cannot be added to the directory.");
                continue;
            }

            var existing = await _db.Employees
                .FirstOrDefaultAsync(e => e.AzureAdObjectId == user.AzureAdObjectId, ct);

            if (existing != null)
            {
                existing.FirstName = user.FirstName;
                existing.LastName = user.LastName;
                existing.Title = user.Title;
                existing.Email = user.Email;
                existing.OfficePhone = user.OfficePhone;
                existing.MobilePhone = user.MobilePhone;
                existing.OfficeLocation = user.OfficeLocation;
                existing.Source = "Ad";
                existing.LastSyncedAt = DateTime.UtcNow;
                updated++;
                continue;
            }

            // A different person may already hold this address — the unique index would
            // reject the insert and take the batch down with it. Report it as a conflict.
            var emailTaken = await _db.Employees
                .AnyAsync(e => e.Email == user.Email, ct);
            if (emailTaken)
            {
                skipped.Add($"{Describe(user)} — another directory record already uses that email address.");
                continue;
            }

            user.Source = "Ad";
            user.LastSyncedAt = DateTime.UtcNow;
            _db.Employees.Add(user);
            created++;
        }

        var adObjectIds = users.Select(u => u.AzureAdObjectId).ToHashSet();
        var activeUsers = await _db.Employees.Where(e => e.IsActive).ToListAsync(ct);
        var toDeactivate = SelectEmployeesToDeactivate(activeUsers, adObjectIds);

        foreach (var active in toDeactivate)
        {
            active.IsActive = false;
            active.UpdatedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(ct);
        await StoreDeltaTokenAsync(newDeltaToken, ct);

        if (skipped.Count > 0)
        {
            _logger.LogWarning(
                "AD sync skipped {Count} user(s) that could not be stored: {Reasons}",
                skipped.Count, string.Join(" | ", skipped));
        }

        _logger.LogInformation(
            "AD sync: {Fetched} fetched, {Created} created, {Updated} updated, {Deactivated} deactivated, {Skipped} skipped",
            users.Count, created, updated, toDeactivate.Count, skipped.Count);

        return new AdSyncResult(
            users.Count, created, updated, toDeactivate.Count, skipped, newDeltaToken);
    }

    /// <summary>Names a user without assuming any particular field is populated.</summary>
    private static string Describe(Employee user)
    {
        var name = $"{user.FirstName} {user.LastName}".Trim();
        return !string.IsNullOrWhiteSpace(name) ? name
            : !string.IsNullOrWhiteSpace(user.AzureAdObjectId) ? user.AzureAdObjectId
            : "an unnamed directory entry";
    }

    private async Task StoreDeltaTokenAsync(string? deltaToken, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(deltaToken)) return;

        var setting = await _db.AppSettings.FirstOrDefaultAsync(s => s.Key == "AdDeltaToken", ct);
        if (setting != null)
        {
            setting.Value = deltaToken;
        }
        else
        {
            _db.AppSettings.Add(new AppSetting
            {
                Key = "AdDeltaToken",
                Value = deltaToken,
                Description = "Azure AD Graph API delta token for incremental user sync"
            });
        }
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Pure rule behind AD deactivation: only records tagged <see cref="Employee.Source"/>
    /// == "Ad" and no longer present in AD are selected. Local/CsvImport/unsourced records
    /// are never deactivated, regardless of their <see cref="Employee.AzureAdObjectId"/>.
    /// Kept internal/static so the invariant is unit-testable.
    /// </summary>
    internal static List<Employee> SelectEmployeesToDeactivate(
        IEnumerable<Employee> activeEmployees, HashSet<string> adObjectIds) =>
        activeEmployees
            .Where(e => e.Source == "Ad" && !adObjectIds.Contains(e.AzureAdObjectId))
            .ToList();
}
