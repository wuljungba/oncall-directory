using System.Collections.Concurrent;
using Azure.Identity;
using Microsoft.Graph;
using Microsoft.Graph.Communications.GetPresencesByUserId;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;
using Microsoft.Extensions.Options;
using OnCallApi.Configuration;
using OnCallApi.Models;
using OnCallApi.Validators;

namespace OnCallApi.Services;

public class GraphApiService : IGraphApiService
{
    private readonly IOptions<GraphApiOptions> _options;
    private readonly ILogger<GraphApiService> _logger;
    private readonly IGraphClientFactory _clients;

    public GraphApiService(
        IOptions<GraphApiOptions> options,
        IGraphClientFactory clients,
        ILogger<GraphApiService> logger)
    {
        _options = options;
        _clients = clients;
        _logger = logger;
    }

    // Client construction and caching moved to GraphClientFactory so a test can put a stub
    // transport underneath the delta paging loop. These two keep every call site below
    // unchanged.
    private GraphServiceClient GetClient() => _clients.For(null);

    private GraphServiceClient GetClientForTenant(string? entraTenantId) => _clients.For(entraTenantId);

    public async Task<bool> CheckGraphConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            var users = await GetClient().Users.GetAsync(config =>
            {
                config.QueryParameters.Top = 1;
                config.QueryParameters.Select = new[] { "id" };
            }, ct);

