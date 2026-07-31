using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace OnCallApi.Authentication;

/// <summary>
/// Issues and validates self-contained JWTs for local database accounts.
/// Uses HMAC-SHA256 symmetric signing.
///
/// Configuration:
///   Authentication:Local:SigningKey — A 32+ character secret key
///   Authentication:Local:TokenExpiryMinutes — Token lifetime (default 1440 = 24h)
/// </summary>
public class LocalJwtService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<LocalJwtService> _logger;

    public LocalJwtService(IConfiguration configuration, ILogger<LocalJwtService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// The expected issuer for local JWTs.
    /// </summary>
    public const string Issuer = "oncall-directory";

    /// <summary>
    /// The expected audience for local JWTs.
    /// </summary>
    public const string Audience = "oncall-api";

    /// <summary>
    /// Generates a JWT for a local account user.
    /// </summary>
    public string GenerateToken(int userId, string email, string displayName, string[] roles, Guid? employeeId = null)
    {
        var signingKey = GetSigningKey();
        var expiryMinutes = _configuration.GetValue<int>("Authentication:Local:TokenExpiryMinutes", 1440);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, $"local-{userId}"),
            new(ClaimTypes.Email, email),
            new(ClaimTypes.Name, displayName),
            new("auth_provider", "local"),
            new("scp", "access_as_user"),
            new("oid", $"local-{userId}"),
        };

        foreach (var role in roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        if (employeeId.HasValue)
        {
            claims.Add(new Claim("employee_id", employeeId.Value.ToString()));
        }

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(expiryMinutes),
            signingCredentials: credentials
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>
    /// Validates a local JWT and returns the ClaimsPrincipal.
    /// Returns null if validation fails.
    /// </summary>
    public ClaimsPrincipal? ValidateToken(string token)
    {
        try
        {
            var signingKey = GetSigningKey();
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey));

            var handler = new JwtSecurityTokenHandler();
            var result = handler.ValidateToken(token, new TokenValidationParameters
            {
                ValidIssuer = Issuer,
                ValidAudience = Audience,
                IssuerSigningKey = key,
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromMinutes(1),
            }, out _);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Local JWT validation failed");
            return null;
        }
    }

    private string GetSigningKey()
    {
        var key = _configuration["Authentication:Local:SigningKey"];
        if (string.IsNullOrEmpty(key) || key.Length < 32)
        {
            _logger.LogWarning("Local JWT signing key is missing or too short (< 32 chars). Using development fallback.");
            // Development fallback — never use in production
            return "dev-local-jwt-signing-key-at-least-32-chars!!";
        }
        return key;
    }

    /// <summary>
    /// Gets the TokenValidationParameters for local JWTs, for use in
    /// multi-issuer JWT validation setup.
    /// </summary>
    public TokenValidationParameters GetValidationParameters()
    {
        var signingKey = GetSigningKey();
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey));

        return new TokenValidationParameters
        {
            ValidIssuer = Issuer,
            ValidAudience = Audience,
            IssuerSigningKey = key,
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1),
        };
    }
}
