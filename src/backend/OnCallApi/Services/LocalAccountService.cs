using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OnCallApi.Authentication;
using OnCallApi.Authorization;
using OnCallApi.Configuration;
using OnCallApi.Data;
using OnCallApi.Models;

namespace OnCallApi.Services;

/// <summary>
/// Service for managing local database accounts.
/// Uses BCrypt for password hashing and LocalJwtService for token generation.
/// </summary>
public class LocalAccountService : ILocalAccountService
{
    private readonly AppDbContext _db;
    private readonly LocalJwtService _jwtService;
    private readonly SuperAdminOptions _superAdmins;
    private readonly ILogger<LocalAccountService> _logger;

    public LocalAccountService(
        AppDbContext db,
        LocalJwtService jwtService,
        IOptions<SuperAdminOptions> superAdmins,
        ILogger<LocalAccountService> logger)
    {
        _db = db;
        _jwtService = jwtService;
        _superAdmins = superAdmins.Value;
        _logger = logger;
    }

    public async Task<LocalAccount> RegisterAsync(string email, string password, string displayName, string[]? roles = null, Guid? employeeId = null)
    {
        // Normalize ONCE. The duplicate check used to compare the raw argument while the row
        // was stored lowercased and trimmed, so "Admin@X.com " sailed past a check against an
        // existing "admin@x.com" and then wrote a colliding row.
        var normalized = email.ToLowerInvariant().Trim();

        // A configured super administrator is identified by EMAIL, and the email on a local
        // account is chosen by whoever creates it and never verified against anything. Without
        // this guard, any Admin.Full holder could register a local account bearing the super
        // admin's address and sign in with Tenant.Manage and SuperAdmin on every tenant —
        // strictly more than they held, with no record of the escalation.
        if (_superAdmins.Emails.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Refused to register a local account for {Email}: reserved for a configured super administrator",
                normalized);
            throw new InvalidOperationException(
                "That address is reserved for a configured super administrator and cannot be "
                + "used for a local account.");
        }

        // Check for existing account
        var existing = await _db.LocalAccounts.AnyAsync(a => a.Email == normalized);
        if (existing)
            throw new InvalidOperationException("An account with this email already exists.");

        // Link to an existing directory Employee by email so the local account can be
        // assigned on-call shifts and appear in the directory. An explicit employeeId
        // (from the admin dashboard) always wins.
        if (employeeId == null)
        {
            // e.Email != null is a security guard, not a null-check ceremony. Email is
            // optional now, and without it a department contact carrying no address could
            // be matched by a blank comparison and silently adopted as the identity behind
            // a sign-in -- handing someone shifts and a directory presence that are not
            // theirs. A row with no address is never anybody.
            var matched = await _db.Employees
                .FirstOrDefaultAsync(e => e.Email != null && e.Email.ToLower() == normalized);
            if (matched != null) employeeId = matched.Id;
        }

