using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using OnCallApi.Data;
using OnCallApi.Services;
using OnCallApi.Middleware;
using OnCallApi.Hubs;
using OnCallApi.Authentication;
using OnCallApi.Services.Dispatch;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Web;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// Don't crash the whole app if a background service fails (e.g. Graph API not configured in dev)
builder.Services.Configure<HostOptions>(opts =>
    opts.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);

// ── Validate production secrets aren't placeholders ──
void ValidateSecret(string configPath, string value, string placeholder, string friendlyName)
{
    if (string.Equals(value, placeholder, StringComparison.OrdinalIgnoreCase))
    {
        var message = $"SECURITY: {friendlyName} in configuration '{configPath}' is still set to the default placeholder value '{placeholder}'. "
                    + "This must be overridden via environment variables, user secrets, or Key Vault before deploying to production.";
        if (builder.Environment.IsProduction())
        {
            throw new InvalidOperationException(message);
        }
        else
        {
            var logger = LoggerFactory.Create(c => c.AddConsole()).CreateLogger("StartupValidation");
            logger.LogWarning("⚠️ {Message}", message);
        }
    }
}
ValidateSecret("AzureAd:ClientId", builder.Configuration["AzureAd:ClientId"] ?? "", "your-api-client-id", "Azure AD Client ID");
ValidateSecret("GraphApi:ClientSecret", builder.Configuration["GraphApi:ClientSecret"] ?? "", "your-graph-client-secret", "Graph API Client Secret");
ValidateSecret("Authentication:Local:SigningKey", builder.Configuration["Authentication:Local:SigningKey"] ?? "", "change-me-to-a-32-char-min-secret-key!!", "Local JWT Signing Key");

// ── Authentication ──
var devAuthEnabled = builder.Configuration.GetValue<bool>("DevAuth:Enabled");

// Register local JWT service (needed by LocalAccountService regardless of auth mode)
builder.Services.AddSingleton<LocalJwtService>();

