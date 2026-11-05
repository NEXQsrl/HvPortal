using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace HvPortal.Gateway.Data;

/// <summary>Used only by "dotnet ef" to generate migrations; never at runtime.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<PortalDbContext>
{
    public PortalDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<PortalDbContext>().UseSqlite("Data Source=design-time.db").Options);
}
