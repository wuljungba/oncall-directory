using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OnCallApi.Authorization;
using OnCallApi.Configuration;
using OnCallApi.Data;
using OnCallApi.Services;

namespace OnCallApi.Middleware;

/// <summary>
/// Middleware that runs after authentication to expand the user's claims
/// with tenant-scoped permissions from TenantAdmin records.
///
/// For each TenantAdmin record matching the user's AzureAdObjectId,
/// this middleware adds:
///   - A "TenantId:{id}" claim with the admin's role as the value
///   - Granular permission claims (Schedule.Read, Directory.Write, etc.)
///
/// Also performs lazy auto-assignment: if the user's Azure AD group claims
/// match a Tenant.AzureAdGroupId, a TenantAdmin record is created on the fly.
/// This handles the case where a user was added to a group between sync cycles.
/// </summary>
public class TenantClaimsMiddleware
{
    private readonly RequestDelegate _next;
    private readonly SuperAdminOptions _superAdmins;

    public TenantClaimsMiddleware(RequestDelegate next, IOptions<SuperAdminOptions> superAdmins)
    {
        _next = next;
        _superAdmins = superAdmins.Value;
    }

    /// <param name="identities">
    /// Optional so the middleware can be exercised standalone in tests. Claim expansion is
    /// the job here; recording who signed in is a side benefit that must never be able to
    /// affect a request.
    /// </param>
    public async Task InvokeAsync(HttpContext context, AppDbContext db, IIdentityDirectoryService? identities = null)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            // Record that this principal exists, so administrators can find and provision
            // them. Entra/Google users otherwise leave no trace until someone grants them
            // permission — which requires already knowing their address. Enqueue-only and
            // throttled, so it costs nothing on the request path.
            if (identities != null) RecordSignIn(context.User, identities);