if (devAuthEnabled)
{
    // Development mode: auto-authenticate every request as a user with all roles.
    // No Entra ID, no JWT tokens needed.
    builder.Services.AddAuthentication(DevelopmentAuthenticationHandler.SchemeName)
        .AddScheme<AuthenticationSchemeOptions, DevelopmentAuthenticationHandler>(
            DevelopmentAuthenticationHandler.SchemeName, null);
}
else
{
    // Multi-provider JWT authentication: Microsoft Entra ID, Google, and Local.
    //
    // The ForwardDefaultSelector (set on the default JwtBearerOptions below) inspects
    // the raw JWT's "iss" claim to route each token to the correct handler:
    //   - accounts.google.com  →  "Google" scheme
    //   - oncall-directory     →  "Local" scheme
    //   - login.microsoftonline.com → "Bearer" (Microsoft, default)

    // 1. Set up the default authentication scheme
    var authBuilder = builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme);

    // 2. Microsoft Entra ID (default "Bearer" scheme)
    authBuilder.AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));

    // 3. Google OAuth (added as named scheme "Google")
    authBuilder.AddJwtBearer("Google", options =>
    {
        var googleConfig = builder.Configuration.GetSection("Authentication:Google");
        options.Authority = "https://accounts.google.com";
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = "https://accounts.google.com",
            ValidateAudience = true,
            ValidAudience = googleConfig["ClientId"],
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
        };
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = async context =>
            {
                var identity = context.Principal?.Identity as ClaimsIdentity;
                if (identity != null)
                {
                    identity.AddClaim(new Claim("auth_provider", "google"));
                    identity.AddClaim(new Claim("auth_validated", "true"));

                    // Map Google "sub" to "oid" for compatibility
                    var sub = context.Principal!.FindFirst("sub")?.Value;
                    if (sub != null)
                        identity.AddClaim(new Claim("oid", $"google-{sub}"));

                    // Google users get the default Viewer role
                    identity.AddClaim(new Claim(ClaimTypes.Role, "OnCall.Viewer"));
                }
                await Task.CompletedTask;
            }
        };
    });

    // 4. Local accounts (added as named scheme "Local")
    // TokenValidationParameters configured via PostConfigure below
    authBuilder.AddJwtBearer("Local", options =>
    {
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = async context =>
            {
                var identity = context.Principal?.Identity as ClaimsIdentity;
                if (identity != null)
                {
                    identity.AddClaim(new Claim("auth_provider", "local"));
                    identity.AddClaim(new Claim("auth_validated", "true"));
                }
                await Task.CompletedTask;
            }
        };
    });

    // Post-configure Local JWT options (resolves LocalJwtService from DI)
    builder.Services.AddOptions<JwtBearerOptions>("Local")
        .PostConfigure<LocalJwtService>((options, localService) =>
        {
            options.TokenValidationParameters = localService.GetValidationParameters();
        });

    // Post-configure the default JwtBearerOptions for:
    //   - Microsoft multi-tenant issuer validation
    //   - ForwardDefaultSelector for multi-provider routing
    builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
        .PostConfigure(options =>
        {
            // Multi-tenant issuer validation (any valid Azure AD tenant)
            options.TokenValidationParameters.ValidIssuer = null;
            options.TokenValidationParameters.IssuerValidator = (issuer, token, parameters) =>
            {
                if (Uri.TryCreate(issuer, UriKind.Absolute, out var uri) &&
                    uri.Host == "login.microsoftonline.com" &&
                    uri.Segments.Length >= 3 &&
                    uri.Segments[2].TrimEnd('/') != "common" &&
                    Guid.TryParse(uri.Segments[1].TrimEnd('/'), out _))
                {
                    return issuer;
                }
                throw new SecurityTokenInvalidIssuerException(
                    $"Issuer '{issuer}' is not a valid Azure AD tenant issuer.");
            };

            // Add auth_provider claim to Microsoft Entra ID tokens for consistent
            // provider identification across the auth pipeline (alongside Google and Local).
            options.Events = options.Events ?? new JwtBearerEvents();
            var existingOnTokenValidated = options.Events.OnTokenValidated;
            options.Events.OnTokenValidated = async context =>
            {
                var identity = context.Principal?.Identity as ClaimsIdentity;
                if (identity != null)
                {
                    identity.AddClaim(new Claim("auth_provider", "microsoft"));
                }
                // Chain any existing handler that Microsoft.Identity.Web may have registered
                if (existingOnTokenValidated != null)
                {
                    await existingOnTokenValidated(context);
                }
            };

            // Forwarding selector: route tokens to the correct scheme by issuer
            options.ForwardDefaultSelector = ctx =>
            {
                var authHeader = ctx.Request.Headers["Authorization"].FirstOrDefault();
                if (string.IsNullOrEmpty(authHeader))
                    return JwtBearerDefaults.AuthenticationScheme;

                var token = authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                    ? authHeader["Bearer ".Length..].Trim()
                    : null;

                if (string.IsNullOrEmpty(token))
                    return JwtBearerDefaults.AuthenticationScheme;

                try
                {
                    var handler = new JwtSecurityTokenHandler();
                    var jwt = handler.ReadJwtToken(token);

                    return jwt.Issuer switch
                    {
                        "https://accounts.google.com" or "accounts.google.com" => "Google",
                        LocalJwtService.Issuer => "Local",
                        _ => JwtBearerDefaults.AuthenticationScheme // Microsoft / default
                    };
                }
                catch
                {
                    return JwtBearerDefaults.AuthenticationScheme;
                }
            };
        });
}

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("RequireAdmin", policy =>
        policy.RequireRole("OnCall.Admin"));

    options.AddPolicy("RequireScheduler", policy =>
        policy.RequireRole("OnCall.Scheduler", "OnCall.Admin"));

    options.AddPolicy("RequireViewer", policy =>
        policy.RequireRole("OnCall.Viewer", "OnCall.Scheduler", "OnCall.Admin"));

    // ── Granular permission-based policies ──
    options.AddPolicy("RequireScheduleRead", policy =>
        policy.RequireClaim(OnCallApi.Authorization.Permissions.ClaimType,
            OnCallApi.Authorization.Permissions.ScheduleRead));

    options.AddPolicy("RequireScheduleWrite", policy =>
        policy.RequireClaim(OnCallApi.Authorization.Permissions.ClaimType,
            OnCallApi.Authorization.Permissions.ScheduleWrite));

    options.AddPolicy("RequireDirectoryRead", policy =>
        policy.RequireClaim(OnCallApi.Authorization.Permissions.ClaimType,
            OnCallApi.Authorization.Permissions.DirectoryRead));

    options.AddPolicy("RequireDirectoryWrite", policy =>
        policy.RequireClaim(OnCallApi.Authorization.Permissions.ClaimType,
            OnCallApi.Authorization.Permissions.DirectoryWrite));

    options.AddPolicy("RequireAdminFull", policy =>
        policy.RequireClaim(OnCallApi.Authorization.Permissions.ClaimType,
            OnCallApi.Authorization.Permissions.AdminFull));

    options.AddPolicy("RequireCodeCallWrite", policy =>
        policy.RequireClaim(OnCallApi.Authorization.Permissions.ClaimType,
            OnCallApi.Authorization.Permissions.CodeCallWrite));

    // ── Multi-tenant policies ──
    options.AddPolicy("RequireAdminScoped", policy =>
        policy.RequireClaim(OnCallApi.Authorization.Permissions.ClaimType,
            OnCallApi.Authorization.Permissions.AdminScoped));

    options.AddPolicy("RequireTenantManage", policy =>
        policy.RequireClaim(OnCallApi.Authorization.Permissions.ClaimType,
            OnCallApi.Authorization.Permissions.TenantManage));

    options.AddPolicy("RequireAdminFullOrScoped", policy =>
        policy.RequireAssertion(context =>
            context.User.HasClaim(OnCallApi.Authorization.Permissions.ClaimType,
                OnCallApi.Authorization.Permissions.AdminFull) ||
            context.User.HasClaim(OnCallApi.Authorization.Permissions.ClaimType,
                OnCallApi.Authorization.Permissions.AdminScoped)));

    options.AddPolicy("RequireAdminFullOrTenantManage", policy =>
        policy.RequireAssertion(context =>
            context.User.HasClaim(OnCallApi.Authorization.Permissions.ClaimType,
                OnCallApi.Authorization.Permissions.AdminFull) ||
            context.User.HasClaim(OnCallApi.Authorization.Permissions.ClaimType,
                OnCallApi.Authorization.Permissions.TenantManage)));
});

