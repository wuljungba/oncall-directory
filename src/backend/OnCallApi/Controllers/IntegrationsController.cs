using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OnCallApi.Authorization;
using OnCallApi.Data;
using OnCallApi.Models;
using OnCallApi.Services;

namespace OnCallApi.Controllers;

[ApiController]
[Route("api/integrations")]
[Authorize(Policy = "RequireScheduleRead")]
public class IntegrationsController : ControllerBase
{
    private readonly IGraphApiService _graphApi;
    private readonly ILogger<IntegrationsController> _logger;

    public IntegrationsController(IGraphApiService graphApi, ILogger<IntegrationsController> logger)
    {
        _graphApi = graphApi;
        _logger = logger;
    }

    /// <summary>
    /// Trigger an immediate AD sync.
    ///
    /// This used to call a Graph read and return its count, writing nothing — so it
    /// reported "synced: 3" while the directory stayed exactly as it was. It now runs the
    /// same sync the timer does, and reports what was actually written, including any
    /// users that could not be stored and why.
    /// </summary>
    [HttpPost("sync/ad")]
    [Authorize(Policy = "RequireAdminFull")]
    public async Task<ActionResult> SyncActiveDirectory(
        [FromServices] IAdDirectorySyncService sync,
        CancellationToken ct,
        [FromQuery] bool full = true)
    {
        // Full sync rather than delta: someone pressing this wants the directory reconciled
        // now, not the increment since the last scheduled run. That was already the promise in
        // this comment, and was not true — the stored cursor was passed straight through. Pass
        // ?full=false for a plain incremental run. Every connected directory is covered, so the
        // button means the same thing whether one customer is connected or ten.
        // Attributable: a human-initiated run that retires half a directory should name who
        // started it, not "system".
        var triggeredBy = PrincipalClaims.GetObjectId(User) ?? PrincipalClaims.GetEmail(User) ?? "unknown";
        var results = await sync.SyncAllAsync(full, triggeredBy, ct);

        return Ok(new
        {
            fetched = results.Sum(r => r.Fetched),
            created = results.Sum(r => r.Created),
            updated = results.Sum(r => r.Updated),
            deactivated = results.Sum(r => r.Deactivated),
            deactivationsRefused = results.Sum(r => r.DeactivationsRefused),
            failedDirectories = results.Count(r => !r.Succeeded),
            skipped = results.SelectMany(r => r.Skipped).ToList(),
            // Reported per tenant as well as in total: "0 created" across ten directories
            // hides which one of them actually failed.
            tenants = results.Select(r => new
            {
                tenantId = r.TenantId,
                tenantName = r.TenantName,
                succeeded = r.Succeeded,
                fetched = r.Fetched,
                created = r.Created,
                updated = r.Updated,
                deactivated = r.Deactivated,
                // The three that say whether this run can be believed: how much of the
                // directory was read, whether it was a full picture, and whether a
                // deactivation batch was refused as implausible.
                pagesRead = r.PagesRead,
                mode = r.WasFullEnumeration ? "full" : "incremental",
                deactivationsRefused = r.DeactivationsRefused,
                needsAttention = r.NeedsAttention,
            }),
        });
    }

    /// <summary>
    /// The recent directory sync runs: what was read, what changed, and whether it can be believed.
    ///
    /// Nothing recorded a sync cycle before this, which is how a bug that read one page of a
    /// paginated directory and retired everyone on the rest ran for months leaving only a log
    /// line. <c>pagesRead</c> is its fingerprint — one page for a large directory — and
    /// <c>deactivationsRefused</c> is the safety valve saying it caught something.
    /// </summary>
    [HttpGet("sync/ad/runs")]
    [Authorize(Policy = "RequireAdminFullOrScoped")]
    public async Task<ActionResult> GetSyncRuns(
        [FromServices] AppDbContext db,
        [FromServices] ITenantContextService tenants,
        CancellationToken ct,
        [FromQuery] int? tenantId = null,
        [FromQuery] string source = SyncSources.AdUsers,
        [FromQuery] int take = 50)
    {
        var query = db.SyncRuns.AsNoTracking().Where(r => r.Source == source);

        // A scoped admin sees their own subscriptions and nothing else. Runs for the home
        // directory (TenantId null) belong to super admins — they describe every customer's
        // directory at once.
        if (!tenants.IsSuperAdmin(User))
        {
            var allowed = await tenants.GetAuthorizedTenantIdsAsync(User);
            query = query.Where(r => r.TenantId != null && allowed.Contains(r.TenantId.Value));
        }

        if (tenantId.HasValue) query = query.Where(r => r.TenantId == tenantId.Value);

        var runs = await query
            .OrderByDescending(r => r.StartedAt)
            .Take(Math.Clamp(take, 1, 200))
            .Select(r => new
            {
                id = r.Id,
                tenantId = r.TenantId,
                source = r.Source,
                mode = r.Mode,
                outcome = r.Outcome,
                startedAt = r.StartedAt,
                completedAt = r.CompletedAt,
                pagesRead = r.PagesRead,
                fetched = r.Fetched,
                created = r.Created,
                updated = r.Updated,
                skipped = r.Skipped,
                deactivated = r.Deactivated,
                deactivatedByRemoval = r.DeactivatedByRemoval,
                deactivatedByDisabledAccount = r.DeactivatedByDisabledAccount,
                deactivationsRefused = r.DeactivationsRefused,
                deltaLinkStored = r.DeltaLinkStored,
                tokenWasRejected = r.TokenWasRejected,
                triggeredBy = r.TriggeredBy,
                failureDetail = r.FailureDetail,
                notes = r.Notes,
                // Deliberately absent: the delta link itself. It is a Graph resource URL, and
                // this endpoint is open to scoped admins.
            })
            .ToListAsync(ct);

        return Ok(runs);
    }

    /// <summary>Send a test Teams notification to a user.</summary>
    [HttpPost("notify/teams")]
    [Authorize(Policy = "RequireScheduleWrite")]
    public async Task<ActionResult> SendTeamsNotification([FromBody] TeamsNotificationRequest request)
    {
        await _graphApi.SendTeamsNotificationAsync(request.UserId, request.Title, request.Message);
        _logger.LogInformation("Teams notification sent to {UserId}: {Title}", request.UserId, request.Title);
        return Ok(new { sent = true });
    }

    /// <summary>Push an on-call shift to Outlook calendar.</summary>
    [HttpPost("calendar/push")]
    [Authorize(Policy = "RequireScheduleWrite")]
    public async Task<ActionResult> PushToCalendar([FromBody] CalendarPushRequest request)
    {
        await _graphApi.CreateOutlookCalendarEventAsync(
            request.UserId, request.Subject, request.StartTime, request.EndTime);
        return Ok(new { pushed = true });
    }

    /// <summary>Get presence for a user from Teams.</summary>
    [HttpGet("presence/{userId}")]
    public async Task<ActionResult> GetPresence(string userId)
    {
        var presence = await _graphApi.GetUserPresenceAsync(userId);
        return Ok(new { userId, presence });
    }
}

public record TeamsNotificationRequest(string UserId, string Title, string Message);
public record CalendarPushRequest(string UserId, string Subject, DateTime StartTime, DateTime EndTime);
