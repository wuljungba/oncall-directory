using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OnCallApi.Data;
using OnCallApi.Models;

namespace OnCallApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "RequireScheduleRead")]
public class SettingsController : ControllerBase
{
    private readonly AppDbContext _db;

    public SettingsController(AppDbContext db) => _db = db;

    /// <summary>Get all application settings.</summary>
    [HttpGet]
    public async Task<ActionResult<List<AppSetting>>> GetAll()
    {
        return await _db.AppSettings.OrderBy(s => s.Key).ToListAsync();
    }

    /// <summary>Get a single setting by key.</summary>
    [HttpGet("{key}")]
    public async Task<ActionResult<AppSetting>> Get(string key)
    {
        var setting = await _db.AppSettings.FindAsync(key);
        if (setting == null) return NotFound();
        return setting;
    }

    /// <summary>Create or update a setting.</summary>
    [HttpPut("{key}")]
    [Authorize(Policy = "RequireAdminFull")]
    public async Task<ActionResult<AppSetting>> Upsert(string key, [FromBody] UpsertSettingRequest request)
    {
        var existing = await _db.AppSettings.FindAsync(key);

        if (existing != null)
        {
            existing.Value = request.Value;
            existing.Description = request.Description;
            existing.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            existing = new AppSetting
            {
                Key = key,
                Value = request.Value,
                Description = request.Description,
                UpdatedAt = DateTime.UtcNow
            };
            _db.AppSettings.Add(existing);
        }

        await _db.SaveChangesAsync();
        return existing;
    }
}

public record UpsertSettingRequest(
    [Required] string Value,
    string? Description
);
