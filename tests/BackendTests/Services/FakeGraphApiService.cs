using System.Runtime.CompilerServices;
using OnCallApi.Models;
using OnCallApi.Services;

namespace BackendTests.Services;

/// <summary>
/// A scripted <see cref="IGraphApiService"/> for testing the directory sync.
///
/// Hand-written rather than mocked: the interface has eleven members, these tests care about
/// exactly one, and the other ten throwing by name documents what the sync must never call —
/// a mock returning defaults would let a stray Graph call pass silently.
/// </summary>
internal sealed class FakeGraphApiService : IGraphApiService
{
    private readonly Queue<GraphUserDeltaResult> _results;

    /// <summary>Every delta call, in order, so a test can assert what was sent to Graph.</summary>
    public List<(string? EntraTenantId, string? DeltaLink)> DeltaCalls { get; } = [];

    public FakeGraphApiService(params GraphUserDeltaResult[] results) => _results = new Queue<GraphUserDeltaResult>(results);

    public Task<GraphUserDeltaResult> SyncUsersDeltaAsync(
        string? entraTenantId, string? deltaLink, CancellationToken ct = default)
    {
        DeltaCalls.Add((entraTenantId, deltaLink));

        if (_results.Count == 0)
        {
            throw new InvalidOperationException(
                "The sync asked Graph for more pages than this test scripted.");
        }

        return Task.FromResult(_results.Dequeue());
    }

    // ── Everything below is out of bounds for the directory sync ──────────────────────────
    public Task<List<Employee>> SyncUsersAsync(CancellationToken ct = default) => throw NotUsed();
    public Task<string?> GetUserPresenceAsync(string azureAdObjectId, CancellationToken ct = default) => throw NotUsed();
    public Task<IReadOnlyDictionary<string, string>> GetPresencesAsync(
        IReadOnlyCollection<string> azureAdObjectIds, CancellationToken ct = default) => throw NotUsed();
    public Task SendTeamsNotificationAsync(string userId, string title, string message, CancellationToken ct = default) => throw NotUsed();
    public Task<bool> SendTeamsMessageAsync(string userId, string htmlContent, CancellationToken ct = default) => throw NotUsed();
    public Task CreateOutlookCalendarEventAsync(
        string userId, string subject, DateTime start, DateTime end, CancellationToken ct = default) => throw NotUsed();
    public Task<List<Employee>> GetDepartmentMembersAsync(string groupId, CancellationToken ct = default) => throw NotUsed();
    public Task<List<GroupInfo>> GetAllGroupsAsync(CancellationToken ct = default) => throw NotUsed();
    public Task CreateSharePointPageAsync(string siteId, string title, string pageContent, CancellationToken ct = default) => throw NotUsed();
    public Task<bool> CheckGraphConnectionAsync(CancellationToken ct = default) => throw NotUsed();

    private static NotSupportedException NotUsed([CallerMemberName] string? member = null) =>
        new($"The directory sync must not call {member}.");
}

/// <summary>Readable delta results, so each test says what KIND of run it is describing.</summary>
internal static class DeltaResults
{
    public const string DeltaLink = "https://graph.microsoft.com/v1.0/users/delta?$deltatoken=abc123";

    /// <summary>Started from nothing and reached the end — the only shape where absence means departure.</summary>
    public static GraphUserDeltaResult FullComplete(
        IEnumerable<Employee>? users = null, IEnumerable<string>? removed = null, int pages = 1) =>
        new(users?.ToList() ?? [], removed?.ToList() ?? [], DeltaLink,
            WasFullEnumeration: true, Completed: true, TokenWasRejected: false, PagesRead: pages, FailureDetail: null);

    /// <summary>Resumed from a cursor: it carries changes only, so absence means "unchanged".</summary>
    public static GraphUserDeltaResult IncrementalComplete(
        IEnumerable<Employee>? users = null, IEnumerable<string>? removed = null, int pages = 1) =>
        new(users?.ToList() ?? [], removed?.ToList() ?? [], DeltaLink,
            WasFullEnumeration: false, Completed: true, TokenWasRejected: false, PagesRead: pages, FailureDetail: null);

    /// <summary>Read part of the directory and stopped — no conclusions may be drawn from absence.</summary>
    public static GraphUserDeltaResult FullIncomplete(IEnumerable<Employee>? users = null, int pages = 1) =>
        new(users?.ToList() ?? [], [], null,
            WasFullEnumeration: true, Completed: false, TokenWasRejected: false, PagesRead: pages,
            FailureDetail: "Graph stopped answering half way through.");

    /// <summary>Nothing was read at all.</summary>
    public static GraphUserDeltaResult Failed() =>
        new([], [], null, WasFullEnumeration: true, Completed: false, TokenWasRejected: false, PagesRead: 0,
            FailureDetail: "The directory could not be reached.");
}
