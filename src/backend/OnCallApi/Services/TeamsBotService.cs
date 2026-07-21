using Microsoft.EntityFrameworkCore;
using OnCallApi.Data;

namespace OnCallApi.Services;

/// <summary>
/// Handles incoming Teams bot messages for schedule lookups.
/// Supports natural language queries like "Who's on call?" and "Am I on call tomorrow?"
/// This service is called from a Teams messaging extension or bot webhook.
/// </summary>
public class TeamsBotService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<TeamsBotService> _logger;

    public TeamsBotService(IServiceProvider services, ILogger<TeamsBotService> logger)
    {
        _services = services;
        _logger = logger;
    }

    /// <summary>
    /// Process an incoming message and return a response.
    /// </summary>
    public async Task<string> HandleMessageAsync(string message, string userAzureAdId)
    {
        var msg = message.ToLower().Trim();

        if (msg.Contains("who") && msg.Contains("call"))
            return await GetOnCallNowAsync(msg);

        if (msg.Contains("am i") && msg.Contains("call"))
            return await GetMyOnCallStatusAsync(userAzureAdId, msg);

        if (msg.Contains("swap") || msg.Contains("switch"))
            return "To request a swap, open the schedule in the OnCall web app and click the swap icon on your shift.";

        if (msg.Contains("next") && msg.Contains("shift"))
            return await GetNextShiftAsync(userAzureAdId);

        if (msg.Contains("help") || msg.Contains("what can"))
            return "I can answer:\n• \"Who's on call?\" — current on-call staff\n• \"Am I on call?\" — your status\n• \"Am I on call tomorrow?\" — check specific day\n• \"When's my next shift?\" — upcoming schedule\n• \"How do I swap?\" — swap instructions";

        return "I'm not sure how to help with that. Try \"Who's on call?\" or \"Help\" for available commands.";
    }

    private async Task<string> GetOnCallNowAsync(string query)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var now = DateTime.UtcNow;
        var onCall = await db.Shifts
            .Include(s => s.Employee)
            .Include(s => s.Schedule).ThenInclude(s => s.Department)
            .Where(s => s.StartTime <= now && s.EndTime >= now && s.Status != "gap" && s.Employee != null)
            .OrderBy(s => s.Tier)
            .ToListAsync();

        if (!onCall.Any())
            return "No one is currently on call.";

        var lines = onCall.Select(s =>
            $"• {s.Employee?.FirstName} {s.Employee?.LastName} ({s.Tier}) — {s.Schedule?.Department?.Name ?? "General"} — until {s.EndTime.ToLocalTime():h:mm tt}");
        return $"**Currently On Call:**\n{string.Join("\n", lines)}";
    }

    private async Task<string> GetMyOnCallStatusAsync(string azureAdObjectId, string query)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var employee = await db.Employees.FirstOrDefaultAsync(e => e.AzureAdObjectId == azureAdObjectId);
        if (employee == null) return "I couldn't find your profile. Have you been synced from Active Directory?";

        // Check for "tomorrow" keyword
        var checkDate = query.Contains("tomorrow") ? DateTime.UtcNow.Date.AddDays(1) : DateTime.UtcNow.Date;

        var shift = await db.Shifts
            .Include(s => s.Schedule)
            .FirstOrDefaultAsync(s =>
                s.EmployeeId == employee.Id &&
                s.StartTime <= checkDate.AddDays(1) &&
                s.EndTime >= checkDate &&
                s.Status != "gap");

        if (shift == null)
        {
            var dayStr = query.Contains("tomorrow") ? "tomorrow" : "right now";
            return $"You're not on call {dayStr}, {employee.FirstName}. Enjoy your time off! 🎉";
        }

        return $"Yes, you're on call! {shift.Tier} shift from {shift.StartTime.ToLocalTime():h:mm tt} to {shift.EndTime.ToLocalTime():h:mm tt}.";
    }

    private async Task<string> GetNextShiftAsync(string azureAdObjectId)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var employee = await db.Employees.FirstOrDefaultAsync(e => e.AzureAdObjectId == azureAdObjectId);
        if (employee == null) return "I couldn't find your profile.";

        var nextShift = await db.Shifts
            .Include(s => s.Schedule).ThenInclude(s => s.Department)
            .Where(s => s.EmployeeId == employee.Id && s.StartTime > DateTime.UtcNow && s.Status != "gap")
            .OrderBy(s => s.StartTime)
            .FirstOrDefaultAsync();

        if (nextShift == null)
            return "You have no upcoming shifts scheduled.";

        return $"Your next shift: {nextShift.Tier} on {nextShift.StartTime.ToLocalTime():dddd, MMM d} at {nextShift.StartTime.ToLocalTime():h:mm tt} ({nextShift.Schedule?.Department?.Name ?? "General"})";
    }
}
