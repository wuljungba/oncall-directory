using Microsoft.EntityFrameworkCore;
using OnCallApi.Data;
using OnCallApi.Models;

namespace OnCallApi.Services;

public class PhoneTreeEventService : IPhoneTreeEventService
{
    private readonly AppDbContext _db;
    private readonly ITenantScope _scope;
    private readonly ILogger<PhoneTreeEventService> _logger;

    public PhoneTreeEventService(AppDbContext db, ITenantScope scope, ILogger<PhoneTreeEventService> logger)
    {
        _db = db;
        _scope = scope;
        _logger = logger;
    }

    /// <summary>
    /// Restricts an event query to the caller's tenants. Events belong to a tenant through
    /// their phone tree's department, the same path DirectoryService uses for the trees
    /// themselves.
    ///
    /// A caller with no tenants sees nothing: the filter is never skipped for a request,
    /// only for a null scope, which means a super admin or a background service (dispatch,
    /// escalation) running outside any request.
    /// </summary>
    private async Task<IQueryable<PhoneTreeEvent>> ScopeEventsAsync(IQueryable<PhoneTreeEvent> query)
    {
        var tenantIds = await _scope.AllowedTenantIdsAsync();
        if (tenantIds == null) return query;
        return query.Where(e => e.PhoneTree != null
            && e.PhoneTree.Department != null
            && e.PhoneTree.Department.TenantId.HasValue
            && tenantIds.Contains(e.PhoneTree.Department.TenantId.Value));
    }

    /// <summary>
    /// Resolves an event the caller is actually allowed to touch, or throws.
    ///
    /// Every mutator goes through this. An out-of-tenant id is reported as "not found"
    /// rather than "forbidden" so the endpoint does not confirm that another customer's
    /// incident exists.
    /// </summary>
    private async Task<PhoneTreeEvent> RequireEventAsync(int eventId)
    {
        var scoped = await ScopeEventsAsync(_db.PhoneTreeEvents);
        return await scoped.FirstOrDefaultAsync(e => e.Id == eventId)
            ?? throw new KeyNotFoundException($"Phone tree event {eventId} not found");
    }

    public async Task<List<PhoneTreeEvent>> GetEventsAsync(int phoneTreeId)
    {
        var scoped = await ScopeEventsAsync(_db.PhoneTreeEvents);
        return await scoped
            .Include(e => e.InitiatedBy)
            .Include(e => e.Participants).ThenInclude(p => p.Employee)
            .Where(e => e.PhoneTreeId == phoneTreeId)
            .OrderByDescending(e => e.StartedAt)
            .ToListAsync();
    }

    public async Task<PhoneTreeEvent?> GetEventByIdAsync(int eventId)
    {
        var scoped = await ScopeEventsAsync(_db.PhoneTreeEvents);
        return await scoped
            .Include(e => e.InitiatedBy)
            .Include(e => e.Participants).ThenInclude(p => p.Employee)
            // DispatchSteps are what say whether anyone was actually reached. Without this
            // include the single-event endpoint returned an empty dispatch timeline while
            // the active/resolved list endpoints returned a populated one, so drilling into
            // a specific incident hid the very failures the operator opened it to see.
            .Include(e => e.DispatchSteps)
            .Include(e => e.DebriefLog)
            .FirstOrDefaultAsync(e => e.Id == eventId);
    }

    public async Task<PhoneTreeEvent> CreateEventAsync(PhoneTreeEvent evt)
    {
        evt.CreatedAt = DateTime.UtcNow;
        evt.Status = "active";
        _db.PhoneTreeEvents.Add(evt);
        await _db.SaveChangesAsync();

        // Reload with the PhoneTree so callers can read the correct code type
        // (e.g., for dispatch). Without this, TreeType always fell back to "emergency".
        evt = await _db.PhoneTreeEvents
            .Include(e => e.PhoneTree)
            .FirstAsync(e => e.Id == evt.Id);

        _logger.LogInformation("Created phone tree event {Id} for tree {TreeId}", evt.Id, evt.PhoneTreeId);
        return evt;
    }

    public async Task<PhoneTreeEvent> UpdateEventAsync(PhoneTreeEvent evt)
    {
        var existing = await RequireEventAsync(evt.Id);

        existing.EndedAt = evt.EndedAt;
        existing.Status = evt.Status ?? existing.Status;
        existing.Outcome = evt.Outcome;
        existing.Notes = evt.Notes;

        await _db.SaveChangesAsync();
        _logger.LogInformation("Updated phone tree event {Id}", evt.Id);
        return existing;
    }

    public async Task DeleteEventAsync(int eventId)
    {
        var evt = await RequireEventAsync(eventId);

        _db.PhoneTreeEvents.Remove(evt);
        await _db.SaveChangesAsync();
        _logger.LogInformation("Deleted phone tree event {Id}", eventId);
    }

    public async Task<PhoneTreeEventParticipant> AddParticipantAsync(int eventId, PhoneTreeEventParticipant participant)
    {
        await RequireEventAsync(eventId);

        participant.PhoneTreeEventId = eventId;
        _db.PhoneTreeEventParticipants.Add(participant);
        await _db.SaveChangesAsync();
        _logger.LogInformation("Added participant to event {EventId}", eventId);
        return participant;
    }

