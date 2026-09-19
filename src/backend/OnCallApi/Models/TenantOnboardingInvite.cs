namespace OnCallApi.Models;

/// <summary>
/// A one-time link that ties a customer's admin consent to a specific subscription.
///
/// Registration used to mean an operator typing the customer's Entra tenant GUID into a form.
/// A typo there is silent — a malformed id simply never matches anyone's token, so the
/// subscription looks connected and nobody can sign in. Meanwhile the consent redirect has been
/// carrying the real tenant id all along, unused, because a tenant id in a redirect is
/// attacker-supplied and cannot be trusted on its own.
///
/// This is the missing half. The token travels as the OAuth <c>state</c> parameter, so a consent
/// that comes back proves WHICH subscription was being onboarded; a live Graph read against the
/// returned directory then proves the consent was real. Neither is sufficient alone.
///
/// Single-use and expiring, and the token is stored as issued — the same shape
/// <see cref="PublicShare"/> uses, and reading it requires the Tenant.Manage that could mint a
/// new one anyway.
/// </summary>
public class TenantOnboardingInvite
{
    public int Id { get; set; }

    public int TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    /// <summary>The value carried in the consent link's <c>state</c>.</summary>
    public Guid Token { get; set; } = Guid.NewGuid();

    /// <summary>Who issued it, so a connection can be traced back to the person who invited it.</summary>
    [MaxLength(200)]
    public string? CreatedBy { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Onboarding is a conversation measured in days, not months. An invite that never expires
    /// is a standing offer to connect a directory to this subscription.
    /// </summary>
    public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddDays(14);

    public DateTime? ConsumedAt { get; set; }

    /// <summary>The directory this invite ended up connecting, once a probe confirmed it.</summary>
    [MaxLength(100)]
    public string? ConsumedDirectoryId { get; set; }

    /// <summary>
    /// Why the last attempt did not connect — most usefully "Microsoft recorded the consent but
    /// we still cannot read the directory", which means only one of the two links was opened.
    /// </summary>
    [MaxLength(1000)]
    public string? LastError { get; set; }

    public bool IsUsable(DateTime utcNow) => ConsumedAt == null && ExpiresAt > utcNow;
}
