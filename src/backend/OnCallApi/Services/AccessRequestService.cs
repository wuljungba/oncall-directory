using System.Net.Mail;
using Microsoft.EntityFrameworkCore;
using OnCallApi.Data;
using OnCallApi.Models;

namespace OnCallApi.Services;

public interface IAccessRequestService
{
    /// <summary>
    /// Records an access request. Returns false only when the submission itself is
    /// unusable (no address, or one that is not an email); a repeat request from someone
    /// who already has one open is treated as success, because the caller must not be able
    /// to learn from the response whether an address is already known here.
    /// </summary>
    Task<bool> SubmitAsync(SubmitAccessRequest request, CancellationToken ct = default);

    Task<List<AccessRequest>> ListAsync(string? status, CancellationToken ct = default);

    Task<AccessRequest> ReviewAsync(int id, bool approved, string? reviewerName, string? note, CancellationToken ct = default);
}

public class AccessRequestService : IAccessRequestService
{
    private readonly AppDbContext _db;
    private readonly ILogger<AccessRequestService> _logger;

    public AccessRequestService(AppDbContext db, ILogger<AccessRequestService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<bool> SubmitAsync(SubmitAccessRequest request, CancellationToken ct = default)
    {
        var email = request.Email?.Trim();
        if (string.IsNullOrEmpty(email) || email.Length > 320 || !LooksLikeEmail(email))
            return false;

        // One open request per address. Someone who submits twice — impatient, or probing
        // — updates their own pending row rather than filling the admin queue with
        // duplicates, and the endpoint answers identically either way.
        var existing = await _db.AccessRequests
            .FirstOrDefaultAsync(r => r.Email == email && r.Status == AccessRequestStatus.Pending, ct);

        if (existing != null)
        {
            existing.FullName = Clamp(request.FullName, 200) ?? existing.FullName;
            existing.Organization = Clamp(request.Organization, 200) ?? existing.Organization;
            existing.RoleRequested = Clamp(request.RoleRequested, 200) ?? existing.RoleRequested;
            existing.Note = Clamp(request.Note, 1000) ?? existing.Note;
            existing.CreatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            _logger.LogInformation("Access request {Id} resubmitted", existing.Id);
            return true;
        }

        var entry = new AccessRequest
        {
            Email = email,
            FullName = Clamp(request.FullName, 200),
            Organization = Clamp(request.Organization, 200),
            RoleRequested = Clamp(request.RoleRequested, 200),
            Note = Clamp(request.Note, 1000),
            Status = AccessRequestStatus.Pending,
            CreatedAt = DateTime.UtcNow,
        };

        _db.AccessRequests.Add(entry);
        await _db.SaveChangesAsync(ct);

        // The address and the free-text note are what a stranger typed, so neither goes to
        // the log stream. The row id is enough to find the request in the admin queue.
        _logger.LogInformation("Access request {Id} received", entry.Id);
        return true;
    }

    public async Task<List<AccessRequest>> ListAsync(string? status, CancellationToken ct = default)
    {
        var query = _db.AccessRequests.AsQueryable();

        if (!string.IsNullOrWhiteSpace(status) && !string.Equals(status, "all", StringComparison.OrdinalIgnoreCase))
        {
            var wanted = status.Trim().ToLowerInvariant();
            if (!AccessRequestStatus.IsKnown(wanted))
                throw new InvalidOperationException($"Unknown status '{status}'.");
            query = query.Where(r => r.Status == wanted);
        }

        return await query
            .OrderBy(r => r.Status == AccessRequestStatus.Pending ? 0 : 1)
            .ThenByDescending(r => r.CreatedAt)
            .Take(500)
            .ToListAsync(ct);
    }

    public async Task<AccessRequest> ReviewAsync(
        int id, bool approved, string? reviewerName, string? note, CancellationToken ct = default)
    {
        var entry = await _db.AccessRequests.FirstOrDefaultAsync(r => r.Id == id, ct)
            ?? throw new KeyNotFoundException($"Access request {id} not found");

        if (entry.Status != AccessRequestStatus.Pending)
            throw new InvalidOperationException("That request has already been reviewed.");

        entry.Status = approved ? AccessRequestStatus.Approved : AccessRequestStatus.Denied;
        entry.ReviewedAt = DateTime.UtcNow;
        entry.ReviewedByName = Clamp(reviewerName, 200);
        entry.ReviewNote = Clamp(note, 1000);

        await _db.SaveChangesAsync(ct);

        // Worth stating plainly in the log: approving is a triage decision, not a grant.
        // The permissions are still assigned by hand on the Permissions screen.
        _logger.LogInformation(
            "Access request {Id} marked {Status} by {Reviewer} — no permissions were granted by this action",
            entry.Id, entry.Status, entry.ReviewedByName ?? "an unidentified admin");

        return entry;
    }

    private static string? Clamp(string? value, int max)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }

    private static bool LooksLikeEmail(string value)
    {
        // MailAddress is the same check the rest of the platform applies; it is a sanity
        // filter on an anonymous field, not proof the address exists.
        try
        {
            var parsed = new MailAddress(value);
            return parsed.Address == value && value.Contains('.', StringComparison.Ordinal);
        }
        catch (FormatException) { return false; }
    }
}