// ── Database ──
if (builder.Environment.IsDevelopment())
{
    // Use SQLite for local development - provides persistent file-based storage
    // Database file: OnCallDb.sqlite in the project directory
    var dbPath = Path.Combine(AppContext.BaseDirectory, "OnCallDb.sqlite");
    builder.Services.AddDbContext<AppDbContext>(options =>
        options.UseSqlite($"Data Source={dbPath}"));
}
else
{
    builder.Services.AddDbContext<AppDbContext>(options =>
        options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));
}

// ── Rate Limiting ──
builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter("Api", opt =>
    {
        opt.PermitLimit = 100;
        opt.Window = TimeSpan.FromMinutes(1);
    });
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});

// ── Response Compression ──
builder.Services.AddResponseCompression(options =>
{
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
});

// ── Graph API Configuration ──
builder.Services.Configure<OnCallApi.Configuration.GraphApiOptions>(
    builder.Configuration.GetSection("GraphApi"));

// ── Application Services ──
builder.Services.AddScoped<IGraphApiService, GraphApiService>();
builder.Services.AddScoped<IScheduleService, ScheduleService>();
builder.Services.AddScoped<IDirectoryService, DirectoryService>();
builder.Services.AddScoped<IDutyHourService, DutyHourService>();
builder.Services.AddScoped<BulkImportService>();
builder.Services.AddScoped<TeamsNotificationService>();
builder.Services.AddSingleton<AuditService>();
builder.Services.AddSingleton<IAuditService>(sp => sp.GetRequiredService<AuditService>());
builder.Services.AddHostedService<AuditBackgroundService>();
builder.Services.AddHostedService<AdSyncBackgroundService>();
builder.Services.AddHostedService<DepartmentSyncService>();
builder.Services.AddHostedService<PresenceSyncService>();
builder.Services.AddHostedService<CalendarSyncService>();
builder.Services.AddScoped<AvailabilityService>();
builder.Services.AddScoped<EscalationService>();
builder.Services.AddScoped<IAdminService, AdminService>();
builder.Services.AddScoped<ITenantContextService, TenantContextService>();
builder.Services.AddScoped<ILocalAccountService, LocalAccountService>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IPhoneTreeEventService, PhoneTreeEventService>();
// Register HttpClient factory for dispatch clients (CUCM, InformaCast, Vocera)
builder.Services.AddHttpClient();

builder.Services.AddScoped<ICodeCallDispatchService, CodeCallDispatchService>();
builder.Services.AddScoped<ICiscoCucmClient, CiscoCucmClient>();
builder.Services.AddScoped<IInformaCastClient, InformaCastClient>();
builder.Services.AddScoped<IVoceraClient, VoceraClient>();
builder.Services.Configure<OnCallApi.Configuration.DispatchOptions>(
    builder.Configuration.GetSection(OnCallApi.Configuration.DispatchOptions.SectionName));

