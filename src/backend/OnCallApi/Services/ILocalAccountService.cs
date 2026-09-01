using OnCallApi.Models;

namespace OnCallApi.Services;

public interface ILocalAccountService
{
    /// <summary>Register a new local account (admin-only).</summary>
    Task<LocalAccount> RegisterAsync(string email, string password, string displayName, string[]? roles = null, Guid? employeeId = null);

    /// <summary>
    /// Creates an account for someone signing themselves up. Grants nothing.
    /// See the implementation for why this is a separate method rather than a flag.
    /// </summary>
    Task<LocalAccount> RegisterSelfServeAsync(string email, string password, string displayName);

    /// <summary>Authenticate a local account by email and password.</summary>
    Task<(LocalAccount? Account, string? Token)> AuthenticateAsync(string email, string password);

    /// <summary>Get a local account by ID.</summary>
    Task<LocalAccount?> GetByIdAsync(int id);

    /// <summary>Get a local account by email.</summary>
    Task<LocalAccount?> GetByEmailAsync(string email);

    /// <summary>List all local accounts (with optional active filter).</summary>
    Task<List<LocalAccount>> GetAllAsync(bool includeInactive = false);

    /// <summary>Update a local account's details.</summary>
    Task<LocalAccount> UpdateAsync(int id, string? displayName, bool? isActive, string[]? roles, Guid? employeeId = null);

    /// <summary>Change a local account's password.</summary>
    Task<bool> ChangePasswordAsync(int id, string currentPassword, string newPassword);

    /// <summary>Reset a local account's password (admin-only).</summary>
    Task ResetPasswordAsync(int id, string newPassword);

    /// <summary>Delete (deactivate) a local account.</summary>
    Task DeactivateAsync(int id);
}
