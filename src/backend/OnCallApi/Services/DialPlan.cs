using Microsoft.EntityFrameworkCore;
using OnCallApi.Data;
using OnCallApi.Models;
using OnCallApi.Validators;

namespace OnCallApi.Services;

/// <summary>
/// Turns an internal extension into a number an outside caller can dial, using the
/// extension prefix configured for a subscription.
///
/// A hospital directory records "3North, x3434" because that is what staff dial from a
/// desk phone. Anyone outside the building needs the whole number, and the missing piece
/// is a per-site constant: prefix "845568" plus extension "3434" is +1 845 568 3434.
///
/// Nothing here guesses. With no prefix configured the extension is stored on its own and
/// the phone column stays empty, because a fabricated number does not fail visibly -- it
/// reaches a stranger's switchboard, and on the code-call path that is a page nobody
/// answers.
/// </summary>
public static class DialPlan
{
    /// <summary>The app-wide default, used when a subscription sets none of its own.</summary>
    public const string ExtensionPrefixKey = "Directory:ExtensionPrefix";

    /// <summary>
    /// The per-subscription key. The tenant is part of the KEY rather than a filter on
    /// AppSetting.TenantId because AppSetting's primary key is the key column alone --
    /// two subscriptions cannot otherwise hold a value under the same name. TenantId is
    /// still populated on the row so the existing tenant index and scoping keep meaning.
    /// </summary>
    public static string ExtensionPrefixKeyFor(int tenantId) =>
        $"{ExtensionPrefixKey}:{tenantId}";

    /// <summary>
    /// The extension prefix in force for a subscription: its own if it has one, otherwise
    /// the app-wide default, otherwise null.
    /// </summary>
    public static async Task<string?> ResolveExtensionPrefixAsync(
        AppDbContext db, int? tenantId, CancellationToken ct = default)
    {
        var keys = tenantId.HasValue
            ? new[] { ExtensionPrefixKeyFor(tenantId.Value), ExtensionPrefixKey }
            : [ExtensionPrefixKey];

        var found = await db.AppSettings
            .AsNoTracking()
            .Where(s => keys.Contains(s.Key))
            .Select(s => new { s.Key, s.Value })
            .ToListAsync(ct);

        // Ordered by the caller's preference, not by whatever the database returned.
        foreach (var key in keys)
        {
            var value = found.FirstOrDefault(s => s.Key == key)?.Value;
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }

        return null;
    }

    /// <summary>
    /// Fills in an employee's office phone from their extension when the extension is all
    /// the source data gave. An existing number is never overwritten: a real number the
    /// file supplied always beats one derived from a prefix.
    /// </summary>
    /// <returns>True when a number was derived and assigned.</returns>
    public static bool ApplyExtensionPrefix(Employee employee, string? prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix)) return false;
        if (string.IsNullOrWhiteSpace(employee.Extension)) return false;
        if (!string.IsNullOrWhiteSpace(employee.OfficePhone)) return false;

        var derived = PhoneValidation.BuildNumberFromExtension(prefix, employee.Extension);
        if (derived == null) return false;

        employee.OfficePhone = derived;
        return true;
    }
}
