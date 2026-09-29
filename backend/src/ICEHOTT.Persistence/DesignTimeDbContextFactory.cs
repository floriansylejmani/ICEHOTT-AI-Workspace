using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ICEHOTT.Persistence;

/// <summary>
/// Lets <c>dotnet ef</c> and the self-contained migration bundle create the context from a
/// connection string alone, without booting the web host (which demands JWT, CORS and
/// provider settings that are irrelevant to migrating a schema). The bundle's own
/// <c>--connection</c> argument overrides the placeholder below at run time.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ICEHOTTDbContext>
{
    public const string ConnectionVariable = "ICEHOTT_MIGRATION_CONNECTION";

    // Never dialled: only used when no connection is supplied, e.g. while generating a script.
    private const string Placeholder = "Host=unset.invalid;Database=unset;Username=unset";

    public ICEHOTTDbContext CreateDbContext(string[] args)
    {
        var connection =
            Environment.GetEnvironmentVariable(ConnectionVariable) ??
            Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection") ??
            Placeholder;

        return new ICEHOTTDbContext(
            new DbContextOptionsBuilder<ICEHOTTDbContext>()
                .UseNpgsql(connection)
                .Options);
    }
}
