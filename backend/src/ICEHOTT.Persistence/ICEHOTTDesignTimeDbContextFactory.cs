using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ICEHOTT.Persistence;

public sealed class ICEHOTTDesignTimeDbContextFactory
    : IDesignTimeDbContextFactory<ICEHOTTDbContext>
{
    public ICEHOTTDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("ICEHOTT_DESIGNTIME_CONNECTION")
            ?? "Host=localhost;Port=5432;Database=icehott;Username=icehott;Password=design-time-only";

        var options = new DbContextOptionsBuilder<ICEHOTTDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new ICEHOTTDbContext(options);
    }
}
