using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using OnCallApi.Hubs;
using OnCallApi.Models;
using OnCallApi.Services;
using OnCallApi.Services.Dispatch;

namespace OnCallApi.Controllers;

[ApiController]
[Route("api/phone-trees")]
[Authorize(Policy = "RequireDirectoryRead")]
public class PhoneTreeEventsController : ControllerBase
{
    private readonly IPhoneTreeEventService _service;
    private readonly IHubContext<OnCallNotificationHub> _hub;
    private readonly ICodeCallDispatchService _dispatch;
    private readonly DispatchBackgroundService _dispatchStatus;
    private readonly ITenantContextService _tenantContext;

    public PhoneTreeEventsController(
        IPhoneTreeEventService service,
        IHubContext<OnCallNotificationHub> hub,
        ICodeCallDispatchService dispatch,
        DispatchBackgroundService dispatchStatus,
        ITenantContextService tenantContext)
    {
        _service = service;
        _hub = hub;
        _dispatch = dispatch;
        _dispatchStatus = dispatchStatus;
        _tenantContext = tenantContext;
    }

    // ── Command Center Endpoints ──

    /// <summary>Get live dispatch queue status (pending, processing, totals).</summary>
    [HttpGet("dispatch/status")]
    [Authorize(Policy = "RequireScheduleWrite")]
    public ActionResult<DispatchQueueStatus> GetDispatchStatus()
    {
        return Ok(_dispatchStatus.GetStatus());
    }

    // ── Command Center Endpoints ──

    /// <summary>Get all active (in-progress) incidents for the command center.</summary>
    [HttpGet("events/active")]
    public async Task<ActionResult<List<PhoneTreeEvent>>> GetActiveEvents()
    {
        return await _service.GetActiveEventsAsync();
    }

    /// <summary>Get all resolved incidents for the debrief log.</summary>
    [HttpGet("events/resolved")]
    public async Task<ActionResult<List<PhoneTreeEvent>>> GetResolvedEvents()
    {
        return await _service.GetResolvedEventsAsync();
    }

