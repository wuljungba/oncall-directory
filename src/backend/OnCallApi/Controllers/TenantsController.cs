using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OnCallApi.Authorization;
using OnCallApi.Configuration;
using OnCallApi.Data;
using OnCallApi.Models;
using OnCallApi.Services;

namespace OnCallApi.Controllers;

/// <summary>
/// Reading the tenant list requires only admin standing, because a sub-admin needs the
/// NAME of the subscription they administer -- without it the admin UI renders every
/// reference to it as "Tenant 4". The list is filtered to the caller's own tenants, so
/// this exposes no other customer. Creating, editing and deactivating still require
/// Tenant.Manage, declared per action below.
/// </summary>
[ApiController]
[Route("api/tenants")]
[Authorize(Policy = "RequireAdminFullOrScoped")]
public class TenantsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ITenantContextService _tenants;
    private readonly IOptions<GraphApiOptions> _graphOptions;
    private readonly IConfiguration _config;
    private readonly ILogger<TenantsController> _logger;

    public TenantsController(
        AppDbContext db,
        ITenantContextService tenants,
        IOptions<GraphApiOptions> graphOptions,
        IConfiguration config,
        ILogger<TenantsController> logger)
    {
        _db = db;
        _tenants = tenants;
        _graphOptions = graphOptions;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Lists the tenants the caller may see: every one for a super admin, and only their
    /// own for anyone else. Returning the whole table to a sub-admin would disclose the
    /// name and contact of every other customer on the deployment.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<Tenant>>> GetAll([FromQuery] bool includeInactive = false)
    {
        var query = _db.Tenants.AsQueryable();
        if (!includeInactive)
            query = query.Where(t => t.IsActive);

        if (!_tenants.IsSuperAdmin(User))
        {
            var allowed = await _tenants.GetAuthorizedTenantIdsAsync(User);
            query = query.Where(t => allowed.Contains(t.Id));
        }

        return await query.OrderBy(t => t.Name).ToListAsync();
    }

    /// <summary>Get a single tenant by ID.</summary>
    [HttpGet("{id}")]
    public async Task<ActionResult<Tenant>> Get(int id)
    {
        var tenant = await _db.Tenants.FindAsync(id);
        if (tenant == null) return NotFound();

        // NotFound rather than Forbid: whether a given tenant exists is itself something
        // one customer should not learn about another.
        if (!_tenants.IsSuperAdmin(User))
        {
            var allowed = await _tenants.GetAuthorizedTenantIdsAsync(User);
            if (!allowed.Contains(id)) return NotFound();
        }

        return tenant;
    }

    /// <summary>Create a new tenant (business/facility).</summary>
    [Authorize(Policy = "RequireTenantManage")]
    [HttpPost]
    public async Task<ActionResult<Tenant>> Create([FromBody] CreateTenantRequest request)
    {
        // Trimmed and compared case-insensitively: "Acme" and "acme " both succeeded and
        // then looked identical in the admin UI, which is a support call waiting to happen.
        var name = request.Name.Trim();
        var existing = await _db.Tenants.AnyAsync(t => t.Name.ToLower() == name.ToLower());
        if (existing)
            return Conflict(new { error = "A tenant with this name already exists." });

        var tenant = new Tenant
        {
            Name = name,
            Description = request.Description,
            AzureAdGroupId = request.AzureAdGroupId,
            AzureAdTenantId = NormalizeDirectoryId(request.AzureAdTenantId),
            ContactEmail = request.ContactEmail,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        };

        _db.Tenants.Add(tenant);
        await _db.SaveChangesAsync();
        _logger.LogInformation("Tenant created: {TenantId} {Name}", tenant.Id, tenant.Name);

        return CreatedAtAction(nameof(Get), new { id = tenant.Id }, tenant);
    }

    /// <summary>Update a tenant.</summary>
    [Authorize(Policy = "RequireTenantManage")]
    [HttpPut("{id}")]
    public async Task<ActionResult<Tenant>> Update(int id, [FromBody] UpdateTenantRequest request)
    {
        var tenant = await _db.Tenants.FindAsync(id);
        if (tenant == null) return NotFound();

        if (request.Name != null) tenant.Name = request.Name;
        if (request.Description != null) tenant.Description = request.Description;
        if (request.AzureAdGroupId != null) tenant.AzureAdGroupId = request.AzureAdGroupId;
        // Blank clears the connection, which is how an administrator withdraws it. Because
        // nothing is stored per user, clearing it revokes everyone's read access at once.
        if (request.AzureAdTenantId != null)
            tenant.AzureAdTenantId = NormalizeDirectoryId(request.AzureAdTenantId);
        if (request.ContactEmail != null) tenant.ContactEmail = request.ContactEmail;
        if (request.IsActive.HasValue) tenant.IsActive = request.IsActive.Value;

        await _db.SaveChangesAsync();
        _logger.LogInformation("Tenant updated: {TenantId}", id);

        return Ok(tenant);
    }

    /// <summary>Deactivate a tenant (soft delete).</summary>
    [Authorize(Policy = "RequireTenantManage")]
    [HttpDelete("{id}")]
    public async Task<ActionResult> Deactivate(int id)
    {
        var tenant = await _db.Tenants.FindAsync(id);
        if (tenant == null) return NotFound();

        tenant.IsActive = false;
        await _db.SaveChangesAsync();
        _logger.LogInformation("Tenant deactivated: {TenantId}", id);

        return NoContent();
    }

    /// <summary>
    /// Stores a connected directory id in one canonical form. Entra reports `tid` in
    /// lowercase; a GUID pasted from the portal in another case would be compared as text
    /// and silently never match.
    /// </summary>
    private static string? NormalizeDirectoryId(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.ToLowerInvariant();
    
}

    /// <summary>
    /// The admin-consent links to send a connected organization.
    ///
    /// Consent has to be granted by an administrator in THEIR directory, and there are two
    /// app registrations to grant it to:
    /// <list type="bullet">
    /// <item><b>OnCall API</b> — the app their staff sign in to. A directory that does not let
    /// users consent to apps (the norm at hospitals) stops every one of them at "Need admin
    /// approval" until this is granted.</item>
    /// <item><b>OnCall Graph</b> — creates the service principal this app uses to read their
    /// users.</item>
    /// </list>
    /// This used to hand over only the second, which connected a directory whose people still
    /// could not get in. There is nothing we can do from here to bring either about, so the
    /// useful thing is to hand the operator both exact links and the redirect URI they share.
    /// </summary>
    [Authorize(Policy = "RequireTenantManage")]
    [HttpGet("{id}/directory-consent-link")]
    public async Task<ActionResult> GetDirectoryConsentLink(int id)
    {
        var tenant = await _db.Tenants.FindAsync(id);
        if (tenant == null) return NotFound();

        if (string.IsNullOrWhiteSpace(tenant.AzureAdTenantId))
        {
            return BadRequest(new
            {
                error = "Set this subscription's Directory Tenant ID before requesting consent.",
            });
        }

        var signInClientId = _config["AzureAd:ClientId"];
        if (!IsConfiguredClientId(signInClientId))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                error = "AzureAd:ClientId is not configured, so no sign-in consent link can be built.",
            });
        }

        var directoryClientId = _graphOptions.Value.ClientId;
        if (!IsConfiguredClientId(directoryClientId))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                error = "GraphApi:ClientId is not configured, so no directory consent link can be built.",
            });
        }

        var origin = (_config["Cors:Origin"] ?? "").TrimEnd('/');
        var redirectUri = $"{origin}/admin";

        return Ok(new
        {
            directoryTenantId = tenant.AzureAdTenantId,
            redirectUri,
            signInConsentUrl = AdminConsentUrl(tenant.AzureAdTenantId, signInClientId!, redirectUri),
            directoryConsentUrl = AdminConsentUrl(tenant.AzureAdTenantId, directoryClientId, redirectUri),
            // Stated rather than assumed: consent fails on the final redirect unless this
            // exact URI is registered on each app registration.
            note = "This redirect URI must be registered on both the OnCall API and OnCall Graph app registrations, or consent will fail at the last step.",
        });
    }

    /// <summary>
    /// A one-time invitation to connect a directory to this subscription.
    ///
    /// The alternative it replaces is an operator typing the customer's Entra tenant GUID into a
    /// form. That fails silently: a wrong id matches nobody's token, so the subscription reads as
    /// connected and their staff simply cannot sign in.
    ///
    /// These links name no directory at all — <c>/organizations</c> means "whichever directory
    /// the admin signs in to" — and carry the invite token as OAuth <c>state</c>. When consent
    /// comes back, the state says which subscription was being onboarded and a live Graph read
    /// says the consent was real. The id is then filled in by the app, not by hand.
    /// </summary>
    [Authorize(Policy = "RequireTenantManage")]
    [HttpPost("{id}/onboarding-invite")]
    public async Task<ActionResult> CreateOnboardingInvite(int id, CancellationToken ct)
    {
        var tenant = await _db.Tenants.FindAsync([id], ct);
        if (tenant == null) return NotFound();

        var signInClientId = _config["AzureAd:ClientId"];
        if (!IsConfiguredClientId(signInClientId))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                error = "AzureAd:ClientId is not configured, so no sign-in consent link can be built.",
            });
        }

        var directoryClientId = _graphOptions.Value.ClientId;
        if (!IsConfiguredClientId(directoryClientId))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                error = "GraphApi:ClientId is not configured, so no directory consent link can be built.",
            });
        }

        var origin = (_config["Cors:Origin"] ?? "").TrimEnd('/');
        var redirectUri = $"{origin}/admin";

        var invite = new TenantOnboardingInvite
        {
            TenantId = tenant.Id,
            CreatedBy = PrincipalClaims.GetObjectId(User) ?? PrincipalClaims.GetEmail(User),
        };

        _db.TenantOnboardingInvites.Add(invite);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Onboarding invite issued for tenant {TenantId} by {CreatedBy}", tenant.Id, invite.CreatedBy);

        return Ok(new
        {
            expiresAt = invite.ExpiresAt,
            signInConsentUrl = InviteConsentUrl(signInClientId!, redirectUri, invite.Token),
            directoryConsentUrl = InviteConsentUrl(directoryClientId, redirectUri, invite.Token),
            note = "Single use, and it expires. Both links must be opened by an administrator of "
                + "the customer's directory: the first lets their staff sign in, the second lets "
                + "OnCall read their directory.",
        });
    }

    /// <summary>
    /// Whether this subscription's directory is genuinely connected, asked live rather than
    /// inferred.
    ///
    /// "Directory connected" in the admin list has only ever meant that somebody typed a GUID.
    /// This asks Graph whether the directory can actually be read, and pairs it with what the
    /// last sync did — the two questions an operator is really asking.
    /// </summary>
    [HttpGet("{id}/directory-status")]
    public async Task<ActionResult> GetDirectoryStatus(
        int id, [FromServices] IGraphApiService graph, CancellationToken ct)
    {
        var tenant = await _db.Tenants.FindAsync([id], ct);
        if (tenant == null) return NotFound();

        // NotFound rather than Forbid, as everywhere else here: whether a given subscription
        // exists is itself something one customer should not learn about another.
        if (!_tenants.IsSuperAdmin(User))
        {
            var allowed = await _tenants.GetAuthorizedTenantIdsAsync(User);
            if (!allowed.Contains(id)) return NotFound();
        }

        var lastRun = await _db.SyncRuns.AsNoTracking()
            .Where(r => r.TenantId == id && r.Source == SyncSources.AdUsers)
            .OrderByDescending(r => r.StartedAt)
            .FirstOrDefaultAsync(ct);

        var staffCount = await _db.Employees.CountAsync(e => e.TenantId == id && e.IsActive, ct);

        if (string.IsNullOrWhiteSpace(tenant.AzureAdTenantId))
        {
            return Ok(new
            {
                directoryTenantId = (string?)null,
                consentGranted = false,
                canReadDirectory = false,
                needsReconsent = false,
                detail = "No directory is connected yet. Send this subscription an onboarding invite.",
                lastSyncAt = lastRun?.StartedAt,
                lastOutcome = lastRun?.Outcome,
                staffCount,
            });
        }

        var probe = await graph.ProbeDirectoryAsync(tenant.AzureAdTenantId, ct);

        return Ok(new
        {
            directoryTenantId = tenant.AzureAdTenantId,
            directoryDisplayName = tenant.DirectoryDisplayName,
            directoryVerifiedAt = tenant.DirectoryVerifiedAt,
            consentGranted = probe.CanRead || !probe.NeedsConsent,
            canReadDirectory = probe.CanRead,
            needsReconsent = probe.NeedsConsent,
            detail = probe.CanRead ? null : probe.FailureDetail,
            lastSyncAt = lastRun?.StartedAt,
            lastOutcome = lastRun?.Outcome,
            lastPagesRead = lastRun?.PagesRead,
            lastDeactivationsRefused = lastRun?.DeactivationsRefused,
            staffCount,
        });
    }

    private static string InviteConsentUrl(string clientId, string redirectUri, Guid state) =>
        // "organizations" rather than a tenant id: we do not know which directory yet, and that
        // is the point — the admin's own directory is whichever they sign in to.
        "https://login.microsoftonline.com/organizations/adminconsent"
        + $"?client_id={Uri.EscapeDataString(clientId)}"
        + $"&redirect_uri={Uri.EscapeDataString(redirectUri)}"
        + $"&state={state:N}";

    private static bool IsConfiguredClientId(string? clientId) =>
        !string.IsNullOrWhiteSpace(clientId)
        && !clientId.Contains("your-", StringComparison.OrdinalIgnoreCase);

    private static string AdminConsentUrl(string directoryTenantId, string clientId, string redirectUri) =>
        $"https://login.microsoftonline.com/{Uri.EscapeDataString(directoryTenantId)}/adminconsent"
        + $"?client_id={Uri.EscapeDataString(clientId)}"
        + $"&redirect_uri={Uri.EscapeDataString(redirectUri)}";
}
