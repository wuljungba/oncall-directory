using OnCallApi.Data;
using OnCallApi.Services;
using OnCallApi.Middleware;
using OnCallApi.Hubs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Web;
using Microsoft.AspNetCore.Authentication.JwtBearer;

var builder = WebApplication.CreateBuilder(args);

// ── Authentication ──
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("RequireAdmin", policy =>
        policy.RequireRole("OnCall.Admin"));

    options.AddPolicy("RequireScheduler", policy =>
        policy.RequireRole("OnCall.Scheduler", "OnCall.Admin"));

    options.AddPolicy("RequireViewer", policy =>
        policy.RequireRole("OnCall.Viewer", "OnCall.Scheduler", "OnCall.Admin"));
});

// ── Database ──
if (builder.Environment.IsDevelopment())
{
    // Use in-memory DB for local development when SQL LocalDB is not available.
    builder.Services.AddDbContext<AppDbContext>(options =>
        options.UseInMemoryDatabase("OnCallDb"));
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

// ── Application Services ──
builder.Services.AddScoped<IGraphApiService, GraphApiService>();
builder.Services.AddScoped<IScheduleService, ScheduleService>();
builder.Services.AddScoped<IDirectoryService, DirectoryService>();
builder.Services.AddScoped<BulkImportService>();
builder.Services.AddSingleton<AuditService>();
builder.Services.AddSingleton<IAuditService>(sp => sp.GetRequiredService<AuditService>());
builder.Services.AddHostedService<AuditBackgroundService>();
builder.Services.AddHostedService<AdSyncBackgroundService>();

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
builder.Services.AddControllers();

var app = builder.Build();

// ── Middleware Pipeline ──
app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseResponseCompression();
app.UseRateLimiter();
app.UseHttpsRedirection();
app.UseCors("Frontend");
app.UseAuthentication();
app.UseAuthorization();
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

// ── Auto-migrate (only for relational providers)
if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var provider = db.Database.ProviderName ?? string.Empty;
    if (!provider.Contains("InMemory", StringComparison.OrdinalIgnoreCase))
    {
        await db.Database.MigrateAsync();
    }
}

app.Run();
