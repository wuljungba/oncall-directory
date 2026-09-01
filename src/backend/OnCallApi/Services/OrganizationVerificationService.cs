using Microsoft.EntityFrameworkCore;
using OnCallApi.Data;
using OnCallApi.Models;

namespace OnCallApi.Services;

/// <summary>
/// Decides whether an organization is who it says it is, and records how that was
/// decided.
///
/// The judgement is deliberately narrow. Two things are checked, both against sources the
/// applicant does not control: the NPI against the public CMS registry, and the
/// representative's email domain against the organization's own. Everything else is
/// collected for the record and for a human to weigh.
///
/// A check that cannot be completed sends the submission to a person. It never rejects:
/// a registry outage is not evidence that a hospital does not exist, and a wrongly
/// rejected customer is a customer whose on-call schedule stops working.
/// </summary>
public class OrganizationVerificationService
{
    private readonly AppDbContext _db;
    private readonly INppesRegistryClient _registry;
    private readonly IAuditService? _audit;
    private readonly ILogger<OrganizationVerificationService> _logger;

    public OrganizationVerificationService(
        AppDbContext db,
        INppesRegistryClient registry,
        ILogger<OrganizationVerificationService> logger,
        IAuditService? audit = null)
    {
        _db = db;
        _registry = registry;
        _logger = logger;
        _audit = audit;
    }

    /// <summary>
    /// Records a submission and checks what can be checked.
    /// </summary>
    public async Task<(OrganizationVerification? Verification, string? Error)> SubmitAsync(
        int tenantId, SubmitVerificationRequest request, string? submittedByName,
        CancellationToken ct = default)
    {
        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, ct);
        if (tenant == null) return (null, "Subscription not found.");

        var organizationType = request.OrganizationType?.Trim();
        if (!OrganizationType.IsKnown(organizationType))
            return (null, "Choose the kind of organization this is.");

        var legalName = request.LegalName?.Trim();
        if (string.IsNullOrWhiteSpace(legalName))
            return (null, "The organization's legal name is required.");

        // Declaring a non-healthcare type is a valid answer, and it ends the process
        // rather than starting it. Somebody managing a contact list has nothing to verify,
        // and putting them through this would block the import path for no gain.
        if (!OrganizationType.RequiresVerification(organizationType))
        {
            tenant.OrganizationType = organizationType;
            tenant.VerificationStatus = VerificationStatus.Verified;
            await _db.SaveChangesAsync(ct);

            Audit(tenant, "Verified", submittedByName,
                $"Declared as '{organizationType}', which does not require verification.");

            return (null, null);
        }

        var existing = await _db.OrganizationVerifications
            .FirstOrDefaultAsync(v => v.TenantId == tenantId, ct);

        var verification = existing ?? new OrganizationVerification { TenantId = tenantId };

        verification.LegalName = legalName;
        verification.DoingBusinessAs = Trim(request.DoingBusinessAs);
        verification.Npi = request.Npi == null
            ? null
            : new string(request.Npi.Where(char.IsDigit).ToArray());
        verification.AddressLine1 = Trim(request.AddressLine1);
        verification.City = Trim(request.City);
        verification.State = Trim(request.State);
        verification.PostalCode = Trim(request.PostalCode);
        verification.StateLicenseNumber = Trim(request.StateLicenseNumber);
        verification.LicenseState = Trim(request.LicenseState);
        verification.Ein = Trim(request.Ein);
        verification.RepresentativeName = Trim(request.RepresentativeName);
        verification.RepresentativeTitle = Trim(request.RepresentativeTitle);
        verification.RepresentativeEmail = Trim(request.RepresentativeEmail)?.ToLowerInvariant();
        verification.SubmittedByName = submittedByName;
        verification.SubmittedAt = DateTime.UtcNow;
        verification.DecidedAt = null;
        verification.DecidedByName = null;
        verification.DecisionReason = null;

        if (existing == null) _db.OrganizationVerifications.Add(verification);

        tenant.OrganizationType = organizationType;

        var (status, findings) = await AssessAsync(verification, ct);

        verification.RegistryFindings = findings;
        verification.RegistryCheckedAt = DateTime.UtcNow;
        tenant.VerificationStatus = status;

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Verification submitted for tenant {TenantId}: {Status}", tenantId, status);

