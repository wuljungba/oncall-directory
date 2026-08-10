using OnCallApi.Models;

namespace OnCallApi.Services;

public interface IPhoneTreeEventService
{
    Task<List<PhoneTreeEvent>> GetEventsAsync(int phoneTreeId);
    Task<PhoneTreeEvent?> GetEventByIdAsync(int eventId);
    Task<PhoneTreeEvent> CreateEventAsync(PhoneTreeEvent evt);
    Task<PhoneTreeEvent> UpdateEventAsync(PhoneTreeEvent evt);
    Task DeleteEventAsync(int eventId);
    Task<PhoneTreeEventParticipant> AddParticipantAsync(int eventId, PhoneTreeEventParticipant participant);
    Task RemoveParticipantAsync(int participantId);

    // Command center methods
    Task<List<PhoneTreeEvent>> GetActiveEventsAsync();
    Task<List<PhoneTreeEvent>> GetResolvedEventsAsync();
    Task<PhoneTreeEvent> AcknowledgeEventAsync(int eventId);
    Task<PhoneTreeEvent> ResolveEventAsync(int eventId, string? outcome, string? notifiedByName = null);
    Task<DispatchStep> AddDispatchStepAsync(int eventId, DispatchStep step);
    Task<PhoneTreeEvent> SaveDebriefNotesAsync(int eventId, string? notes);
}