        var account = new LocalAccount
        {
            Email = normalized,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password, workFactor: 12),
            DisplayName = displayName,
            Roles = roles ?? ["OnCall.Viewer"],
            EmployeeId = employeeId,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        };

        _db.LocalAccounts.Add(account);
        await _db.SaveChangesAsync();

        // Onboarding standard: every sign-in-capable account gets the staff baseline
        // (Schedule.Read + Directory.Read). Admins can demote/adjust afterward.
        var hasBaseline = await _db.PermissionGrants.AnyAsync(g =>
            g.IsActive && g.ExternalPrincipalId == account.Email && (g.Permissions ?? string.Empty).Contains(Permissions.ScheduleRead));
        if (!hasBaseline)
        {
            _db.PermissionGrants.Add(new PermissionGrant
            {
                PrincipalType = "local",
                ExternalPrincipalId = account.Email,
                LocalUserId = account.Id,
                Permissions = $"{Permissions.ScheduleRead},{Permissions.DirectoryRead}",
                IsActive = true,
            });
            await _db.SaveChangesAsync();
        }

        _logger.LogInformation("Local account registered: {Email} (Id={Id})", account.Email, account.Id);
        return account;
    }

    /// <summary>
    /// Registers someone who signed themselves up.
    ///
    /// A SEPARATE method rather than a flag on RegisterAsync, because almost everything
    /// that one does is wrong here. It links the new account to a directory entry with a
    /// matching address, and it grants the staff baseline. Both are correct when an
    /// administrator creates the account -- somebody has vouched for the person -- and
    /// both are an account takeover when the person creating it is whoever filled in a
    /// public form.
    ///
    /// What this creates is inert: no roles, no permission grant, no directory link.
    /// An administrator provisions it afterwards, exactly as they do today for a
    /// Microsoft or Google sign-in. Signing up proves you can be reached at an address
    /// and nothing else.
    ///
    /// NOTE: there is no email verification here, because this application has no way to
    /// send email -- there is no SMTP client and the Graph registration is read-only.
    /// The protection that replaces it is the claim check below: an address that already
    /// means somebody cannot be taken by a stranger. If mail delivery is added later,
    /// verification belongs here on top, not instead.
    /// </summary>
    public async Task<LocalAccount> RegisterSelfServeAsync(string email, string password, string displayName)
    {
        var normalized = email.ToLowerInvariant().Trim();

        // A configured super administrator is identified by EMAIL, and the address on a
        // local account is never verified against anything.
        if (_superAdmins.Emails.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Refused self-signup for {Email}: reserved for a configured super administrator",
                normalized);
            throw new InvalidOperationException(TakenMessage);
        }

        if (await _db.LocalAccounts.AnyAsync(a => a.Email == normalized))
            throw new InvalidOperationException(TakenMessage);

        // The address already belongs to somebody in the directory. Letting a stranger
        // register it would be bad on its own; it is worse than that, because
        // TenantClaimsMiddleware resolves permission grants BY EMAIL. An administrator
        // later granting access to jane@hospital.example -- meaning the real Jane, who
        // signs in with Microsoft -- would be granting it to whoever had registered that
        // address here first.
        if (await _db.Employees.AnyAsync(e => e.Email != null && e.Email.ToLower() == normalized))
        {
            _logger.LogWarning(
                "Refused self-signup for an address already in the directory (no account created)");
            throw new InvalidOperationException(TakenMessage);
        }

        // Same reasoning, for an address that already carries a grant even though no
        // directory entry uses it.
        if (await _db.PermissionGrants.AnyAsync(g =>
                g.IsActive && g.ExternalPrincipalId.ToLower() == normalized))
        {
            _logger.LogWarning("Refused self-signup for an address that already holds a grant");
            throw new InvalidOperationException(TakenMessage);
        }

        var account = new LocalAccount
        {
            Email = normalized,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password, workFactor: 12),
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? normalized : displayName.Trim(),
            Roles = [],
            EmployeeId = null,
            Origin = LocalAccountOrigin.SelfSignup,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        };

        _db.LocalAccounts.Add(account);
        await _db.SaveChangesAsync();

        _logger.LogInformation(
            "Self-registered local account created with no permissions (Id={Id})", account.Id);

        return account;
    }

    /// <summary>
    /// One message for every reason a signup is refused.
    ///
    /// Saying WHICH reason turns the form into an oracle: a stranger could enumerate which
    /// addresses belong to staff, which hold permissions, and who the super admins are,
    /// one submission at a time.
    /// </summary>
    private const string TakenMessage =
        "That address cannot be used to create an account here. If you work with this "
        + "organization, ask an administrator for access.";

    /// <summary>Failed attempts before an account is locked.</summary>
    private const int MaxFailedLogins = 5;

    /// <summary>How long a locked account stays locked.</summary>
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    public async Task<(LocalAccount? Account, string? Token)> AuthenticateAsync(string email, string password)
    {
        var account = await _db.LocalAccounts.FirstOrDefaultAsync(a => a.Email == email.ToLowerInvariant().Trim());

        if (account == null)
        {
            _logger.LogWarning("Local login failed: account not found for {Email}", email);
            return (null, null);
        }

        if (!account.IsActive)
        {
            _logger.LogWarning("Local login failed: account {Email} is inactive", email);
            return (null, null);
        }

        // Checked BEFORE the password. Verifying first would let an attacker keep
        // guessing against a locked account and, through timing, learn when they got it
        // right -- the lockout has to cost them the attempt, not just the answer.
        if (account.LockedOutUntil is { } until && until > DateTime.UtcNow)
        {
            _logger.LogWarning(
                "Local login refused: account {Id} is locked until {Until:u}", account.Id, until);
            return (null, null);
        }

        if (!BCrypt.Net.BCrypt.Verify(password, account.PasswordHash))
        {
            account.FailedLoginCount++;

            if (account.FailedLoginCount >= MaxFailedLogins)
            {
                account.LockedOutUntil = DateTime.UtcNow.Add(LockoutDuration);
                account.FailedLoginCount = 0;
                _logger.LogWarning(
                    "Local account {Id} locked for {Minutes} minutes after {Count} failed attempts",
                    account.Id, LockoutDuration.TotalMinutes, MaxFailedLogins);
            }
            else
            {
                _logger.LogWarning("Local login failed: invalid password for account {Id}", account.Id);
            }

            await _db.SaveChangesAsync();
            return (null, null);
        }

        // Generate JWT
        var token = _jwtService.GenerateToken(
            account.Id,
            account.Email,
            account.DisplayName,
            account.Roles,
            account.EmployeeId);

        // A successful sign-in clears the count: five wrong attempts spread over a month
        // are somebody mistyping, not somebody guessing.
        account.FailedLoginCount = 0;
        account.LockedOutUntil = null;
        account.LastLoginAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        _logger.LogInformation("Local login successful: {Email}", email);
        return (account, token);
    }

    public async Task<LocalAccount?> GetByIdAsync(int id)
    {
        return await _db.LocalAccounts.FindAsync(id);
    }

    public async Task<LocalAccount?> GetByEmailAsync(string email)
    {
        return await _db.LocalAccounts.FirstOrDefaultAsync(a => a.Email == email.ToLowerInvariant().Trim());
    }

    public async Task<List<LocalAccount>> GetAllAsync(bool includeInactive = false)
    {
        var query = _db.LocalAccounts.AsQueryable();
        if (!includeInactive)
            query = query.Where(a => a.IsActive);
        return await query.OrderBy(a => a.Email).ToListAsync();
    }

    public async Task<LocalAccount> UpdateAsync(int id, string? displayName, bool? isActive, string[]? roles, Guid? employeeId = null)
    {
        var account = await _db.LocalAccounts.FindAsync(id)
            ?? throw new InvalidOperationException("Local account not found.");

        if (displayName != null) account.DisplayName = displayName;
        if (isActive.HasValue) account.IsActive = isActive.Value;
        if (roles != null) account.Roles = roles;
        if (employeeId.HasValue) account.EmployeeId = employeeId == Guid.Empty ? null : employeeId;

        await _db.SaveChangesAsync();
        _logger.LogInformation("Local account updated: {Email} (Id={Id})", account.Email, account.Id);
        return account;
    }

    public async Task<bool> ChangePasswordAsync(int id, string currentPassword, string newPassword)
    {
        var account = await _db.LocalAccounts.FindAsync(id);
        if (account == null) return false;

        if (!BCrypt.Net.BCrypt.Verify(currentPassword, account.PasswordHash))
            return false;

        account.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword, workFactor: 12);
        await _db.SaveChangesAsync();
        _logger.LogInformation("Password changed for local account: {Email}", account.Email);
        return true;
    }

    public async Task ResetPasswordAsync(int id, string newPassword)
    {
        var account = await _db.LocalAccounts.FindAsync(id)
            ?? throw new InvalidOperationException("Local account not found.");

        account.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword, workFactor: 12);
        await _db.SaveChangesAsync();
        _logger.LogInformation("Password reset for local account: {Email} (admin initiated)", account.Email);
    }

    public async Task DeactivateAsync(int id)
    {
        var account = await _db.LocalAccounts.FindAsync(id)
            ?? throw new InvalidOperationException("Local account not found.");

        account.IsActive = false;
        await _db.SaveChangesAsync();
        _logger.LogInformation("Local account deactivated: {Email}", account.Email);
    }
}
