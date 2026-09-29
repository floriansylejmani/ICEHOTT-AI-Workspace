using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace ICEHOTT.Persistence;

/// <summary>
/// Lets <c>dotnet ef</c> and the self-contained migration bundle create the context from a
/// connection string alone, without booting the web host (which demands JWT, CORS and
/// provider settings that are irrelevant to migrating a schema).
///
/// Because a design-time factory takes precedence over the web host, this factory resolves
/// the connection the same way the host would, so local <c>dotnet ef</c> keeps working from
/// the API project's appsettings:
/// <list type="number">
///   <item><c>ICEHOTT_MIGRATION_CONNECTION</c> (explicit migration credential),</item>
///   <item><c>ConnectionStrings__DefaultConnection</c> and <c>appsettings(.{Environment}).json</c>
///   in the working directory (the API project directory for <c>dotnet ef</c>),</item>
///   <item>a never-dialled placeholder, for script generation and for the bundle, whose own
///   <c>--connection</c> argument overrides it at run time.</item>
/// </list>
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ICEHOTTDbContext>
{
    public const string ConnectionVariable = "ICEHOTT_MIGRATION_CONNECTION";

    private const string Placeholder = "Host=unset.invalid;Database=unset;Username=unset";

    public ICEHOTTDbContext CreateDbContext(string[] args) =>
        Create(
            Directory.GetCurrentDirectory(),
            Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ??
            Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ??
            "Development");

    public static ICEHOTTDbContext Create(string settingsDirectory, string environmentName)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(settingsDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile($"appsettings.{environmentName}.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var connection = Environment.GetEnvironmentVariable(ConnectionVariable);
        if (string.IsNullOrWhiteSpace(connection))
            connection = configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connection))
            connection = Placeholder;

        return new ICEHOTTDbContext(
            new DbContextOptionsBuilder<ICEHOTTDbContext>()
                .UseNpgsql(connection)
                .Options);
    }
}
