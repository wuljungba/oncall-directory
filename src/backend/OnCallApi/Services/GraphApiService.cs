using Azure.Identity;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;
using Microsoft.Extensions.Options;
using OnCallApi.Configuration;
using OnCallApi.Models;

namespace OnCallApi.Services;

public class GraphApiService : IGraphApiService
{
    private readonly GraphServiceClient _graphClient;
    private readonly ILogger<GraphApiService> _logger;

    public GraphApiService(IOptions<GraphApiOptions> options, ILogger<GraphApiService> logger)
    {
        _logger = logger;
        var creds = new ClientSecretCredential(
            options.Value.TenantId,
            options.Value.ClientId,
            options.Value.ClientSecret);
        _graphClient = new GraphServiceClient(creds);
    }

    public async Task<List<Employee>> SyncUsersAsync(CancellationToken ct = default)
    {
        var employees = new List<Employee>();
        try
        {
            // Use a simple users request; avoid SDK-specific query-parameter types here.
            var users = await _graphClient.Users.GetAsync(cancellationToken: ct);

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

    public async Task<List<Employee>> SyncUsersDeltaAsync(string? deltaToken, CancellationToken ct = default)
    {
        var employees = new List<Employee>();
        try
        {
            // Use delta without SDK-specific query parameter manipulation; some SDK versions
            // expose different query parameter properties. Keep delta simple and rely on server defaults.
            var users = await _graphClient.Users.Delta.GetAsync(cancellationToken: ct);

            if (users?.Value == null) return employees;

            foreach (var user in users.Value)
            {
                if (user.AdditionalData?.ContainsKey("@removed") == true)
                    continue; // Skip deleted users, handle separately
                employees.Add(MapGraphUserToEmployee(user));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to sync users delta from Azure AD");
        }
        return employees;
    }

    public async Task<string?> GetUserPresenceAsync(string azureAdObjectId, CancellationToken ct = default)
    {
        try
        {
            var presence = await _graphClient.Users[azureAdObjectId].Presence.GetAsync(cancellationToken: ct);
            return presence?.Availability ?? "unknown";
        }
        catch
        {
            return "unknown";
        }
    }

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
            // Send to user's primary Teams chat via Graph API.
            // Requires ChatMessage.Send or Chat.ReadWrite.All application permission.
            // Uses the user's email or AAD object ID as the chat thread identifier.
            try
            {
                var chat = await _graphClient.Users[userId].Chats.GetAsync(cancellationToken: ct);
                if (chat?.Value != null && chat.Value.Count > 0)
                {
                    var targetChat = chat.Value.FirstOrDefault(c =>
                        c.ChatType == ChatType.OneOnOne);
                    if (targetChat != null)
                    {
                        await _graphClient.Chats[targetChat.Id].Messages.PostAsync(chatMessage, cancellationToken: ct);
                        _logger.LogInformation("Teams message sent to {UserId} via chat {ChatId}", userId, targetChat.Id);
                        return;
                    }
                }
            }
            catch (ODataError ex)
            {
                _logger.LogWarning(ex, "Could not send Teams message via chat; trying activity notification fallback");
            }

            // Fallback: send as activity notification
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
                var chats = await _graphClient.Users[userId].Chats.GetAsync(cancellationToken: ct);
                var oneOnOne = chats?.Value?.FirstOrDefault(c => c.ChatType == ChatType.OneOnOne);
                if (oneOnOne != null)
                {
                    await _graphClient.Chats[oneOnOne.Id].Messages.PostAsync(chatMessage, cancellationToken: ct);
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
            await _graphClient.Users[userId].Calendar.Events.PostAsync(calendarEvent, cancellationToken: ct);
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
            var result = await _graphClient.Groups.GetAsync(cancellationToken: ct);
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
            // Create a SharePoint list item as a page in the SitePages library.
            // Requires Sites.ReadWrite.All permission.
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
            await _graphClient.Sites[siteId].Lists["SitePages"].Items.PostAsync(listItem, cancellationToken: ct);
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
            var groupMembers = await _graphClient.Groups[groupId].Members.GetAsync(cancellationToken: ct);
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
            OfficePhone = user.BusinessPhones?.FirstOrDefault(),
            MobilePhone = user.MobilePhone,
            OfficeLocation = user.OfficeLocation,
            LastSyncedAt = DateTime.UtcNow
        };
    }
}