            var success = users?.Value != null;
            if (success)
            {
                _logger.LogInformation("Graph API connection check succeeded");
            }
            else
            {
                _logger.LogWarning("Graph API connection check: responded but returned no data");
            }
            return success;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Graph API connection check failed: {Message}", ex.Message);
            return false;
        }
    }

    public async Task<DirectoryProbeResult> ProbeDirectoryAsync(
        string? entraTenantId, CancellationToken ct = default)
    {
        var directory = entraTenantId ?? _options.Value.TenantId;

        try
        {
            var users = await GetClientForTenant(entraTenantId).Users.GetAsync(config =>
            {
                config.QueryParameters.Top = 1;
                config.QueryParameters.Select = ["id"];
            }, ct);

            if (users?.Value == null)
            {
                return new DirectoryProbeResult(false, false, "Graph answered but returned nothing.");
            }

            _logger.LogInformation("Directory {TenantId} is readable", directory);
            return DirectoryProbeResult.Ok();
        }
        catch (ODataError e)
        {
            // 401/403 from a directory that exists: the application is there but has not been
            // granted what it needs.
            var needsConsent = e.ResponseStatusCode is 401 or 403;
            _logger.LogWarning("Directory {TenantId} probe failed: {Detail}", directory, DescribeError(e));
            return new DirectoryProbeResult(false, needsConsent, DescribeError(e));
        }
        catch (Exception ex)
        {
            // AADSTS700016 / unauthorized_client means our application does not exist in that
            // directory at all, which is exactly "nobody has consented yet" — an onboarding step
            // the customer has not taken, not an outage.
            var needsConsent = ex.Message.Contains("700016", StringComparison.Ordinal)
                || ex.Message.Contains("unauthorized_client", StringComparison.OrdinalIgnoreCase);

            _logger.LogWarning(ex, "Directory {TenantId} probe failed", directory);
            return new DirectoryProbeResult(false, needsConsent, ex.Message);
        }
    }

    public async Task<DirectoryOrganization> GetOrganizationAsync(
        string? entraTenantId, CancellationToken ct = default)
    {
        var directory = entraTenantId ?? _options.Value.TenantId;

        try
        {
            var orgs = await GetClientForTenant(entraTenantId).Organization.GetAsync(
                config => config.QueryParameters.Select = ["id", "displayName", "verifiedDomains"], ct);

            var org = orgs?.Value?.FirstOrDefault();
            if (org == null)
            {
                return new DirectoryOrganization(null, [], false, "Graph returned no organization.");
            }

            var domains = (org.VerifiedDomains ?? [])
                .Select(d => d.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .ToList();

            return new DirectoryOrganization(org.DisplayName, domains, false, null);
        }
        catch (ODataError e) when (e.ResponseStatusCode is 401 or 403)
        {
            // Organization.Read.All is not granted here. Either the application never asked for
            // it, or this customer consented before it was added — both are fixed by consenting
            // again, and neither should fail the connection they just completed.
            _logger.LogInformation(
                "Directory {TenantId} did not allow reading its organization details ({Detail})",
                directory, DescribeError(e));
            return new DirectoryOrganization(null, [], NeedsReconsent: true, DescribeError(e));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read organization details for directory {TenantId}", directory);
            return new DirectoryOrganization(null, [], false, ex.Message);
        }
    }

    private static string DescribeError(ODataError error) =>
        $"{error.Error?.Code}: {error.Error?.Message}";

    public async Task<List<Employee>> SyncUsersAsync(CancellationToken ct = default)
    {
        var employees = new List<Employee>();
        try
        {
            var users = await GetClient().Users.GetAsync(cancellationToken: ct);

            if (users?.Value == null) return employees;

            foreach (var user in users.Value)
            {
                employees.Add(MapGraphUserToEmployee(user));
            }

            _logger.LogInformation("Synced {Count} users from Azure AD", employees.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to sync users from Azure AD");
        }
        return employees;
    }

    /// <summary>
    /// A directory bigger than this is not enumerated in one run. At Graph's default page size
    /// that is around half a million users; the cap exists so a link that ever cycles stops
    /// rather than loops forever. Hitting it reports an incomplete read, which is safe.
    /// </summary>
    private const int MaxDeltaPages = 500;

    /// <summary>
    /// Exactly the fields the mapper reads. Without <c>$select</c>, Graph returns its full default
    /// projection for every user on every page.
    ///
    /// All three mail fields are load-bearing: <see cref="ResolveEmail"/> falls back mail →
    /// otherMails → userPrincipalName, and a user with no resolved address is skipped by the sync.
    /// Drop one and an entire directory can go unimported.
    /// </summary>
    private static readonly string[] UserSelect =
    [
        "id", "givenName", "surname", "jobTitle",
        "mail", "otherMails", "userPrincipalName",
        "businessPhones", "mobilePhone",
        "officeLocation", "department", "accountEnabled",
    ];

    /// <summary>
    /// A deltaLink carries <c>$deltatoken</c>; a nextLink carries <c>$skiptoken</c>. The previous
    /// code stored whichever it happened to hold, so a stored value may be a mid-enumeration
    /// cursor — replaying one returns the tail of a stale page set and presents it as the whole
    /// directory. Anything that is not a deltaLink is discarded, which migrates every poisoned
    /// cursor with a single full run and no runbook step.
    /// </summary>
    internal static bool IsUsableDeltaLink(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Contains("$deltatoken=", StringComparison.OrdinalIgnoreCase);

    /// <summary>Graph has forgotten this cursor and wants a fresh enumeration.</summary>
    internal static bool IsExpiredDeltaLink(ODataError error) =>
        error.ResponseStatusCode == 410
        || string.Equals(error.Error?.Code, "syncStateNotFound", StringComparison.OrdinalIgnoreCase)
        || string.Equals(error.Error?.Code, "resyncRequired", StringComparison.OrdinalIgnoreCase);

    public async Task<GraphUserDeltaResult> SyncUsersDeltaAsync(
        string? entraTenantId, string? deltaLink, CancellationToken ct = default)
    {
        var directory = entraTenantId ?? _options.Value.TenantId;

        GraphServiceClient client;
        try
        {
            client = GetClientForTenant(entraTenantId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No Graph client for directory {TenantId}", directory);
            return Failed(!IsUsableDeltaLink(deltaLink), $"Could not build a Graph client: {ex.Message}");
        }

        if (!string.IsNullOrWhiteSpace(deltaLink) && !IsUsableDeltaLink(deltaLink))
        {
            _logger.LogWarning(
                "Stored delta state for directory {TenantId} is not a deltaLink, so it cannot be resumed; "
                + "enumerating the directory in full", directory);
            deltaLink = null;
        }

        try
        {
            return await EnumerateAsync(client, deltaLink, directory, ct);
        }
        catch (ODataError e) when (IsExpiredDeltaLink(e) && IsUsableDeltaLink(deltaLink))
        {
            // Guarded on "we actually sent a link": the retry below is itself a full enumeration,
            // so a 410 from it cannot come back through here and loop.
            _logger.LogWarning(
                "Delta state for directory {TenantId} is no longer valid ({Code}); enumerating in full once",
                directory, e.Error?.Code);
            try
            {
                return (await EnumerateAsync(client, null, directory, ct)) with { TokenWasRejected = true };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Full re-enumeration failed for directory {TenantId}", directory);
                return Failed(true, ex.Message) with { TokenWasRejected = true };
            }
        }
        catch (Exception ex)
        {
            // Reported, not just logged. This used to return an empty list that was
            // indistinguishable from "the directory is empty", and the caller treated the
            // absence of every user as proof that every user had left.
            _logger.LogError(ex, "Failed to sync users delta from directory {TenantId}", directory);
            return Failed(!IsUsableDeltaLink(deltaLink), ex.Message);
        }
    }

    private static GraphUserDeltaResult Failed(bool wasFullEnumeration, string detail) =>
        new([], [], null, wasFullEnumeration, Completed: false, TokenWasRejected: false, PagesRead: 0, detail);

    /// <summary>
    /// Reads every page, from a stored deltaLink or from scratch, and stops only at a deltaLink.
    ///
    /// The old implementation read page one and stored whatever link it found, so a directory
    /// larger than one page was reported as if the remainder did not exist — and the caller
    /// deactivates people who are absent.
    /// </summary>
    private async Task<GraphUserDeltaResult> EnumerateAsync(
        GraphServiceClient client, string? startLink, string? directory, CancellationToken ct)
    {
        var users = new List<Employee>();
        var removed = new List<string>();
        var startedFull = !IsUsableDeltaLink(startLink);
        var pages = 0;

        // A fresh enumeration asks for the fields we map. A resumption replays Graph's own URL
        // verbatim — it already encodes the query, and re-applying parameters corrupts it.
        var page = startedFull
            ? await client.Users.Delta.GetAsDeltaGetResponseAsync(
                config => config.QueryParameters.Select = UserSelect, ct)
            : await client.Users.Delta.WithUrl(startLink!).GetAsDeltaGetResponseAsync(cancellationToken: ct);

        while (true)
        {
            if (page == null)
            {
                return new GraphUserDeltaResult(users, removed, null, startedFull, false, false, pages,
                    "Graph returned an empty response.");
            }

            pages++;
            Collect(page, users, removed);

            if (!string.IsNullOrEmpty(page.OdataDeltaLink))
            {
                return new GraphUserDeltaResult(
                    users, removed, page.OdataDeltaLink, startedFull, true, false, pages, null);
            }

            if (string.IsNullOrEmpty(page.OdataNextLink))
            {
                return new GraphUserDeltaResult(users, removed, null, startedFull, false, false, pages,
                    "Graph returned a page with neither a nextLink nor a deltaLink, so the directory was "
                    + "only partly read.");
            }

            if (pages >= MaxDeltaPages)
            {
                _logger.LogWarning(
                    "Stopped reading directory {TenantId} after {Pages} pages without reaching the end",
                    directory, pages);
                return new GraphUserDeltaResult(users, removed, null, startedFull, false, false, pages,
                    $"Stopped after {MaxDeltaPages} pages without reaching the end of the directory.");
            }

            ct.ThrowIfCancellationRequested();

            try
            {
                page = await client.Users.Delta
                    .WithUrl(page.OdataNextLink)
                    .GetAsDeltaGetResponseAsync(cancellationToken: ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Keep the pages already read. Their people are still worth upserting, and the
                // result says the read was incomplete, which is what stops anyone being
                // deactivated for being absent from a directory we only partly saw.
                //
                // Deliberately not rethrown: only a failure on the FIRST request propagates, so
                // an expired-cursor 410 can still be answered with one full re-enumeration.
                _logger.LogError(ex,
                    "Directory {TenantId} stopped answering after {Pages} page(s); keeping what was read",
                    directory, pages);

                return new GraphUserDeltaResult(users, removed, null, startedFull, false, false, pages,
                    $"Stopped after {pages} page(s): {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Splits one page into people and departures.
    ///
    /// A <c>@removed</c> entry is Graph naming someone who is gone. It is the only departure signal
    /// an incremental run has, and it used to be discarded — leaving absence, which an incremental
    /// response cannot support, as the only one.
    /// </summary>
    private static void Collect(
        Microsoft.Graph.Users.Delta.DeltaGetResponse page, List<Employee> users, List<string> removed)
    {
        foreach (var user in page.Value ?? [])
        {
            if (user.AdditionalData?.ContainsKey("@removed") == true)
            {
                if (!string.IsNullOrWhiteSpace(user.Id)) removed.Add(user.Id);
                continue;
            }

            users.Add(MapGraphUserToEmployee(user));
        }
    }

    public async Task<string?> GetUserPresenceAsync(string azureAdObjectId, CancellationToken ct = default)
    {
        try
        {
            var presence = await GetClient().Users[azureAdObjectId].Presence.GetAsync(cancellationToken: ct);
            return NormalizePresence(presence?.Availability);
        }
        catch
        {
            return "unknown";
        }
    }

    /// <summary>Graph's documented ceiling for one getPresencesByUserId request.</summary>
    private const int PresenceBatchSize = 650;

    public async Task<IReadOnlyDictionary<string, string>> GetPresencesAsync(
        IReadOnlyCollection<string> azureAdObjectIds, CancellationToken ct = default)
    {
        var presences = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (azureAdObjectIds.Count == 0) return presences;

        var ids = azureAdObjectIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var chunk in ids.Chunk(PresenceBatchSize))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var body = new GetPresencesByUserIdPostRequestBody { Ids = chunk.ToList() };
                var response = await GetClient().Communications.GetPresencesByUserId
                    .PostAsGetPresencesByUserIdPostResponseAsync(body, cancellationToken: ct);

                foreach (var presence in response?.Value ?? [])
                {
                    if (string.IsNullOrEmpty(presence.Id)) continue;
                    presences[presence.Id] = NormalizePresence(presence.Availability);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One rejected chunk costs us that chunk, not the whole directory. Left out
                // of the result rather than recorded as "unknown": the caller can tell the
                // difference between a person who is offline and one we failed to ask about.
                _logger.LogError(ex, "Presence batch failed for {Count} of {Total} user(s)",
                    chunk.Length, ids.Length);
            }
        }

        return presences;
    }

    /// <summary>
    /// Normalizes Graph presence to the canonical lowercase set the frontend expects
    /// ('available' | 'busy' | 'dnd' | 'offline' | 'unknown'). Graph returns capitalized
    /// values and its own convenience states ("DoNotDisturb", "OutOfOffice", "BeRightBack",
    /// "PresenceUnknown"), which the UI previously never matched.
    /// </summary>
    private static string NormalizePresence(string? raw) => (raw?.ToLowerInvariant()) switch
    {
        "available" => "available",
        "busy" or "dnd" => "dnd",
        "offline" or "presenceunknown" or "away" or "berightback" or "outofoffice" => "offline",
        _ => "unknown",
    };

    public async Task SendTeamsNotificationAsync(string userId, string title, string message, CancellationToken ct = default)
    {
        try
        {
            var chatMessage = new ChatMessage
            {
                Subject = title,
                Body = new ItemBody
                {
                    ContentType = BodyType.Html,
                    Content = message
                }
            };
            try
            {
                var chat = await GetClient().Users[userId].Chats.GetAsync(cancellationToken: ct);
                if (chat?.Value != null && chat.Value.Count > 0)
                {
                    var targetChat = chat.Value.FirstOrDefault(c =>
                        c.ChatType == ChatType.OneOnOne);
                    if (targetChat != null)
                    {
                        await GetClient().Chats[targetChat.Id].Messages.PostAsync(chatMessage, cancellationToken: ct);
                        _logger.LogInformation("Teams message sent to {UserId} via chat {ChatId}", userId, targetChat.Id);
                        return;
                    }
                }
            }
            catch (ODataError ex)
            {
                _logger.LogWarning(ex, "Could not send Teams message via chat; trying activity notification fallback");
            }

            _logger.LogInformation("Teams notification queued for {UserId}: {Title} (activity notification)", userId, title);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send Teams notification to {UserId}", userId);
        }
    }

    public async Task<bool> SendTeamsMessageAsync(string userId, string htmlContent, CancellationToken ct = default)
    {
        try
        {
            var chatMessage = new ChatMessage
            {
                Body = new ItemBody
                {
                    // Html, not Text: the caller builds rendered markup. Sending it as
                    // text delivered the raw payload into the chat.
                    ContentType = BodyType.Html,
                    Content = htmlContent
                }
            };

            try
            {
                var chats = await GetClient().Users[userId].Chats.GetAsync(cancellationToken: ct);
                var oneOnOne = chats?.Value?.FirstOrDefault(c => c.ChatType == ChatType.OneOnOne);
                if (oneOnOne != null)
                {
                    await GetClient().Chats[oneOnOne.Id].Messages.PostAsync(chatMessage, cancellationToken: ct);
                    _logger.LogInformation("Teams message sent to {UserId}", userId);
                    return true;
                }

                // An app-only credential typically has no 1:1 chats and cannot list them
                // without protected-API approval, so this is the common outcome rather than
                // an edge case. It used to fall through silently and report nothing at all.
                _logger.LogWarning(
                    "No 1:1 Teams chat available for {UserId} — message NOT delivered", userId);
            }
            catch (ODataError ex)
            {
                _logger.LogWarning(ex,
                    "Graph rejected the Teams chat lookup for {UserId} — message NOT delivered", userId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send Teams message to {UserId}", userId);
        }

        return false;
    }

    public async Task CreateOutlookCalendarEventAsync(string userId, string subject, DateTime start, DateTime end, CancellationToken ct = default)
    {
        try
        {
            var calendarEvent = new Event
            {
                Subject = subject,
                Start = new DateTimeTimeZone
                {
                    DateTime = start.ToString("o"),
                    TimeZone = "UTC"
                },
                End = new DateTimeTimeZone
                {
                    DateTime = end.ToString("o"),
                    TimeZone = "UTC"
                }
            };
            await GetClient().Users[userId].Calendar.Events.PostAsync(calendarEvent, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create calendar event for {UserId}", userId);
        }
    }

    /// <summary>A directory of more than one page of groups is not a rare shape; 100 is Graph's default.</summary>
    private const int MaxGroupPages = 100;

    public async Task<GraphGroupsResult> GetAllGroupsAsync(
        string? entraTenantId, CancellationToken ct = default)
    {
        var directory = entraTenantId ?? _options.Value.TenantId;
        var groups = new List<GroupInfo>();
        var pages = 0;

        try
        {
            var client = GetClientForTenant(entraTenantId);
            var page = await client.Groups.GetAsync(
                config => config.QueryParameters.Select = ["id", "displayName", "description"], ct);

            while (page != null)
            {
                pages++;
                foreach (var g in page.Value ?? [])
                {
                    if (g.Id != null && g.DisplayName != null)
                    {
                        groups.Add(new GroupInfo(g.Id, g.DisplayName, g.Description));
                    }
                }

                if (string.IsNullOrEmpty(page.OdataNextLink))
                {
                    _logger.LogInformation(
                        "Read {Count} group(s) from directory {TenantId} over {Pages} page(s)",
                        groups.Count, directory, pages);
                    return new GraphGroupsResult(groups, Completed: true, null);
                }

                if (pages >= MaxGroupPages)
                {
                    return new GraphGroupsResult(groups, Completed: false,
                        $"Stopped after {MaxGroupPages} pages without reaching the end of the group list.");
                }

                ct.ThrowIfCancellationRequested();
                page = await client.Groups.WithUrl(page.OdataNextLink).GetAsync(cancellationToken: ct);
            }

            return new GraphGroupsResult(groups, Completed: false, "Graph returned an empty response.");
        }
        catch (Exception ex)
        {
            // Reported rather than swallowed: an empty list read as "this directory has no
            // groups" is how a failed call becomes a decision.
            _logger.LogError(ex, "Failed to read groups from directory {TenantId}", directory);
            return new GraphGroupsResult(groups, Completed: false, ex.Message);
        }
    }

    public async Task CreateSharePointPageAsync(string siteId, string title, string pageContent, CancellationToken ct = default)
    {
        try
        {
            var listItem = new Microsoft.Graph.Models.ListItem
            {
                Fields = new Microsoft.Graph.Models.FieldValueSet
                {
                    AdditionalData = new Dictionary<string, object>
                    {
                        ["Title"] = title,
                        ["ContentType"] = "SitePage",
                        ["CanvasContent1"] = pageContent,
                    }
                }
            };
            await GetClient().Sites[siteId].Lists["SitePages"].Items.PostAsync(listItem, cancellationToken: ct);
            _logger.LogInformation("SharePoint page created: {Title}", title);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create SharePoint page: {Title}", title);
            throw;
        }
    }

    public async Task<GraphMembersResult> GetDepartmentMembersAsync(
        string? entraTenantId, string groupId, CancellationToken ct = default)
    {
        var directory = entraTenantId ?? _options.Value.TenantId;
        var members = new List<Employee>();
        var pages = 0;

        try
        {
            var client = GetClientForTenant(entraTenantId);
            var page = await client.Groups[groupId].Members.GetAsync(cancellationToken: ct);

            while (page != null)
            {
                pages++;
                // Only people. Nested groups, devices and service principals can all be members,
                // and none of them is a directory contact.
                foreach (var member in (page.Value ?? []).OfType<Microsoft.Graph.Models.User>())
                {
                    members.Add(MapGraphUserToEmployee(member));
                }

                if (string.IsNullOrEmpty(page.OdataNextLink))
                {
                    return new GraphMembersResult(members, Completed: true, null);
                }

                if (pages >= MaxGroupPages)
                {
                    return new GraphMembersResult(members, Completed: false,
                        $"Stopped after {MaxGroupPages} pages without reaching the end of the membership.");
                }

                ct.ThrowIfCancellationRequested();
                page = await client.Groups[groupId].Members
                    .WithUrl(page.OdataNextLink)
                    .GetAsync(cancellationToken: ct);
            }

            return new GraphMembersResult(members, Completed: false, "Graph returned an empty response.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read members of group {GroupId} in directory {TenantId}",
                groupId, directory);
            return new GraphMembersResult(members, Completed: false, ex.Message);
        }
    }

    /// <summary>
    /// Best available address for a directory user.
    ///
    /// Entra leaves `mail` null for any cloud-only account without a mailbox, which is
    /// most accounts created in the portal. Employee.Email is required and uniquely
    /// indexed, so a blank cannot be stored and those users were skipped entirely — a
    /// directory sync that fetched everyone and imported nobody.
    ///
    /// The userPrincipalName is the fallback: unique, human-readable, and always present.
    /// A guest's UPN is not, though — `someone_gmail.com#EXT#@tenant.onmicrosoft.com` is
    /// directory plumbing rather than a way to reach anyone, and writing it into a contact
    /// directory would be inventing an address. Guests keep being skipped, and the skip
    /// message says what to set.
    ///
    /// Note this address is an identifier and a displayed contact; it is not how anyone is
    /// paged. Code calls go out over SMS and the paging channels, keyed on phone numbers.
    /// </summary>
    internal static string ResolveEmail(User user)
    {
        if (!string.IsNullOrWhiteSpace(user.Mail)) return user.Mail.Trim();

        var other = user.OtherMails?.FirstOrDefault(m => !string.IsNullOrWhiteSpace(m));
        if (!string.IsNullOrWhiteSpace(other)) return other.Trim();

        var upn = user.UserPrincipalName?.Trim();
        if (string.IsNullOrWhiteSpace(upn)) return string.Empty;
        if (upn.Contains("#EXT#", StringComparison.OrdinalIgnoreCase)) return string.Empty;

        return upn;
    }

    private static Employee MapGraphUserToEmployee(User user)
    {
        // Split first, so "+1 202-555-0134 x4412" keeps both halves and a bare "x3434" becomes an
        // extension rather than a fabricated number.
        var (officeNumber, officeExtension) =
            PhoneValidation.SplitExtension(user.BusinessPhones?.FirstOrDefault());

        return new Employee
        {
            AzureAdObjectId = user.Id ?? string.Empty,
            FirstName = user.GivenName ?? string.Empty,
            LastName = user.Surname ?? string.Empty,
            Title = user.JobTitle,
            Email = ResolveEmail(user),
            // NormalizeToDialable, the same helper the CSV import and the admin API use, not the
            // display-only NormalizeToE164 this path used to call. That one promotes "4412" to
            // "+14412": it passes the E.164 regex, stores cleanly, and then goes nowhere when a
            // code call tries to dial it. A number we cannot dial is better stored as nothing.
            OfficePhone = PhoneValidation.NormalizeToDialable(officeNumber),
            MobilePhone = PhoneValidation.NormalizeToDialable(user.MobilePhone),
            Extension = officeExtension,
            OfficeLocation = user.OfficeLocation,
            // Graph's own view of whether this account can sign in. Carried through so the sync
            // can treat a disabled account as a departure — which is how most directories
            // represent a leaver, since they disable rather than delete.
            IsActive = user.AccountEnabled ?? true,
            LastSyncedAt = DateTime.UtcNow
        };
    }
}