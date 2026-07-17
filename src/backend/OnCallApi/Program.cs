using OnCallApi.Data;
using OnCallApi.Services;
using OnCallApi.Middleware;
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
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

// ── Application Services ──
builder.Services.AddScoped<IGraphApiService, GraphApiService>();
builder.Services.AddScoped<IScheduleService, ScheduleService>();
builder.Services.AddScoped<IDirectoryService, DirectoryService>();
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

// ── Swagger ──
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// ── Middleware Pipeline ──
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

// ── SignalR Hubs ──
app.MapHub<OnCallNotificationHub>("/hubs/notifications");

// ── Auto-migrate (dev only) ──
if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
}

app.Run();
