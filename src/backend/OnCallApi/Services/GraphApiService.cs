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

    public async Task<(List<Employee> Users, string? DeltaToken)> SyncUsersDeltaAsync(string? deltaToken, CancellationToken ct = default)
    {
        var employees = new List<Employee>();
        string? newDeltaToken = null;
        try
        {
            var users = await GetClient().Users.Delta.GetAsDeltaGetResponseAsync(cancellationToken: ct);

            if (users?.Value == null) return (employees, newDeltaToken);

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
            _logger.LogError(ex, "Failed to sync users delta from Azure AD");
        }
        return (employees, newDeltaToken);
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

    public async Task SendTeamsMessageAsync(string userId, string messageJson, CancellationToken ct = default)
    {
        try
        {
            var chatMessage = new ChatMessage
            {
                Body = new ItemBody
                {
                    ContentType = BodyType.Text,
                    Content = messageJson
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
                    return;
                }
            }
            catch (ODataError)
            {
                _logger.LogWarning("No Teams chat available for {UserId}; notification logged", userId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send Teams message to {UserId}", userId);
        }
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

    private static Employee MapGraphUserToEmployee(User user)
    {
        return new Employee
        {
            AzureAdObjectId = user.Id ?? string.Empty,
            FirstName = user.GivenName ?? string.Empty,
            LastName = user.Surname ?? string.Empty,
            Title = user.JobTitle,
            Email = user.Mail ?? string.Empty,
            // Normalize to E.164 on ingestion — AD/Graph numbers are rarely stored
            // in canonical E.164 (spaces, dashes, parens; often missing the country code).
            OfficePhone = PhoneValidation.NormalizeToE164(user.BusinessPhones?.FirstOrDefault()),
            MobilePhone = PhoneValidation.NormalizeToE164(user.MobilePhone),
            OfficeLocation = user.OfficeLocation,
            LastSyncedAt = DateTime.UtcNow
        };
    }
}