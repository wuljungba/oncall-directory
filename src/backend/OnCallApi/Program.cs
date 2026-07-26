using OnCallApi.Data;
using OnCallApi.Services;
using OnCallApi.Middleware;
using OnCallApi.Hubs;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Web;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// ── Authentication ──
var devAuthEnabled = builder.Configuration.GetValue<bool>("DevAuth:Enabled");

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
    // GraphApiService creates its own GraphServiceClient via ClientSecretCredential,
    // so we only need the Web API JWT bearer validation here — not downstream token acquisition.
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));

    // Post-configure JwtBearerOptions for multi-tenant issuer validation.
    // Runs after Microsoft.Identity.Web's own configuration and safely replaces
    // the issuer validator to accept any valid Azure AD tenant.
    builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
        .PostConfigure(options =>
        {
            options.TokenValidationParameters.ValidIssuer = null;
            options.TokenValidationParameters.IssuerValidator = (issuer, token, parameters) =>
            {
                // Accept any valid Azure AD v2.0 issuer
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
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IPhoneTreeEventService, PhoneTreeEventService>();
builder.Services.AddScoped<ICodeCallDispatchService, CodeCallDispatchService>();
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
