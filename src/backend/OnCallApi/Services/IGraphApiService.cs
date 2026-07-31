using OnCallApi.Models;

namespace OnCallApi.Services;

public interface IGraphApiService
{
    Task<List<Employee>> SyncUsersAsync(CancellationToken ct = default);
    Task<(List<Employee> Users, string? DeltaToken)> SyncUsersDeltaAsync(string? deltaToken, CancellationToken ct = default);
    Task<string?> GetUserPresenceAsync(string azureAdObjectId, CancellationToken ct = default);
    Task SendTeamsNotificationAsync(string userId, string title, string message, CancellationToken ct = default);
    Task SendTeamsMessageAsync(string userId, string messageJson, CancellationToken ct = default);
    Task CreateOutlookCalendarEventAsync(string userId, string subject, DateTime start, DateTime end, CancellationToken ct = default);
    Task<List<Employee>> GetDepartmentMembersAsync(string groupId, CancellationToken ct = default);
    Task<List<GroupInfo>> GetAllGroupsAsync(CancellationToken ct = default);
    Task CreateSharePointPageAsync(string siteId, string title, string pageContent, CancellationToken ct = default);

    /// <summary>
    /// Tests Graph API connectivity by fetching a single user.
    /// Returns true on success; logs and returns false on failure.
    /// </summary>
    Task<bool> CheckGraphConnectionAsync(CancellationToken ct = default);
}
