using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
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
    /// The admin-consent link to send a connected organization.
    ///
    /// Consent has to be granted by an administrator in THEIR directory — that is what
    /// creates the service principal this app uses to read their users. There is nothing
    /// we can do from here to bring it about, so the useful thing is to hand the operator
    /// the exact link and the exact redirect URI it depends on.
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

        var clientId = _graphOptions.Value.ClientId;
        if (string.IsNullOrWhiteSpace(clientId) || clientId.Contains("your-", StringComparison.OrdinalIgnoreCase))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                error = "GraphApi:ClientId is not configured, so no consent link can be built.",
            });
        }

        var origin = (_config["Cors:Origin"] ?? "").TrimEnd('/');
        var redirectUri = $"{origin}/admin";

        var url =
            $"https://login.microsoftonline.com/{Uri.EscapeDataString(tenant.AzureAdTenantId)}/adminconsent"
            + $"?client_id={Uri.EscapeDataString(clientId)}"
            + $"&redirect_uri={Uri.EscapeDataString(redirectUri)}";

        return Ok(new
        {
            url,
            redirectUri,
            directoryTenantId = tenant.AzureAdTenantId,
            // Stated rather than assumed: consent fails on the final redirect unless this
            // exact URI is registered on the app registration.
            note = "This redirect URI must be registered on the OnCall Graph app registration, or consent will fail at the last step.",
        });
    }
}
