using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using OnCallApi.Data;
using OnCallApi.Models;
using OnCallApi.Services;

namespace OnCallApi.Controllers;

/// <summary>
/// Where a customer's Entra administrator ends up after granting consent.
///
/// Anonymous by necessity: the person completing this has no OnCall account and never will —
/// they are their organisation's directory administrator, not one of its clinicians. What makes
/// that safe is that the request proves two separate things before anything is written:
///
/// <list type="bullet">
/// <item>the <c>state</c> matches an unconsumed, unexpired invite we issued, which says WHICH
/// subscription is being connected;</item>
/// <item>an app-only Graph read against the returned directory succeeds, which says the consent
/// was real. A tenant id on a redirect is attacker-supplied and proves nothing by itself.</item>
/// </list>
///
/// Neither half is sufficient alone. Someone holding a stolen invite link could point it at a
/// directory they control — but only by granting our application consent inside it, which only
/// that directory's own administrator can do, and it would connect them to a subscription they
/// had already been invited to.
/// </summary>
[ApiController]
[Route("api/public/consent")]
[AllowAnonymous]
public class ConsentCallbackController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IGraphApiService _graph;
    private readonly ILogger<ConsentCallbackController> _logger;

    public ConsentCallbackController(
        AppDbContext db, IGraphApiService graph, ILogger<ConsentCallbackController> logger)
    {
        _db = db;
        _graph = graph;
        _logger = logger;
    }

    [HttpPost("complete")]
    [EnableRateLimiting("ConsentCallback")]
    public async Task<ActionResult> Complete([FromBody] ConsentCallbackRequest request, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        if (!Guid.TryParse(request.State, out var token))
        {
            return Ok(NotRecognised());
        }

        var invite = await _db.TenantOnboardingInvites
            .Include(i => i.Tenant)
            .FirstOrDefaultAsync(i => i.Token == token, ct);

        // One answer for "no such invite", "already used" and "expired". Distinct messages would
        // let anyone with this endpoint test which invite tokens exist.
        if (invite == null || !invite.IsUsable(now))
        {
            return Ok(NotRecognised());
        }

        if (!string.IsNullOrWhiteSpace(request.Error))
        {
            invite.LastError = Truncate($"{request.Error}: {request.ErrorDescription}");
            await _db.SaveChangesAsync(ct);

            return Ok(new
            {
                connected = false,
                message = "Microsoft reported that consent was not granted, so nothing has changed. "
                    + "You can open the link again.",
            });
        }

        var directoryId = Normalize(request.Tenant);
        if (directoryId == null)
        {
            invite.LastError = "The consent redirect carried no directory id.";
            await _db.SaveChangesAsync(ct);

            return Ok(new
            {
                connected = false,
                message = "That consent did not name a directory. Please open the link again.",
            });
        }

        // The proof. Everything above this line is a claim made by a redirect.
        var probe = await _graph.ProbeDirectoryAsync(directoryId, ct);
        if (!probe.CanRead)
        {
            invite.LastError = Truncate(probe.FailureDetail);
            await _db.SaveChangesAsync(ct);

            _logger.LogWarning(
                "Consent callback for tenant {TenantId} could not read directory {DirectoryId}: {Detail}",
                invite.TenantId, directoryId, probe.FailureDetail);

            return Ok(new
            {
                connected = false,
                message = probe.NeedsConsent
                    ? "Microsoft recorded your consent, but OnCall still cannot read your directory. "
                      + "The second link — the one granting directory access — may not have been opened yet."
                    : "Your directory could not be reached just now. Nothing has changed; please try again shortly.",
            });
        }

        // One directory, one subscription. Two subscriptions sharing a tenant id would each grant
        // that directory's people read access, and the allow-list has no way to tell which was
        // meant.
        var claimedElsewhere = await _db.Tenants.AnyAsync(
            t => t.Id != invite.TenantId && t.AzureAdTenantId == directoryId, ct);

        if (claimedElsewhere)
        {
            invite.LastError = "That directory is already connected to another subscription.";
            await _db.SaveChangesAsync(ct);

            _logger.LogWarning(
                "Consent callback refused: directory {DirectoryId} is already connected elsewhere",
                directoryId);

            return Ok(new
            {
                connected = false,
                message = "That directory is already connected to another subscription. "
                    + "Ask your contact at OnCall to sort that out before trying again.",
            });
        }

        var tenant = invite.Tenant;

        // This subscription already has a directory, and it is not this one.
        //
        // Overwriting would withdraw access from everyone in the old directory — every clinician
        // who signs in from it — silently, as a side effect of somebody opening a link. Whoever
        // issued the invite may well have meant a new customer and picked the wrong subscription.
        //
        // Re-pointing a live subscription at a different directory is a deliberate act with a
        // blast radius, so it stays one: clear the directory on the subscription first, then
        // invite. Consenting again for the SAME directory is not re-pointing and is allowed —
        // that is how a re-consent refreshes what we know about it.
        if (!string.IsNullOrWhiteSpace(tenant.AzureAdTenantId) && tenant.AzureAdTenantId != directoryId)
        {
            invite.LastError = "That subscription is already connected to a different directory.";
            await _db.SaveChangesAsync(ct);

            _logger.LogWarning(
                "Consent callback refused: tenant {TenantId} is already connected to another directory; "
                + "invite {InviteId} pointed at {DirectoryId}",
                tenant.Id, invite.Id, directoryId);

            return Ok(new
            {
                connected = false,
                message = "That subscription is already connected to a different directory, so nothing "
                    + "has changed. Ask your contact at OnCall to disconnect the old one first.",
            });
        }

        tenant.AzureAdTenantId = directoryId;
        tenant.DirectoryVerifiedAt = now;

        // Best effort, and deliberately not fatal: this needs Organization.Read.All, which a
        // directory that consented before that permission was added has not granted.
        var organization = await _graph.GetOrganizationAsync(directoryId, ct);
        if (!string.IsNullOrWhiteSpace(organization.DisplayName))
        {
            tenant.DirectoryDisplayName = organization.DisplayName;
        }
        if (organization.VerifiedDomains.Count > 0)
        {
            tenant.DirectoryDomains = JsonSerializer.Serialize(organization.VerifiedDomains);
        }

        invite.ConsumedAt = now;
        invite.ConsumedDirectoryId = directoryId;
        invite.LastError = null;

        // Written in band with the change: connecting a directory grants read access to everyone
        // in it, which is exactly the kind of thing an audit trail exists to record.
        _db.AuditLogs.Add(new AuditLog
        {
            UserId = Guid.Empty,
            PrincipalId = "system:consent-callback",
            UserName = "Directory consent",
            Action = "Updated",
            ResourceType = "Tenant",
            ResourceId = tenant.Id.ToString(),
            TenantId = tenant.Id,
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            Details = $"DirectoryConnected={directoryId};InviteId={invite.Id};IssuedBy={invite.CreatedBy}",
            Timestamp = now,
        });

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Directory {DirectoryId} connected to tenant {TenantId} through invite {InviteId}",
            directoryId, tenant.Id, invite.Id);

        return Ok(new
        {
            connected = true,
            subscriptionName = tenant.Name,
            directoryName = tenant.DirectoryDisplayName,
            // True when the directory is readable but its organisation details are not, which
            // means one more consent is needed for the newer permission.
            needsReconsent = organization.NeedsReconsent,
            message = "Your directory is connected. Your staff can sign in to OnCall now.",
        });
    }

    private static object NotRecognised() => new
    {
        connected = false,
        message = "That invitation link is no longer valid. Ask your contact at OnCall for a new one.",
    };

    /// <summary>Entra reports tid in lowercase; a GUID pasted in another case would never match.</summary>
    private static string? Normalize(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.ToLowerInvariant();
    }

    private static string? Truncate(string? value) =>
        string.IsNullOrEmpty(value) || value.Length <= 1000 ? value : value[..1000];
}

/// <summary>
/// What the browser hands back from Microsoft's consent redirect. Every field is untrusted input:
/// the state is checked against an invite, and the tenant id against a live Graph read.
/// </summary>
public record ConsentCallbackRequest(
    string? State,
    string? Tenant,
    string? Error,
    string? ErrorDescription);
