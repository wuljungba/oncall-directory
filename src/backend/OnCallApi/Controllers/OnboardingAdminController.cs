using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OnCallApi.Data;

namespace OnCallApi.Controllers;

/// <summary>
/// Admin health view for the onboarding standard (see docs/onboarding-standard.md).
/// Flags employee records that are missing a Source classification, a sign-in
/// identity, or the baseline permission — so "is everyone onboarded correctly?"
/// is one call instead of a manual audit.
/// </summary>
[ApiController]
[Route("api/admin/onboarding")]
[Authorize(Policy = "RequireAdminFullOrTenantManage")]
public class OnboardingAdminController : ControllerBase
{
    private readonly AppDbContext _db;

    public OnboardingAdminController(AppDbContext db)
    {
        _db = db;
    }

    [HttpGet("health")]
    public async Task<ActionResult<OnboardingHealthResponse>> GetHealth()
    {
        var employees = await _db.Employees
            .AsNoTracking()
            .OrderBy(e => e.LastName).ThenBy(e => e.FirstName)
            .ToListAsync();
        var localAccounts = await _db.LocalAccounts.AsNoTracking().ToListAsync();
        var grants = await _db.PermissionGrants
            .Where(g => g.IsActive)
            .AsNoTracking()
            .ToListAsync();

        var issues = new List<OnboardingIssue>();
        var total = 0;
        var bySource = new Dictionary<string, int>();

        foreach (var emp in employees)
        {
            total++;
            var source = string.IsNullOrWhiteSpace(emp.Source) ? "(none)" : emp.Source!;
            bySource[source] = bySource.GetValueOrDefault(source) + 1;

            var problems = new List<string>();

            if (string.IsNullOrWhiteSpace(emp.Source))
                problems.Add("Missing Source classification");

            var hasIdentity = !string.IsNullOrEmpty(emp.AzureAdObjectId)
                || localAccounts.Any(l => l.EmployeeId == emp.Id || string.Equals(l.Email, emp.Email, StringComparison.OrdinalIgnoreCase));

            var directoryOnly = emp.Source == "CsvImport" && !hasIdentity;

            // Anyone who isn't a pure directory-only import needs a sign-in identity.
            if (!directoryOnly && !hasIdentity)
                problems.Add("No sign-in identity (no Entra object id and no local account)");

            // Sign-in-capable people need the baseline schedule/directory permission.
            if (hasIdentity)
            {
                var hasScheduleRead = grants.Any(g =>
                    (string.Equals(g.ExternalPrincipalId, emp.Email, StringComparison.OrdinalIgnoreCase)
                     || string.Equals(g.ExternalPrincipalId, emp.AzureAdObjectId, StringComparison.OrdinalIgnoreCase))
                    && (g.Permissions ?? string.Empty).Contains("Schedule.Read"));

                if (!hasScheduleRead)
                    problems.Add("Missing baseline permission (Schedule.Read)");
            }

            if (problems.Count > 0)
            {
                issues.Add(new OnboardingIssue
                {
                    Id = emp.Id,
                    Name = $"{emp.FirstName} {emp.LastName}".Trim(),
                    Email = emp.Email,
                    Source = source,
                    IsActive = emp.IsActive,
                    Problems = problems,
                });
            }
        }

        return Ok(new OnboardingHealthResponse
        {
            Total = total,
            WithIssues = issues.Count,
            BySource = bySource,
            Issues = issues,
        });
    }
}

public class OnboardingHealthResponse
{
    public int Total { get; set; }
    public int WithIssues { get; set; }
    public Dictionary<string, int> BySource { get; set; } = new();
    public List<OnboardingIssue> Issues { get; set; } = new();
}

public class OnboardingIssue
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string Source { get; set; } = "";
    public bool IsActive { get; set; }
    public List<string> Problems { get; set; } = new();
}