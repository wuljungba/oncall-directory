using System.Net.Mail;
using System.Text.Json;
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

    /// <summary>
    /// The queue, narrowed to what the caller may see.
    ///
    /// <paramref name="visibleTenantIds"/> null means no narrowing — an admin who can see every
    /// subscription anyway. A list narrows to requests attributed to those subscriptions, and
    /// requests nobody could attribute are NOT in it: "whose is this?" is unanswered, and an
    /// unanswered question is not an invitation to show it to everybody.
    /// </summary>
    Task<List<AccessRequest>> ListAsync(string? status, List<int>? visibleTenantIds, CancellationToken ct = default);

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

        var (tenantId, matchedDomain) = await AttributeAsync(email, ct);

        if (existing != null)
        {
            existing.FullName = Clamp(request.FullName, 200) ?? existing.FullName;
            existing.Organization = Clamp(request.Organization, 200) ?? existing.Organization;
            existing.RoleRequested = Clamp(request.RoleRequested, 200) ?? existing.RoleRequested;
            existing.Note = Clamp(request.Note, 1000) ?? existing.Note;
            // Re-attributed on every submission: a directory that connected since the first
            // one now answers a question that had no answer then.
            existing.TenantId = tenantId;
            existing.MatchedDomain = matchedDomain;
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
            TenantId = tenantId,
            MatchedDomain = matchedDomain,
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

    public async Task<List<AccessRequest>> ListAsync(
        string? status, List<int>? visibleTenantIds, CancellationToken ct = default)
    {
        var query = _db.AccessRequests.AsQueryable();

        if (visibleTenantIds != null)
        {
            // Fails closed on purpose. An admin scoped to one customer sees requests from that
            // customer's own directory and nothing else — not other customers', and not the
            // ones nothing could attribute, which stay with the operators who see everything.
            query = query.Where(r => r.TenantId != null && visibleTenantIds.Contains(r.TenantId.Value));
        }

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

    /// <summary>
    /// Which subscription an address belongs to, judged by its domain against the verified
    /// domains of a directory OnCall has actually read.
    ///
    /// Deliberately not the Organization field: that is free text a stranger typed about
    /// themselves, and taking it as attribution would let anyone put their request into any
    /// customer's queue by naming them. Only <see cref="Tenant.DirectoryDomains"/> counts, and
    /// only once <see cref="Tenant.DirectoryVerifiedAt"/> says a live read confirmed the
    /// directory — the same evidence standard the consent callback applies.
    ///
    /// The result is null far more often than not, which is correct: unattributed is the safe
    /// state, and it narrows who can see the request rather than widening it.
    /// </summary>
    private async Task<(int? TenantId, string? Domain)> AttributeAsync(string email, CancellationToken ct)
    {
        var at = email.LastIndexOf('@');
        if (at < 0 || at == email.Length - 1) return (null, null);

        var domain = email[(at + 1)..].ToLowerInvariant();

        var candidates = await _db.Tenants
            .Where(t => t.IsActive && t.DirectoryVerifiedAt != null && t.DirectoryDomains != null)
            .Select(t => new { t.Id, t.DirectoryDomains })
            .ToListAsync(ct);

        var matches = candidates
            .Where(c => ParseDomains(c.DirectoryDomains).Contains(domain))
            .Select(c => c.Id)
            .Distinct()
            .ToList();

        // Exactly one, or none at all. Two subscriptions claiming one domain is a
        // misconfiguration, and picking between them would hand a stranger's request — their
        // name, their address, whatever they wrote — to the wrong customer.
        if (matches.Count != 1)
        {
            if (matches.Count > 1)
            {
                _logger.LogWarning(
                    "An access request domain matched {Count} subscriptions; leaving it unattributed",
                    matches.Count);
            }
            return (null, null);
        }

        return (matches[0], domain);
    }

    /// <summary>The stored JSON array, or nothing at all if it cannot be read as one.</summary>
    private static IReadOnlyCollection<string> ParseDomains(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            var parsed = JsonSerializer.Deserialize<List<string>>(json);
            return parsed == null
                ? []
                : parsed.Where(d => !string.IsNullOrWhiteSpace(d))
                        .Select(d => d.Trim().ToLowerInvariant())
                        .ToHashSet();
        }
        catch (JsonException)
        {
            return [];
        }
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
