using Microsoft.EntityFrameworkCore;
using OnCallApi.Data;
using OnCallApi.Models;

namespace OnCallApi.Services;

public class PhoneTreeEventService : IPhoneTreeEventService
{
    private readonly AppDbContext _db;
    private readonly ILogger<PhoneTreeEventService> _logger;

    public PhoneTreeEventService(AppDbContext db, ILogger<PhoneTreeEventService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<List<PhoneTreeEvent>> GetEventsAsync(int phoneTreeId)
    {
        return await _db.PhoneTreeEvents
            .Include(e => e.InitiatedBy)
            .Include(e => e.Participants).ThenInclude(p => p.Employee)
            .Where(e => e.PhoneTreeId == phoneTreeId)
            .OrderByDescending(e => e.StartedAt)
            .ToListAsync();
    }

    public async Task<PhoneTreeEvent?> GetEventByIdAsync(int eventId)
    {
        return await _db.PhoneTreeEvents
            .Include(e => e.InitiatedBy)
            .Include(e => e.Participants).ThenInclude(p => p.Employee)
            // DispatchSteps are what say whether anyone was actually reached. Without this
            // include the single-event endpoint returned an empty dispatch timeline while
            // the active/resolved list endpoints returned a populated one, so drilling into
            // a specific incident hid the very failures the operator opened it to see.
            .Include(e => e.DispatchSteps)
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
        var existing = await _db.PhoneTreeEvents.FindAsync(evt.Id)
            ?? throw new KeyNotFoundException($"Phone tree event {evt.Id} not found");

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
        var evt = await _db.PhoneTreeEvents.FindAsync(eventId)
            ?? throw new KeyNotFoundException($"Phone tree event {eventId} not found");

        _db.PhoneTreeEvents.Remove(evt);
        await _db.SaveChangesAsync();
        _logger.LogInformation("Deleted phone tree event {Id}", eventId);
    }

    public async Task<PhoneTreeEventParticipant> AddParticipantAsync(int eventId, PhoneTreeEventParticipant participant)
    {
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

        _db.PhoneTreeEventParticipants.Remove(participant);
        await _db.SaveChangesAsync();
        _logger.LogInformation("Removed participant {Id} from event", participantId);
    }

    // ── Command Center Methods ──

    public async Task<List<PhoneTreeEvent>> GetActiveEventsAsync()
    {
        return await _db.PhoneTreeEvents
            .Include(e => e.PhoneTree)
            .Include(e => e.InitiatedBy)
            .Include(e => e.Participants).ThenInclude(p => p.Employee)
            .Include(e => e.DispatchSteps)
            .Where(e => e.Status == "active")
            .OrderByDescending(e => e.StartedAt)
            .ToListAsync();
    }

    public async Task<List<PhoneTreeEvent>> GetResolvedEventsAsync()
    {
        return await _db.PhoneTreeEvents
            .Include(e => e.PhoneTree)
            .Include(e => e.InitiatedBy)
            .Include(e => e.Participants).ThenInclude(p => p.Employee)
            .Include(e => e.DispatchSteps)
            .Where(e => e.Status == "completed")
            .OrderByDescending(e => e.EndedAt)
            .ToListAsync();
    }

    public async Task<PhoneTreeEvent> AcknowledgeEventAsync(int eventId)
    {
        var evt = await _db.PhoneTreeEvents.FindAsync(eventId)
            ?? throw new KeyNotFoundException($"Event {eventId} not found");

        if (evt.Status != "active")
            throw new InvalidOperationException("Only active events can be acknowledged.");

        evt.AcknowledgedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        _logger.LogInformation("Event {Id} acknowledged", eventId);
        return evt;
    }

    public async Task<PhoneTreeEvent> ResolveEventAsync(int eventId, string? outcome, string? notifiedByName = null)
    {
        var evt = await _db.PhoneTreeEvents.FindAsync(eventId)
            ?? throw new KeyNotFoundException($"Event {eventId} not found");

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
        step.PhoneTreeEventId = eventId;
        step.StartedAt = DateTime.UtcNow;
        _db.DispatchSteps.Add(step);
        await _db.SaveChangesAsync();
        _logger.LogInformation("Dispatch step {StepKey} added to event {EventId}", step.StepKey, eventId);
        return step;
    }

    public async Task<PhoneTreeEvent> SaveDebriefNotesAsync(int eventId, string? notes)
    {
        var evt = await _db.PhoneTreeEvents.FindAsync(eventId)
            ?? throw new KeyNotFoundException($"Event {eventId} not found");

        evt.DebriefNotes = notes;
        await _db.SaveChangesAsync();
        _logger.LogInformation("Debrief notes saved for event {Id}", eventId);
        return evt;
    }
}