// Dispatch job queue + background consumer (safety-critical: jobs are never dropped)
builder.Services.AddSingleton<DispatchJobQueue>();
builder.Services.AddSingleton<DispatchBackgroundService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DispatchBackgroundService>());

builder.Services.AddScoped<TenantSyncService>();
builder.Services.AddScoped<TeamsBotService>();
builder.Services.AddScoped<SharePointPublishingService>();
builder.Services.AddHostedService<EscalationBackgroundService>();

// ── SignalR (real-time notifications) ──
builder.Services.AddSignalR();

// ── CORS ──
builder.Services.AddCors(options =>
{
    options.AddPolicy("Frontend", policy =>
        policy.WithOrigins(builder.Configuration["Cors:Origin"] ?? "http://localhost:5173")
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials());
});

// ── Telemetry ──
builder.Services.AddApplicationInsightsTelemetry(options =>
{
    options.ConnectionString = builder.Configuration["ApplicationInsights:ConnectionString"] ?? string.Empty;
});

// ── Health Checks ──
builder.Services.AddHealthChecks()
    .AddDbContextCheck<AppDbContext>("database");

// ── FluentValidation ──
builder.Services.AddValidatorsFromAssemblyContaining<OnCallApi.Validators.ScheduleValidator>();

// ── Swagger ──
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        // Prevent circular reference errors from navigation properties (Employee -> Manager -> Employee, etc.)
        options.JsonSerializerOptions.ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles;
    });

var app = builder.Build();

// ── Startup Graph API Health Check ──
// Verifies Graph API credentials and connectivity immediately at startup.
// Logs a structured warning on failure but does NOT crash the application.
// This follows the graceful degradation pattern: sync services will also fail
// independently, but early detection helps with diagnostics.
using (var scope = app.Services.CreateScope())
{
    var graphService = scope.ServiceProvider.GetRequiredService<IGraphApiService>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<GraphApiService>>();
    try
    {
        var connected = await graphService.CheckGraphConnectionAsync();
        if (connected)
        {
            logger.LogInformation("Graph API startup health check: connected");
        }
        else
        {
            logger.LogWarning(
                "Graph API startup health check: responded but returned no data. "
                + "Check that the app registration has the correct permissions granted.");
        }
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex,
            "Graph API startup health check failed: {Message}. "
            + "Continuing startup with graceful degradation — sync services will log their own errors.",
            ex.Message);
    }
}

// ── Middleware Pipeline ──
app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseResponseCompression();
app.UseRateLimiter();
app.UseHttpsRedirection();
app.UseCors("Frontend");
app.UseAuthentication();
app.UseAuthorization();

if (!devAuthEnabled)
{
    // JWT scope/claim validation for protected API endpoints (runs after auth)
    // Skipped in dev mode — DevelopmentAuthenticationHandler provides fake claims.
    app.UseMiddleware<JwtValidationMiddleware>();
}

// Expand user claims with tenant-scoped permissions from TenantAdmin records.
app.UseMiddleware<TenantClaimsMiddleware>();

// HIPAA audit logging (runs after auth so User is populated)
// In dev mode the fake user claims are logged instead of real ones
app.UseMiddleware<HipaaAuditMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// ── Controllers ──
app.MapControllers();

// ── Health Checks ──
app.MapHealthChecks("/health", new HealthCheckOptions
{
    ResponseWriter = async (context, report) =>
    {
        context.Response.ContentType = "application/json";
        var result = System.Text.Json.JsonSerializer.Serialize(new
        {
            status = report.Status.ToString(),
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                description = e.Value.Description
            })
        });
        await context.Response.WriteAsync(result);
    }
});

// ── SignalR Hubs ──
app.MapHub<OnCallNotificationHub>("/hubs/notifications");

// ── Auto-setup database in development (EnsureCreated to avoid SQL Server-specific migration SQL)
if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var provider = db.Database.ProviderName ?? string.Empty;
    if (!provider.Contains("InMemory", StringComparison.OrdinalIgnoreCase))
    {
        // EnsureCreated creates schema from the model directly, bypassing migration SQL.
        // This works around nvarchar(max) and other SQL Server-specific syntax in migrations
        // that SQLite cannot parse. HasData() seed values from OnModelCreating are applied.
        await db.Database.EnsureCreatedAsync();
    }
}

app.Run();

// Make Program accessible to integration tests
public partial class Program { }