    /// <summary>Mark an incident as acknowledged (first responder confirmed receipt).</summary>
    [HttpPost("events/{eventId}/acknowledge")]
    [Authorize(Policy = "RequireScheduleWrite")]
    public async Task<ActionResult<PhoneTreeEvent>> AcknowledgeEvent(int eventId)
    {
        try
        {
            var evt = await _service.AcknowledgeEventAsync(eventId);
            await _hub.Clients.All.SendAsync("IncidentUpdated", evt);
            return Ok(evt);
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    /// <summary>Resolve an incident with an outcome summary.</summary>
    [HttpPost("events/{eventId}/resolve")]
    [Authorize(Policy = "RequireScheduleWrite")]
    public async Task<ActionResult<PhoneTreeEvent>> ResolveEvent(int eventId, [FromBody] ResolveEventRequest request)
    {
        try
        {
            var evt = await _service.ResolveEventAsync(eventId, request?.Outcome, request?.NotifiedByName);
            await _hub.Clients.All.SendAsync("IncidentResolved", evt);
            return Ok(evt);
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    /// <summary>Save debrief notes for a resolved incident.</summary>
    [HttpPut("events/{eventId}/debrief")]
    [Authorize(Policy = "RequireScheduleWrite")]
    public async Task<ActionResult<PhoneTreeEvent>> SaveDebriefNotes(int eventId, [FromBody] SaveDebriefNotesRequest request)
    {
        try
        {
            var evt = await _service.SaveDebriefNotesAsync(eventId, request?.Notes);
            return Ok(evt);
        }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    /// <summary>Record a dispatch pipeline step.</summary>
    [HttpPost("events/{eventId}/dispatch-step")]
    [Authorize(Policy = "RequireScheduleWrite")]
    public async Task<ActionResult<DispatchStep>> AddDispatchStep(int eventId, DispatchStep step)
    {
        var created = await _service.AddDispatchStepAsync(eventId, step);
        await _hub.Clients.All.SendAsync("DispatchStepCompleted", created);
        return Ok(created);
    }

    // ── Existing CRUD Endpoints ──

    /// <summary>Get all events for a phone tree.</summary>
    [HttpGet("{treeId}/events")]
    public async Task<ActionResult<List<PhoneTreeEvent>>> GetEvents(int treeId)
    {
        return await _service.GetEventsAsync(treeId);
    }

    /// <summary>Get a single event by ID.</summary>
    [HttpGet("events/{eventId}")]
    public async Task<ActionResult<PhoneTreeEvent>> GetEvent(int eventId)
    {
        var evt = await _service.GetEventByIdAsync(eventId);
        if (evt == null) return NotFound();
        return evt;
    }

    /// <summary>Create a new phone tree event (start a code call) and begin dispatch.</summary>
    [HttpPost("{treeId}/events")]
    [Authorize(Policy = "RequireScheduleWrite")]
    public async Task<ActionResult<PhoneTreeEvent>> CreateEvent(int treeId, [FromBody] StartCodeCallRequest request)
    {
        // Server-side consent gate: firing a live code call (broadcast/paging) always
        // requires explicit operator confirmation. A raw client POST without it is
        // rejected — no silent or accidental dispatch.
        if (request == null || !request.Confirm)
            return BadRequest(new { error = "Operator confirmation required to dispatch a code call." });

        var evt = new PhoneTreeEvent
        {
            PhoneTreeId = treeId,
            StartedAt = request.StartedAt ?? DateTime.UtcNow,
            Location = request.Location,
            LocationZone = request.LocationZone,
            Notes = request.Notes,
            RequestedByName = request.RequestedByName,

            // Triggered-by: pin the signed-in account's name; the reporter is free-text
            // RequestedByName. Employee.Id is captured best-effort (caller may lack a profile).
            InitiatedById = await _tenantContext.GetCurrentEmployeeIdAsync(User),
            InitiatedByName = User.Identity?.Name,
        };

        var created = await _service.CreateEventAsync(evt);

        // Determine code type from the associated phone tree
        var treeType = created.PhoneTree?.TreeType ?? "emergency";

        // Enqueue the dispatch job; it is processed by the dispatch background service
        await _dispatch.DispatchIncidentAsync(created.Id, treeType);

        await _hub.Clients.All.SendAsync("IncidentCreated", created);
        return CreatedAtAction(nameof(GetEvent), new { eventId = created.Id }, created);
    }

    /// <summary>Update a phone tree event (end time, outcome, etc.).</summary>
    [HttpPut("events/{eventId}")]
    [Authorize(Policy = "RequireScheduleWrite")]
    public async Task<ActionResult<PhoneTreeEvent>> UpdateEvent(int eventId, PhoneTreeEvent evt)
    {
        if (eventId != evt.Id)
            return BadRequest(new { error = "Route ID and body ID must match." });

        try
        {
            var updated = await _service.UpdateEventAsync(evt);
            await _hub.Clients.All.SendAsync("IncidentUpdated", updated);
            return Ok(updated);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    /// <summary>Delete a phone tree event (admin only).</summary>
    [HttpDelete("events/{eventId}")]
    [Authorize(Policy = "RequireAdminFull")]
    public async Task<ActionResult> DeleteEvent(int eventId)
    {
        try
        {
            await _service.DeleteEventAsync(eventId);
            await _hub.Clients.All.SendAsync("IncidentUpdated", new { eventId, deleted = true });
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    /// <summary>Add a participant to an event.</summary>
    [HttpPost("events/{eventId}/participants")]
    [Authorize(Policy = "RequireScheduleWrite")]
    public async Task<ActionResult<PhoneTreeEventParticipant>> AddParticipant(int eventId, PhoneTreeEventParticipant participant)
    {
        var created = await _service.AddParticipantAsync(eventId, participant);
        await _hub.Clients.All.SendAsync("IncidentUpdated", new { eventId, action = "participantAdded" });
        return CreatedAtAction(nameof(GetEvent), new { eventId }, created);
    }

    /// <summary>Remove a participant from an event.</summary>
    [HttpDelete("events/{eventId}/participants/{participantId}")]
    [Authorize(Policy = "RequireScheduleWrite")]
    public async Task<ActionResult> RemoveParticipant(int eventId, int participantId)
    {
        try
        {
            await _service.RemoveParticipantAsync(participantId);
            await _hub.Clients.All.SendAsync("IncidentUpdated", new { eventId, action = "participantRemoved" });
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }
}
