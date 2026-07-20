using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace OnCallApi.Data;

/// <summary>
/// Design-time factory for EF Core migrations.
/// This is required because the DbContext is configured conditionally
/// (in-memory for dev, SQL Server for production) in Program.cs.
/// </summary>
public class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();

        // Use SQL Server for design-time (migration generation).
        // The connection string here is for design-time only;
        // the runtime connection string comes from appsettings.json.
        optionsBuilder.UseSqlServer(
            "Server=localhost;Database=OnCallDb;Trusted_Connection=True;TrustServerCertificate=True;");

        return new AppDbContext(optionsBuilder.Options);
    }
}
