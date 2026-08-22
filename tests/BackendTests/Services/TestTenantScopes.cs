using OnCallApi.Services;

namespace BackendTests.Services;

/// <summary>
/// Tenant scopes for unit tests.
///
/// The scope is a required constructor argument on the services that read tenant-owned
/// data, deliberately: an optional one would let a wiring mistake silently produce an
/// unscoped query, which is the failure mode the scoping work exists to remove. Tests
/// therefore state which posture they want.
/// </summary>
internal static class TestTenantScopes
{
    /// <summary>No restriction — how a super admin or a background service sees the estate.</summary>
    public static ITenantScope Unrestricted { get; } = new StubScope(null);

    /// <summary>Restricted to the given tenants. An empty set means "sees nothing".</summary>
    public static ITenantScope For(params int[] tenantIds) => new StubScope([.. tenantIds]);

    private sealed class StubScope : ITenantScope
    {
        private readonly List<int>? _tenantIds;
        public StubScope(List<int>? tenantIds) => _tenantIds = tenantIds;
        public Task<List<int>?> AllowedTenantIdsAsync() => Task.FromResult(_tenantIds);
    }
}
