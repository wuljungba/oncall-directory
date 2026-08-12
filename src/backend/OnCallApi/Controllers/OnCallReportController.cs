using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OnCallApi.Data;

namespace OnCallApi.Controllers;

/// <summary>
/// Consolidated on-call audit report: who was on call, when, what tier, shift status,
/// and any code-call/incident events raised during that shift (who triggered, who was
/// notified, outcome). Admin-only. The frontend renders and exports it as CSV.
/// </summary>
[ApiController]
[Route("api/audit")]
[Authorize(Policy = "RequireAdminFull")]
public class OnCallReportController : ControllerBase
{
    private readonly AppDbContext _db;

    public OnCallReportController(AppDbContext db)
    {
        _db = db;
    }

    [HttpGet("on-call-report")]
    public async Task<ActionResult<List<OnCallReportRow>>> GetOnCallReport(
        [FromQuery] DateTime? from, [FromQuery] DateTime? to)
    {
        var fromDate = from ?? DateTime.UtcNow.AddDays(-30);
        var toDate = to ?? DateTime.UtcNow;

        var shifts = await _db.Shifts
            .Include(s => s.Employee)
            .Where(s => s.StartTime >= fromDate && s.StartTime <= toDate && s.Status != "gap")
            .OrderBy(s => s.StartTime)
            .AsNoTracking()
            .ToListAsync();

        var incidents = await _db.PhoneTreeEvents
            .Where(e => e.StartedAt >= fromDate && e.StartedAt <= toDate)
            .AsNoTracking()
            .ToListAsync();

        var rows = shifts.Select(s => new OnCallReportRow
        {
            EmployeeId = s.EmployeeId,
            EmployeeName = s.Employee == null ? "" : $"{s.Employee.FirstName} {s.Employee.LastName}".Trim(),
            Tier = s.Tier ?? "",
            Start = s.StartTime,
            End = s.EndTime,
            Status = s.Status ?? "",
            Incidents = incidents
                .Where(i => i.StartedAt >= s.StartTime && i.StartedAt <= s.EndTime)
                .Select(i => new IncidentSummary
                {
                    Id = i.Id,
                    StartedAt = i.StartedAt,
                    EndedAt = i.EndedAt,
                    RequestedByName = i.RequestedByName ?? "",
                    InitiatedByName = i.InitiatedByName ?? "",
                    NotifiedByName = i.NotifiedByName ?? "",
                    Location = i.Location ?? "",
                    Status = i.Status ?? "",
                    Outcome = i.Outcome ?? "",
                })
                .OrderBy(i => i.StartedAt)
                .ToList(),
        }).ToList();

        return Ok(rows);
    }
}

public class OnCallReportRow
{
    public Guid EmployeeId { get; set; }
    public string EmployeeName { get; set; } = "";
    public string Tier { get; set; } = "";
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public string Status { get; set; } = "";
    public List<IncidentSummary> Incidents { get; set; } = new();
}

public class IncidentSummary
{
    public int Id { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public string RequestedByName { get; set; } = "";
    public string InitiatedByName { get; set; } = "";
    public string NotifiedByName { get; set; } = "";
    public string Location { get; set; } = "";
    public string Status { get; set; } = "";
    public string Outcome { get; set; } = "";
}