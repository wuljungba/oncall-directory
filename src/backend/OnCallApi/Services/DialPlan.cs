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
    /// The per-department key, for a multi-campus organization whose buildings sit behind
    /// different prefixes. A second campus is the only reason this exists; a single-site
    /// customer never touches it and inherits the subscription's value.
    /// </summary>
    public static string ExtensionPrefixKeyForDepartment(int departmentId) =>
        $"{ExtensionPrefixKey}:Department:{departmentId}";

    /// <summary>
    /// The extension prefix in force, most specific first: the department's own, then the
    /// subscription's, then the app-wide default, then nothing.
    /// </summary>
    public static async Task<string?> ResolveExtensionPrefixAsync(
        AppDbContext db, int? tenantId, int? departmentId = null, CancellationToken ct = default)
    {
        var keys = new List<string>();
        if (departmentId.HasValue) keys.Add(ExtensionPrefixKeyForDepartment(departmentId.Value));
        if (tenantId.HasValue) keys.Add(ExtensionPrefixKeyFor(tenantId.Value));
        keys.Add(ExtensionPrefixKey);

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
    /// The prefixes in force across one import: a subscription-wide default, plus any
    /// department that overrides it.
    /// </summary>
    public sealed class ExtensionPrefixes
    {
        private readonly IReadOnlyDictionary<int, string> _byDepartment;

        internal ExtensionPrefixes(string? fallback, IReadOnlyDictionary<int, string> byDepartment)
        {
            Default = fallback;
            _byDepartment = byDepartment;
        }

        /// <summary>The subscription-wide prefix, or null when none is configured.</summary>
        public string? Default { get; }

        /// <summary>The prefix for a department, falling back to the subscription's.</summary>
        public string? For(int? departmentId) =>
            departmentId.HasValue && _byDepartment.TryGetValue(departmentId.Value, out var own)
                ? own
                : Default;
    }

    /// <summary>
    /// Every prefix that could apply to an import, resolved together.
    ///
    /// One query rather than one per row: an import of four hundred contacts spread over a
    /// dozen departments would otherwise issue four hundred lookups for a handful of values
    /// that cannot change mid-import.
    /// </summary>
    public static async Task<ExtensionPrefixes> ResolveExtensionPrefixesAsync(
        AppDbContext db, int? tenantId, IReadOnlyCollection<int> departmentIds,
        CancellationToken ct = default)
    {
        var fallback = await ResolveExtensionPrefixAsync(db, tenantId, null, ct);

        var byDepartment = new Dictionary<int, string>();
        if (departmentIds.Count == 0) return new ExtensionPrefixes(fallback, byDepartment);

        var wanted = departmentIds.Distinct().ToList();
        var keys = wanted.Select(ExtensionPrefixKeyForDepartment).ToList();

        var overrides = await db.AppSettings
            .AsNoTracking()
            .Where(s => keys.Contains(s.Key))
            .Select(s => new { s.Key, s.Value })
            .ToListAsync(ct);

        foreach (var id in wanted)
        {
            var value = overrides
                .FirstOrDefault(o => o.Key == ExtensionPrefixKeyForDepartment(id))?.Value;

            // Only an override is stored. A department with none simply falls through to
            // the subscription's value at lookup time.
            if (!string.IsNullOrWhiteSpace(value)) byDepartment[id] = value.Trim();
        }

        return new ExtensionPrefixes(fallback, byDepartment);
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
