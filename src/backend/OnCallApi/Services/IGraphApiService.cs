using OnCallApi.Models;

namespace OnCallApi.Services;

public interface IGraphApiService
{
    Task<List<Employee>> SyncUsersAsync(CancellationToken ct = default);

    /// <summary>
    /// Delta sync against a specific connected directory. A blank tenant id means our own.
    ///
    /// <paramref name="deltaLink"/> is the absolute URL Graph handed back last time, not a bare
    /// token — it is named for what it is, because the previous parameter was called a token,
    /// was never sent, and the enumeration silently restarted every run.
    /// </summary>
    Task<GraphUserDeltaResult> SyncUsersDeltaAsync(
        string? entraTenantId, string? deltaLink, CancellationToken ct = default);
    Task<string?> GetUserPresenceAsync(string azureAdObjectId, CancellationToken ct = default);

    /// <summary>
    /// Presence for many people in one round trip, keyed by Entra object id.
    ///
    /// Graph accepts up to 650 ids per call, so this chunks internally: a directory of
    /// 5,000 costs 8 requests rather than 5,000. Ids Graph does not answer for are simply
    /// absent from the result — callers must treat that as "nothing learned", not as
    /// evidence the person is offline.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> GetPresencesAsync(
        IReadOnlyCollection<string> azureAdObjectIds, CancellationToken ct = default);
    Task SendTeamsNotificationAsync(string userId, string title, string message, CancellationToken ct = default);
    /// <summary>
    /// Sends a Teams message. Returns false when it could not be delivered — including the
    /// common case of no 1:1 chat existing for an app-only credential, which previously
    /// returned silently and left callers believing the notification had gone out.
    /// </summary>
    Task<bool> SendTeamsMessageAsync(string userId, string htmlContent, CancellationToken ct = default);
    Task CreateOutlookCalendarEventAsync(string userId, string subject, DateTime start, DateTime end, CancellationToken ct = default);
    /// <summary>
    /// Every group in one directory. A blank tenant id means our own.
    ///
    /// Paged, and it says whether it reached the end: the single-page version this replaces
    /// silently described a directory of 300 groups as the first 100.
    /// </summary>
    Task<GraphGroupsResult> GetAllGroupsAsync(string? entraTenantId, CancellationToken ct = default);

    /// <summary>
    /// Every member of one group, in one directory. A blank tenant id means our own.
    ///
    /// <see cref="GraphMembersResult.Completed"/> matters more here than anywhere else: a caller
    /// that revokes access for people missing from this list must not act on a partial read.
    /// </summary>
    Task<GraphMembersResult> GetDepartmentMembersAsync(
        string? entraTenantId, string groupId, CancellationToken ct = default);
    Task CreateSharePointPageAsync(string siteId, string title, string pageContent, CancellationToken ct = default);

    /// <summary>
    /// Tests Graph API connectivity by fetching a single user.
    /// Returns true on success; logs and returns false on failure.
    /// </summary>
    Task<bool> CheckGraphConnectionAsync(CancellationToken ct = default);
}

/// <summary>
/// A whole directory delta read — every page of it, not one page.
///
/// The flags exist because an empty <paramref name="Users"/> list means four different things
/// (nothing changed, the call failed, the directory is empty, or we only read part of it) and the
/// caller deactivates people based on absence from this list. It must be able to tell them apart:
///
/// <list type="bullet">
/// <item><paramref name="WasFullEnumeration"/> — started from nothing, so the result describes the
/// entire directory. An incremental run describes only what changed, and absence from it means
/// "unchanged", never "gone".</item>
/// <item><paramref name="Completed"/> — every page was read and Graph handed back a deltaLink.
/// A partial read must never be mistaken for a complete picture.</item>
/// <item><paramref name="RemovedObjectIds"/> — departures Graph reported outright. A fact about a
/// named person, unlike absence, so it is safe to act on in any run.</item>
/// </list>
///
/// Invariants, pinned by tests: <c>Completed</c> implies a non-null <paramref name="DeltaLink"/>;
/// the link is always a deltaLink and never a mid-enumeration skiptoken; and
/// <c>WasFullEnumeration &amp;&amp; Completed</c> is the ONLY state in which absence means absence.
/// </summary>
public sealed record GraphUserDeltaResult(
    IReadOnlyList<Employee> Users,
    IReadOnlyList<string> RemovedObjectIds,
    string? DeltaLink,
    bool WasFullEnumeration,
    bool Completed,
    bool TokenWasRejected,
    int PagesRead,
    string? FailureDetail)
{
    /// <summary>
    /// Nothing was read and nothing can be concluded — the run must change nothing and keep the
    /// cursor it had.
    /// </summary>
    public bool ReadNothing => !Completed && Users.Count == 0 && RemovedObjectIds.Count == 0;

    /// <summary>
    /// Whether absence from <see cref="Users"/> may be read as "this person has left".
    /// </summary>
    public bool MayReconcileByAbsence => WasFullEnumeration && Completed && Users.Count > 0;
}

/// <summary>
/// Groups read from one directory, and whether every page of them was read.
///
/// <paramref name="Completed"/> is not decoration. Anything that reconciles against this list —
/// deciding a department or an admin no longer exists — must refuse to act on a partial read,
/// because "absent from a truncated page" and "gone" are indistinguishable in the result.
/// </summary>
public sealed record GraphGroupsResult(
    IReadOnlyList<GroupInfo> Groups, bool Completed, string? FailureDetail);

/// <summary>
/// Members read from one group, and whether every page of them was read. Same warning as
/// <see cref="GraphGroupsResult"/>, and it bites harder: the tenant-admin sync deletes rows for
/// members it cannot see.
/// </summary>
public sealed record GraphMembersResult(
    IReadOnlyList<Employee> Members, bool Completed, string? FailureDetail);