        Audit(tenant, status, submittedByName, findings);

        return (verification, null);
    }

    /// <summary>
    /// What the checkable facts say. Returns the status and a sentence explaining it.
    /// </summary>
    private async Task<(string Status, string Findings)> AssessAsync(
        OrganizationVerification verification, CancellationToken ct)
    {
        var notes = new List<string>();
        var blocking = false;

        // ── The representative's email domain ──
        var domainNote = CheckRepresentativeDomain(verification);
        notes.Add(domainNote.Note);
        if (!domainNote.Passed) blocking = true;

        // ── The NPI ──
        if (string.IsNullOrWhiteSpace(verification.Npi))
        {
            notes.Add("No NPI was given, so nothing could be checked against the public registry.");
            blocking = true;
        }
        else
        {
            var lookup = await _registry.LookupAsync(verification.Npi, ct);

            if (!lookup.Reachable)
            {
                notes.Add($"The NPI could not be checked: {lookup.Message} This needs a second look.");
                blocking = true;
            }
            else if (!lookup.Found)
            {
                notes.Add(lookup.Message ?? "No organization is registered under that NPI.");
                blocking = true;
            }
            else
            {
                notes.Add($"NPI {verification.Npi} is registered to '{lookup.LegalName}'.");

                if (!NamesAgree(verification.LegalName, lookup.LegalName))
                {
                    notes.Add(
                        $"That does not match the legal name given ('{verification.LegalName}').");
                    blocking = true;
                }

                if (!string.IsNullOrWhiteSpace(verification.State)
                    && !string.IsNullOrWhiteSpace(lookup.State)
                    && !string.Equals(verification.State.Trim(), lookup.State.Trim(),
                        StringComparison.OrdinalIgnoreCase))
                {
                    notes.Add(
                        $"The registry has it in {lookup.State}, not {verification.State}.");
                    blocking = true;
                }

                // A Type 2 NPI is the organizational one. A Type 1 belongs to an
                // individual clinician, and is not evidence that an organization exists.
                if (lookup.OrganizationType == "NPI-1")
                {
                    notes.Add("That NPI belongs to an individual, not an organization. "
                        + "An organizational (Type 2) NPI is needed.");
                    blocking = true;
                }
            }
        }

        return (blocking ? VerificationStatus.Pending : VerificationStatus.Verified,
                string.Join(" ", notes));
    }

    /// <summary>
    /// A representative must be reachable at the organization's own domain.
    ///
    /// It is a weak signal on its own and a very strong one when absent: a gmail.com
    /// address for "St. Elsewhere Hospital" is the cheapest evidence there is that nobody
    /// has checked anything.
    /// </summary>
    private static (bool Passed, string Note) CheckRepresentativeDomain(
        OrganizationVerification verification)
    {
        var email = verification.RepresentativeEmail;
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
            return (false, "No work email was given for the authorized representative.");

        var domain = email[(email.LastIndexOf('@') + 1)..].Trim().ToLowerInvariant();
        if (domain.Length == 0)
            return (false, "The representative's email address is not usable.");

        if (FreeEmailDomains.Contains(domain))
        {
            return (false,
                $"The representative's address is on {domain}, which is a personal email "
                + "provider rather than the organization's own domain.");
        }

        // The domain's own words, compared against the organization's. "stelsewhere.org"
        // against "St. Elsewhere Hospital" agrees; "acme-widgets.com" does not.
        var organizationWords = Words(verification.LegalName)
            .Concat(Words(verification.DoingBusinessAs ?? string.Empty))
            .Where(w => w.Length > 2)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var domainRoot = domain.Split('.')[0];
        var compactOrganization = new string(
            verification.LegalName.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

        var agrees = organizationWords.Any(w => domainRoot.Contains(w, StringComparison.OrdinalIgnoreCase))
                     || compactOrganization.Contains(domainRoot, StringComparison.OrdinalIgnoreCase)
                     || domainRoot.Length >= 4 && compactOrganization.StartsWith(
                         domainRoot[..Math.Min(4, domainRoot.Length)], StringComparison.OrdinalIgnoreCase);

        return agrees
            ? (true, $"The representative's address is on {domain}, which matches the organization.")
            : (false, $"The representative's address is on {domain}, which does not obviously "
                      + "belong to this organization.");
    }

    /// <summary>
    /// Personal email providers. Not exhaustive, and does not need to be: anything not
    /// listed still has to agree with the organization's name.
    /// </summary>
    private static readonly HashSet<string> FreeEmailDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "gmail.com", "googlemail.com", "yahoo.com", "ymail.com", "hotmail.com", "outlook.com",
        "live.com", "msn.com", "aol.com", "icloud.com", "me.com", "mac.com", "proton.me",
        "protonmail.com", "gmx.com", "mail.com", "zoho.com", "yandex.com",
    };

    /// <summary>
    /// Whether two organization names are the same name written differently.
    ///
    /// Registry entries are upper-cased and abbreviated in ways nobody types: "ST
    /// ELSEWHERE HOSPITAL INC" against "St. Elsewhere Hospital, Inc.". Punctuation and
    /// case are dropped, and a containment either way counts -- a stricter comparison
    /// sends every real hospital to a human queue, which is the same as having no
    /// automatic check.
    /// </summary>
    private static bool NamesAgree(string? claimed, string? registered)
    {
        if (string.IsNullOrWhiteSpace(claimed) || string.IsNullOrWhiteSpace(registered))
            return false;

        static string Key(string value) =>
            new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

        var a = Key(claimed);
        var b = Key(registered);
        if (a.Length == 0 || b.Length == 0) return false;

        return a == b || a.Contains(b) || b.Contains(a);
    }

    private static IEnumerable<string> Words(string value) =>
        value.Split([' ', ',', '.', '-', '/'], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => new string(w.Where(char.IsLetterOrDigit).ToArray()))
            .Where(w => w.Length > 0);

    // ── An administrator's decision ──

    /// <summary>
    /// Approves or rejects a submission by hand. This is the escape hatch for everything
    /// the automatic check cannot know: a new facility not yet in the registry, a legal
    /// name that changed last month, a domain that genuinely does not resemble the name.
    /// </summary>
    public async Task<(bool Ok, string? Error)> DecideAsync(
        int tenantId, string status, string? reason, string? decidedByName,
        CancellationToken ct = default)
    {
        if (status != VerificationStatus.Verified && status != VerificationStatus.Rejected)
            return (false, "A decision is either Verified or Rejected.");

        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, ct);
        if (tenant == null) return (false, "Subscription not found.");

        var verification = await _db.OrganizationVerifications
            .FirstOrDefaultAsync(v => v.TenantId == tenantId, ct);

        if (verification != null)
        {
            verification.DecidedAt = DateTime.UtcNow;
            verification.DecidedByName = decidedByName;
            verification.DecisionReason = Trim(reason);
        }

        tenant.VerificationStatus = status;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Tenant {TenantId} verification set to {Status} by {Who}", tenantId, status, decidedByName);

        Audit(tenant, status, decidedByName, reason);

        return (true, null);
    }

    /// <summary>Everything awaiting a person's judgement.</summary>
    public async Task<List<OrganizationVerification>> GetPendingAsync(CancellationToken ct = default) =>
        await _db.OrganizationVerifications
            .Include(v => v.Tenant)
            .Where(v => v.Tenant != null && v.Tenant.VerificationStatus == VerificationStatus.Pending)
            .OrderBy(v => v.SubmittedAt)
            .ToListAsync(ct);

    public async Task<OrganizationVerification?> GetAsync(int tenantId, CancellationToken ct = default) =>
        await _db.OrganizationVerifications
            .Include(v => v.Tenant)
            .FirstOrDefaultAsync(v => v.TenantId == tenantId, ct);

    // ── Internals ──

    private static string? Trim(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Who decided what, when, and why. Verification decides whether an organization can
    /// publish schedules and fire code calls, so every change to it is worth a record.
    /// </summary>
    private void Audit(Tenant tenant, string status, string? who, string? detail)
    {
        _audit?.Enqueue(new AuditLog
        {
            UserName = who ?? "(system)",
            Action = "Verification",
            ResourceType = "Tenant",
            ResourceId = tenant.Id.ToString(),
            TenantId = tenant.Id,
            Details = $"Verification status set to {status}. {detail}".Trim(),
            Timestamp = DateTime.UtcNow,
        });
    }
}
