using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OnCallApi.Authorization;
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

    /// <summary>
    /// Every application setting, with sensitive values withheld.
    ///
    /// This endpoint is open to Schedule.Read — most of the directory — because what it was
    /// built for is the default rotation and the dial-plan prefix. Anything secret written into
    /// the same bag would be handed to all of them: the Graph delta link was, until it moved to
    /// its own table. Sensitive keys now come back with a null value and a flag saying why.
    ///
    /// Projected into a response record rather than blanked on the entity, because mutating a
    /// tracked entity to hide a field is one SaveChanges away from saving the mask over the
    /// real value.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<AppSettingResponse>>> GetAll()
    {
        var settings = await _db.AppSettings.AsNoTracking().OrderBy(s => s.Key).ToListAsync();
        return settings.Select(s => AppSettingResponse.From(s)).ToList();
    }

    /// <summary>
    /// One setting by key.
    ///
    /// A sensitive key is 404 rather than 403 below full admin: whether a particular secret is
    /// configured here is itself something not to confirm.
    /// </summary>
    [HttpGet("{key}")]
    public async Task<ActionResult<AppSettingResponse>> Get(string key)
    {
        var setting = await _db.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key);
        if (setting == null) return NotFound();

        if (AppSettingSensitivity.IsSensitive(setting.Key)
            && !User.HasClaim(Permissions.ClaimType, Permissions.AdminFull))
        {
            return NotFound();
        }

        return AppSettingResponse.From(setting, reveal: true);
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

    /// <summary>
    /// Remove a setting, so whatever the code falls back to applies again.
    ///
    /// Upsert could write a value but nothing could take one away, so a setting written by
    /// mistake could only be overwritten with another value — never returned to the built-in
    /// default, which is what an absent row means.
    /// </summary>
    [HttpDelete("{key}")]
    [Authorize(Policy = "RequireAdminFull")]
    public async Task<IActionResult> Delete(string key)
    {
        var existing = await _db.AppSettings.FindAsync(key);
        if (existing == null) return NotFound();

        _db.AppSettings.Remove(existing);
        await _db.SaveChangesAsync();
        return NoContent();
    }
}

public record UpsertSettingRequest(
    [Required] string Value,
    string? Description
);

/// <summary>
/// A setting as it leaves the API. <see cref="Value"/> is null for a sensitive key, with
/// <see cref="IsSensitive"/> saying that is deliberate rather than an empty setting.
/// </summary>
public record AppSettingResponse(
    string Key,
    string? Value,
    string? Description,
    DateTime UpdatedAt,
    bool IsSensitive)
{
    /// <summary>
    /// <paramref name="reveal"/> is for the single-key read by a full admin, who is allowed the
    /// value. The list never reveals: it goes to every schedule reader.
    /// </summary>
    public static AppSettingResponse From(AppSetting setting, bool reveal = false)
    {
        var sensitive = AppSettingSensitivity.IsSensitive(setting.Key);
        return new AppSettingResponse(
            setting.Key,
            sensitive && !reveal ? null : setting.Value,
            setting.Description,
            setting.UpdatedAt,
            sensitive);
    }
}