            try
            {
                var identity = context.User.Identity as ClaimsIdentity;
                if (identity == null)
                {
                    await _next(context);
                    return;
                }

                // Configured super administrators get every role + permission.
                // Real Entra/Google tokens carry no app roles, so this is the only
                // way a real user can obtain Admin.Full / Tenant.Manage today.
                if (IsConfiguredSuperAdmin(context.User))
                {
                    await GrantSuperAdminAsync(identity, db);
                }
                else
                {
                    var azureAdObjectId = GetAzureAdObjectId(context.User);
                    var tid = GetTenantId(context.User);

                    // Skip if tenant claims are already present (avoid re-adding on repeated requests)
                    if (!context.User.HasClaim(c => c.Type.StartsWith("TenantId:")))
                    {
                        if (!string.IsNullOrEmpty(azureAdObjectId))
                        {
                            await AddTenantClaimsAsync(identity, azureAdObjectId, db);

                            // Auto-assignment via Azure AD group membership. This stays
                            // because it IS an invitation: an administrator maps a specific
                            // Entra group to a tenant, and someone must add the user to it.
                            await TryAutoAssignFromGroupsAsync(
                                context.User, identity, azureAdObjectId, db,
                                context.RequestServices.GetRequiredService<ILogger<TenantClaimsMiddleware>>());
                        }

                        // NOTE: matching the token's `tid` alone used to auto-create a
                        // DepartmentAdmin row and grant ScopedAdminPermissions — which
                        // includes CodeCall.Write. That made every employee in the
                        // hospital's Entra tenant a department admin able to fire a live
                        // code call on first sign-in, with no invitation or approval.
                        // Access is now explicit: a user is provisioned by an admin from
                        // Admin -> Users & Permissions, where they appear as soon as they
                        // sign in. `tid` is still recorded on the identity for context.
                        _ = tid;
                    }
                }

                // Explicit per-user permission grants (PermissionGrant rows). Runs for every
                // authenticated user — for a super admin it's harmless (existing claims win;
                // AddClaim via HasClaim guard de-dupes). This is how external Entra/Google
                // users receive assignable Schedule.Read/Write permissions from the dashboard.
                try
                {
                    await AddPermissionGrantsAsync(identity, context.User, db);
                }
                catch (Exception ex)
                {
                    // Isolated on purpose: a missing/misconfigured PermissionGrants table
                    // must NOT break the broader tenant-claim expansion above.
                    context.RequestServices.GetRequiredService<ILogger<TenantClaimsMiddleware>>()
                        .LogWarning(ex, "Per-user permission grant expansion failed — continuing without grants");
                }
            }
            catch (Exception ex)
            {
                // If the Tenants/TenantAdmins tables don't exist yet (migration not applied)
                // or any DB error occurs, silently skip tenant claim expansion.
                // The app functions without tenant scoping until the migration is run.
                // This prevents a missing migration from breaking all API requests.
                var logger = context.RequestServices.GetRequiredService<ILogger<TenantClaimsMiddleware>>();
                logger.LogWarning(ex, "Tenant claims expansion skipped (DB not ready or tenant tables missing)");
            }
        }

        await _next(context);
    }

    private static async Task AddTenantClaimsAsync(ClaimsIdentity identity, string azureAdObjectId, AppDbContext db)
    {
        var adminRecords = await db.TenantAdmins
            .Where(a => a.AzureAdObjectId == azureAdObjectId)
            .Where(a => a.Tenant.IsActive)
            .ToListAsync();

        foreach (var admin in adminRecords)
        {
            // Add tenant-scoped claim: "TenantId:{id}" with value "DepartmentAdmin" or "SuperAdmin"
            identity.AddClaim(new Claim($"TenantId:{admin.TenantId}", admin.Role));

            // Grant scoped admin permissions if they're a DepartmentAdmin
            if (admin.Role == "DepartmentAdmin" || admin.Role == "SuperAdmin")
            {
                foreach (var perm in Permissions.ScopedAdminPermissions)
                {
                    // Only add if not already present (avoid duplicates with Admin.Full)
                    if (!identity.HasClaim(Permissions.ClaimType, perm))
                    {
                        identity.AddClaim(new Claim(Permissions.ClaimType, perm));
                    }
                }
            }
        }
    }

    private static async Task TryAutoAssignFromGroupsAsync(
        ClaimsPrincipal user,
        ClaimsIdentity identity,
        string azureAdObjectId,
        AppDbContext db,
        ILogger logger)
    {
        // Get user's Azure AD group membership claims
        var userGroupIds = user.FindAll("groups")
            .Select(c => c.Value)
            .Concat(user.FindAll("groups:id")
                .Select(c => c.Value))
            .Distinct()
            .ToHashSet();

        if (userGroupIds.Count == 0)
            return;

        // Find tenants whose AzureAdGroupId matches one of the user's groups.
        //
        // Blank is not a mapping. Testing only for null let a tenant carrying an empty
        // AzureAdGroupId be matched by any equally empty group claim, which would hand out
        // CodeCall.Write — the right to fire a live code call — on an accident of data entry.
        var matchingTenants = (await db.Tenants
                .Where(t => t.IsActive && t.AzureAdGroupId != null && t.AzureAdGroupId != "")
                .Where(t => userGroupIds.Contains(t.AzureAdGroupId!))
                .ToListAsync())
            // Whitespace-only cannot be excluded in SQL portably, so it is filtered here.
            .Where(t => !string.IsNullOrWhiteSpace(t.AzureAdGroupId))
            .ToList();

        foreach (var tenant in matchingTenants)
        {
            // Check if already assigned
            var existing = await db.TenantAdmins
                .AnyAsync(a => a.TenantId == tenant.Id && a.AzureAdObjectId == azureAdObjectId);

            if (!existing)
            {
                db.TenantAdmins.Add(new Models.TenantAdmin
                {
                    TenantId = tenant.Id,
                    AzureAdObjectId = azureAdObjectId,
                    Role = "DepartmentAdmin",
                    IsAutoAssigned = true,
                    CreatedAt = DateTime.UtcNow,
                    LastSyncedAt = DateTime.UtcNow,
                });
                await db.SaveChangesAsync();

                // This confers CodeCall.Write — the right to fire a live code call — without
                // anyone approving this individual. The group mapping is the approval, so the
                // grant must at least be visible after the fact.
                logger.LogInformation(
                    "Auto-assigned principal {ObjectId} as DepartmentAdmin of tenant {TenantId} "
                    + "via Entra group {GroupId}; this grants CodeCall.Write",
                    azureAdObjectId, tenant.Id, tenant.AzureAdGroupId);
            }

            // Apply the claims now rather than on the next request. AddTenantClaimsAsync
            // already ran above, so a user assigned by this method would otherwise be
            // admitted only on their second request — access that appears to work
            // intermittently is worse than access that doesn't.
            if (!identity.Claims.Any(c => c.Type == $"TenantId:{tenant.Id}"))
            {
                identity.AddClaim(new Claim($"TenantId:{tenant.Id}", "DepartmentAdmin"));
            }

            // AutoAssignedPermissions, not ScopedAdminPermissions: this path grants access
            // because IT added someone to a directory group, with nobody reviewing the
            // individual. It therefore withholds CodeCall.Write — the right to page on-call
            // clinicians for a real emergency — which stays available through an explicit
            // grant. An admin who genuinely needs it can still hand it out.
            foreach (var perm in Permissions.AutoAssignedPermissions)
            {
                if (!identity.HasClaim(Permissions.ClaimType, perm))
                {
                    identity.AddClaim(new Claim(Permissions.ClaimType, perm));
                }
            }
        }
    }


    private static string? GetTenantId(ClaimsPrincipal user) => PrincipalClaims.GetTenantId(user);

    /// <summary>
    /// Whether the authenticated user matches a configured super administrator
    /// (by email or Entra object ID).
    /// </summary>
    private bool IsConfiguredSuperAdmin(ClaimsPrincipal user)
    {
        // Matching on email is intentional and applies to every provider, including local
        // accounts — an operator may deliberately configure a local break-glass super admin.
        //
        // The escalation risk this invites (someone creating a local account bearing a
        // configured super admin's address) is closed at the source instead:
        // LocalAccountService.RegisterAsync refuses to register a reserved address, and
        // UpdateAsync exposes no way to change an existing account's email. Restricting the
        // match here as well was tried and rejected — it forbids the legitimate break-glass
        // configuration without closing anything the registration guard leaves open.
        var email = GetEmail(user);
        if (!string.IsNullOrEmpty(email) &&
            _superAdmins.Emails.Contains(email, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        var oid = GetAzureAdObjectId(user);
        if (!string.IsNullOrEmpty(oid) &&
            _superAdmins.ObjectIds.Contains(oid, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Grants a configured super administrator every role, every permission, and
    /// SuperAdmin status on every active tenant.
    /// </summary>
    private static async Task GrantSuperAdminAsync(ClaimsIdentity identity, AppDbContext db)
    {
        foreach (var role in Permissions.SuperAdminRoles)
        {
            if (!identity.HasClaim(ClaimTypes.Role, role))
                identity.AddClaim(new Claim(ClaimTypes.Role, role));
        }

        foreach (var perm in Permissions.SuperAdminPermissions)
        {
            if (!identity.HasClaim(Permissions.ClaimType, perm))
                identity.AddClaim(new Claim(Permissions.ClaimType, perm));
        }

        // Super admin of every active tenant, so /api/auth/me exposes the full list.
        if (!identity.Claims.Any(c => c.Type.StartsWith("TenantId:")))
        {
            try
            {
                var tenantIds = await db.Tenants.Where(t => t.IsActive).Select(t => t.Id).ToListAsync();
                foreach (var id in tenantIds)
                    identity.AddClaim(new Claim($"TenantId:{id}", "SuperAdmin"));
            }
            catch
            {
                // Tenants table missing / DB not ready — roles+permissions above still grant access.
            }
        }
    }

    /// <summary>
    /// Expands explicit per-user <see cref="PermissionGrant"/> rows into
    /// <c>Permission</c> claims (and a tenant claim when the grant is tenant-scoped).
    /// Match is by Entra object id OR email for external principals; local accounts
    /// match by the same email they sign in with.
    /// </summary>
    private static async Task AddPermissionGrantsAsync(ClaimsIdentity identity, ClaimsPrincipal user, AppDbContext db)
    {
        var oid = GetAzureAdObjectId(user);
        var email = GetEmail(user);

        if (string.IsNullOrEmpty(oid) && string.IsNullOrEmpty(email))
            return;

        // Object ids and emails are matched against separate shapes of grant, so a grant of
        // one kind can never be satisfied by a claim of the other. Without the "@" rule a
        // grant keyed to an object id could be met by an email claim carrying that same text,
        // and vice versa.
        //
        // Email matching is deliberate and stays: it is what lets an administrator provision
        // someone before their first sign-in, which is the documented onboarding flow. The
        // consequence to be aware of is that it is provider-agnostic — a grant issued for an
        // address is honoured whether the holder later arrives via Entra, Google or a local
        // account, because all three present the same verified address.
        var grants = await db.PermissionGrants
            .Where(g => g.IsActive &&
                ((oid != null && !g.ExternalPrincipalId.Contains("@") && g.ExternalPrincipalId == oid) ||
                 (email != null && g.ExternalPrincipalId.Contains("@") && g.ExternalPrincipalId == email)))
            .ToListAsync();

        foreach (var grant in grants)
        {
            if (grant.TenantId.HasValue &&
                !identity.Claims.Any(c => c.Type == $"TenantId:{grant.TenantId.Value}"))
            {
                identity.AddClaim(new Claim($"TenantId:{grant.TenantId.Value}", "PermissionGrant"));
            }

            foreach (var perm in Permissions.ParsePermissionCsv(grant.Permissions))
            {
                if (!identity.HasClaim(Permissions.ClaimType, perm))
                {
                    identity.AddClaim(new Claim(Permissions.ClaimType, perm));
                }
            }
        }
    }

    /// <summary>
    /// Queues a sign-in observation for the identity directory. Best-effort by design: a
    /// failure here must never affect the request, since this data is for discoverability
    /// only and confers no access.
    /// </summary>
    private static void RecordSignIn(ClaimsPrincipal user, IIdentityDirectoryService identities)
    {
        try
        {
            var objectId = GetAzureAdObjectId(user);
            if (string.IsNullOrEmpty(objectId)) return;

            identities.Observe(new SignInObservation(
                Provider: user.FindFirst("auth_provider")?.Value ?? "microsoft",
                ExternalObjectId: objectId,
                Email: GetEmail(user),
                DisplayName: user.FindFirst(ClaimTypes.Name)?.Value
                             ?? user.FindFirst("name")?.Value,
                TenantIdClaim: GetTenantId(user),
                SeenAt: DateTime.UtcNow));
        }
        catch
        {
            // Intentionally swallowed — see summary.
        }
    }

    private static string? GetEmail(ClaimsPrincipal user) => PrincipalClaims.GetEmail(user);

    private static string? GetAzureAdObjectId(ClaimsPrincipal user) => PrincipalClaims.GetObjectId(user);
}