    public async Task RemoveParticipantAsync(int participantId)
    {
        var participant = await _db.PhoneTreeEventParticipants.FindAsync(participantId)
            ?? throw new KeyNotFoundException($"Participant {participantId} not found");

        // Resolve the parent event through the scoped query, so a participant id cannot be
        // used to reach into another tenant's incident.
        await RequireEventAsync(participant.PhoneTreeEventId);

        _db.PhoneTreeEventParticipants.Remove(participant);
        await _db.SaveChangesAsync();
        _logger.LogInformation("Removed participant {Id} from event", participantId);
    }

    // ── Command Center Methods ──

    public async Task<List<PhoneTreeEvent>> GetActiveEventsAsync()
    {
        var scoped = await ScopeEventsAsync(_db.PhoneTreeEvents);
        return await scoped
            .Include(e => e.PhoneTree)
            .Include(e => e.InitiatedBy)
            .Include(e => e.Participants).ThenInclude(p => p.Employee)
            .Include(e => e.DispatchSteps)
            .Include(e => e.DebriefLog)
            .Where(e => e.Status == "active")
            .OrderByDescending(e => e.StartedAt)
            .ToListAsync();
    }

    public async Task<List<PhoneTreeEvent>> GetResolvedEventsAsync()
    {
        var scoped = await ScopeEventsAsync(_db.PhoneTreeEvents);
        return await scoped
            .Include(e => e.PhoneTree)
            .Include(e => e.InitiatedBy)
            .Include(e => e.Participants).ThenInclude(p => p.Employee)
            .Include(e => e.DispatchSteps)
            .Include(e => e.DebriefLog)
            .Where(e => e.Status == "completed")
            .OrderByDescending(e => e.EndedAt)
            .ToListAsync();
    }

    public async Task<PhoneTreeEvent> AcknowledgeEventAsync(int eventId)
    {
        var evt = await RequireEventAsync(eventId);

        if (evt.Status != "active")
            throw new InvalidOperationException("Only active events can be acknowledged.");

        evt.AcknowledgedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        _logger.LogInformation("Event {Id} acknowledged", eventId);
        return evt;
    }

    public async Task<PhoneTreeEvent> ResolveEventAsync(int eventId, string? outcome, string? notifiedByName = null)
    {
        var evt = await RequireEventAsync(eventId);

        if (evt.Status != "active")
            throw new InvalidOperationException("Only active events can be resolved.");

        evt.Status = "completed";
        evt.EndedAt = DateTime.UtcNow;
        evt.Outcome = outcome;
        if (!string.IsNullOrWhiteSpace(notifiedByName)) evt.NotifiedByName = notifiedByName.Trim();
        evt.ResponseTimeSeconds = evt.AcknowledgedAt.HasValue
            ? (int)(evt.AcknowledgedAt.Value - evt.StartedAt).TotalSeconds
            : (int)(DateTime.UtcNow - evt.StartedAt).TotalSeconds;

        await _db.SaveChangesAsync();
        _logger.LogInformation("Event {Id} resolved", eventId);
        return evt;
    }

    public async Task<DispatchStep> AddDispatchStepAsync(int eventId, DispatchStep step)
    {
        await RequireEventAsync(eventId);

        step.PhoneTreeEventId = eventId;
        step.StartedAt = DateTime.UtcNow;
        _db.DispatchSteps.Add(step);
        await _db.SaveChangesAsync();
        _logger.LogInformation("Dispatch step {StepKey} added to event {EventId}", step.StepKey, eventId);
        return step;
    }

    /// <summary>
    /// Appends one entry to an incident's debrief log. There is no counterpart that edits
    /// or removes an entry, and that is the point: the debrief is the written record of a
    /// code call, so it only ever grows. A blank note is refused rather than stored, since
    /// an empty entry would add a timestamp and an author to the record while saying
    /// nothing about the incident.
    /// </summary>
    public async Task<DebriefNote> AddDebriefNoteAsync(int eventId, string? note, string? authorName)
    {
        await RequireEventAsync(eventId);

        var text = note?.Trim();
        if (string.IsNullOrEmpty(text))
            throw new InvalidOperationException("A debrief note cannot be empty.");

        if (text.Length > 2000)
            throw new InvalidOperationException("A debrief note cannot be longer than 2000 characters.");

        var entry = new DebriefNote
        {
            PhoneTreeEventId = eventId,
            Note = text,
            AuthorName = string.IsNullOrWhiteSpace(authorName) ? null : authorName.Trim(),
            CreatedAt = DateTime.UtcNow,
        };

        _db.DebriefNotes.Add(entry);
        await _db.SaveChangesAsync();

        // The note itself is never logged — a debrief describes a patient event and is
        // treated as PHI. The entry id is enough to find it.
        _logger.LogInformation("Debrief note {NoteId} added to event {Id}", entry.Id, eventId);
        return entry;
    }
}
