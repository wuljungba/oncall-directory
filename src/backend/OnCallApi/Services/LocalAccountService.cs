using Microsoft.EntityFrameworkCore;
using OnCallApi.Authentication;
using OnCallApi.Authorization;
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
    private readonly ILogger<LocalAccountService> _logger;

    public LocalAccountService(
        AppDbContext db,
        LocalJwtService jwtService,
        ILogger<LocalAccountService> logger)
    {
        _db = db;
        _jwtService = jwtService;
        _logger = logger;
    }

    public async Task<LocalAccount> RegisterAsync(string email, string password, string displayName, string[]? roles = null, Guid? employeeId = null)
    {
        // Check for existing account
        var existing = await _db.LocalAccounts.AnyAsync(a => a.Email == email);
        if (existing)
            throw new InvalidOperationException("An account with this email already exists.");

        // Link to an existing directory Employee by email so the local account can be
        // assigned on-call shifts and appear in the directory. An explicit employeeId
        // (from the admin dashboard) always wins.
        if (employeeId == null)
        {
            var matched = await _db.Employees
                .FirstOrDefaultAsync(e => e.Email.ToLower() == email.ToLowerInvariant().Trim());
            if (matched != null) employeeId = matched.Id;
        }

        var account = new LocalAccount
        {
            Email = email.ToLowerInvariant().Trim(),
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

        if (!BCrypt.Net.BCrypt.Verify(password, account.PasswordHash))
        {
            _logger.LogWarning("Local login failed: invalid password for {Email}", email);
            return (null, null);
        }

        // Generate JWT
        var token = _jwtService.GenerateToken(
            account.Id,
            account.Email,
            account.DisplayName,
            account.Roles,
            account.EmployeeId);

        // Update last login
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
