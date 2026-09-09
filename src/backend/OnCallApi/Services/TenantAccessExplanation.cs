namespace OnCallApi.Services;

/// <summary>
/// One rule that contributed tenant access to a principal, and what it contributed.
///
/// <see cref="TenantContextService.GetAuthorizedTenantIdsAsync"/> unions four independent
/// sources and returns a bare list of ids, so an over-broad answer gives no clue which rule
/// produced it. Diagnosing that previously meant sweeping every endpoint as the affected
/// user; this exists so it is a lookup instead.
/// </summary>
public sealed record TenantAccessSource
{
    public string Source { get; init; } = string.Empty;
    public List<int> TenantIds { get; init; } = [];
    public string Detail { get; init; } = string.Empty;

    /// <summary>
    /// True when the rule cannot be evaluated from stored data alone — it depends on claims
    /// in the principal's own token, so it is reported as "would apply if" rather than
    /// counted into <see cref="TenantAccessExplanation.EffectiveTenantIds"/>.
    /// </summary>
    public bool Conditional { get; init; }
}

/// <summary>Why a named principal can reach the tenants it can reach.</summary>
public sealed record TenantAccessExplanation
{
    public string Principal { get; init; } = string.Empty;

    /// <summary>
    /// Whether the principal is listed in Authentication:SuperAdmins. This is the only
    /// legitimate source of Admin.Full, and it short-circuits to every active tenant.
    /// </summary>
    public bool IsConfiguredSuperAdmin { get; init; }

    /// <summary>
    /// The tenants the stored rules grant, excluding <see cref="TenantAccessSource.Conditional"/>
    /// ones. Sorted, de-duplicated.
    /// </summary>
    public List<int> EffectiveTenantIds { get; init; } = [];

    public List<TenantAccessSource> Sources { get; init; } = [];
}
