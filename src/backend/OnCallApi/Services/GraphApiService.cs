using System.Collections.Concurrent;
using Azure.Identity;
using Microsoft.Graph;
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
    private GraphServiceClient? _client;
    private bool _clientInitialized;

    // One client per connected customer directory. Each customer consents to this same
    // application in their own Entra tenant, which creates a service principal there; the
    // client id and secret are ours and constant, and only the tenant id varies. Cached
    // because building a credential per sync cycle would re-do the token dance every time.
    private readonly ConcurrentDictionary<string, GraphServiceClient> _tenantClients = new();

    public GraphApiService(IOptions<GraphApiOptions> options, ILogger<GraphApiService> logger)
    {
        _options = options;
        _logger = logger;
    }

    private GraphServiceClient GetClient()
    {
        if (_client != null) return _client;
        if (_clientInitialized)
        {
            throw new InvalidOperationException("Graph API client initialization already failed; check previous logs.");
        }
        _clientInitialized = true;

        try
        {
            var creds = new ClientSecretCredential(
                _options.Value.TenantId,
                _options.Value.ClientId,
                _options.Value.ClientSecret);
            _client = new GraphServiceClient(creds, _options.Value.Scopes);
            _logger.LogInformation("GraphServiceClient initialized successfully with {ScopeCount} scope(s)",
                _options.Value.Scopes.Length);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize GraphServiceClient (tenant: {TenantId})",
                _options.Value.TenantId);
            throw;
        }
        return _client;
    }

    /// <summary>
    /// Graph client for a specific Entra tenant. Blank, or our own tenant, uses the home
    /// client so nothing about the existing single-tenant path changes.
    /// </summary>
    private GraphServiceClient GetClientForTenant(string? entraTenantId)
    {
        if (string.IsNullOrWhiteSpace(entraTenantId)
            || string.Equals(entraTenantId, _options.Value.TenantId, StringComparison.OrdinalIgnoreCase))
        {
            return GetClient();
        }

        return _tenantClients.GetOrAdd(entraTenantId, tenantId =>
        {
            try
            {
                var creds = new ClientSecretCredential(
                    tenantId, _options.Value.ClientId, _options.Value.ClientSecret);
                _logger.LogInformation("GraphServiceClient initialized for connected directory {TenantId}", tenantId);
                return new GraphServiceClient(creds, _options.Value.Scopes);
            }
            catch (Exception ex)
            {
                // Do not cache a broken client: GetOrAdd would keep handing it back and the
                // customer's directory would stay dead until a restart.
                _logger.LogError(ex, "Failed to initialize GraphServiceClient for directory {TenantId}", tenantId);
                throw;
            }
        });
    }

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

    public Task<GraphUserDelta> SyncUsersDeltaAsync(string? deltaToken, CancellationToken ct = default) =>
        SyncUsersDeltaAsync(null, deltaToken, ct);

    public async Task<GraphUserDelta> SyncUsersDeltaAsync(
        string? entraTenantId, string? deltaToken, CancellationToken ct = default)
    {
        var employees = new List<Employee>();
        string? newDeltaToken = null;
        try
        {
            var users = await GetClientForTenant(entraTenantId)
                .Users.Delta.GetAsDeltaGetResponseAsync(cancellationToken: ct);

            if (users?.Value == null) return new GraphUserDelta(employees, newDeltaToken, true);

            if (users.OdataDeltaLink != null)
            {
                newDeltaToken = users.OdataDeltaLink;
            }
            else if (users.OdataNextLink != null)
            {
                newDeltaToken = users.OdataNextLink;
            }

            foreach (var user in users.Value)
            {
                if (user.AdditionalData?.ContainsKey("@removed") == true)
                    continue;
                employees.Add(MapGraphUserToEmployee(user));
            }
        }
        catch (Exception ex)
        {
            // Reported, not just logged. This used to return an empty list that was
            // indistinguishable from "the directory is empty", and the caller treated the
            // absence of every user as proof that every user had left.
            _logger.LogError(ex, "Failed to sync users delta from directory {TenantId}",
                entraTenantId ?? _options.Value.TenantId);
            return new GraphUserDelta(employees, newDeltaToken, false);
        }
        return new GraphUserDelta(employees, newDeltaToken, true);
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

    public async Task<List<GroupInfo>> GetAllGroupsAsync(CancellationToken ct = default)
    {
        var groups = new List<GroupInfo>();
        try
        {
            var result = await GetClient().Groups.GetAsync(cancellationToken: ct);
            if (result?.Value != null)
            {
                foreach (var g in result.Value)
                {
                    if (g.Id != null && g.DisplayName != null)
                    {
                        groups.Add(new GroupInfo(g.Id, g.DisplayName, g.Description));
                    }
                }
            }
            _logger.LogInformation("Retrieved {Count} M365 groups", groups.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve M365 groups");
        }
        return groups;
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

    public async Task<List<Employee>> GetDepartmentMembersAsync(string groupId, CancellationToken ct = default)
    {
        var members = new List<Employee>();
        try
        {
            var groupMembers = await GetClient().Groups[groupId].Members.GetAsync(cancellationToken: ct);
            if (groupMembers?.Value == null) return members;

            foreach (var member in groupMembers.Value.OfType<Microsoft.Graph.Models.User>())
            {
                members.Add(MapGraphUserToEmployee(member));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get members for group {GroupId}", groupId);
        }
        return members;
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
        return new Employee
        {
            AzureAdObjectId = user.Id ?? string.Empty,
            FirstName = user.GivenName ?? string.Empty,
            LastName = user.Surname ?? string.Empty,
            Title = user.JobTitle,
            Email = ResolveEmail(user),
            // Normalize to E.164 on ingestion — AD/Graph numbers are rarely stored
            // in canonical E.164 (spaces, dashes, parens; often missing the country code).
            OfficePhone = PhoneValidation.NormalizeToE164(user.BusinessPhones?.FirstOrDefault()),
            MobilePhone = PhoneValidation.NormalizeToE164(user.MobilePhone),
            OfficeLocation = user.OfficeLocation,
            LastSyncedAt = DateTime.UtcNow
        };
    }
}