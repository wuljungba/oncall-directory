using OnCallApi.Models;

namespace OnCallApi.Services;

public interface IGraphApiService
{
    Task<List<Employee>> SyncUsersAsync(CancellationToken ct = default);
    Task<(List<Employee> Users, string? DeltaToken)> SyncUsersDeltaAsync(string? deltaToken, CancellationToken ct = default);
    Task<string?> GetUserPresenceAsync(string azureAdObjectId, CancellationToken ct = default);
    Task SendTeamsNotificationAsync(string userId, string title, string message, CancellationToken ct = default);
    /// <summary>
    /// Sends a Teams message. Returns false when it could not be delivered — including the
    /// common case of no 1:1 chat existing for an app-only credential, which previously
    /// returned silently and left callers believing the notification had gone out.
    /// </summary>
    Task<bool> SendTeamsMessageAsync(string userId, string htmlContent, CancellationToken ct = default);
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
