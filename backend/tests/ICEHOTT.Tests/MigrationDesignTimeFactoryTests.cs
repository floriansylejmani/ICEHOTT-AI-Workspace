using ICEHOTT.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ICEHOTT.Tests;

/// <summary>
/// The EF migration bundle must run with nothing but a connection string; it must not
/// need the application's JWT, CORS or provider settings just to migrate a database.
/// </summary>
public sealed class MigrationDesignTimeFactoryTests
{
    [Fact]
    public void Factory_Builds_A_Npgsql_Context_Without_Application_Settings()
    {
        var previous = Environment.GetEnvironmentVariable(
            DesignTimeDbContextFactory.ConnectionVariable);
        Environment.SetEnvironmentVariable(
            DesignTimeDbContextFactory.ConnectionVariable,
            "Host=db.internal;Database=icehott;Username=migrator;Password=x");
        try
        {
            using var context = new DesignTimeDbContextFactory().CreateDbContext([]);
            Assert.True(context.Database.IsNpgsql());
            Assert.Equal("db.internal", new Npgsql.NpgsqlConnectionStringBuilder(
                context.Database.GetConnectionString()).Host);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                DesignTimeDbContextFactory.ConnectionVariable, previous);
        }
    }

    [Fact]
    public void Factory_Falls_Back_To_The_Standard_Connection_Variable_Then_A_Placeholder()
    {
        var saved = new[]
        {
            DesignTimeDbContextFactory.ConnectionVariable,
            "ConnectionStrings__DefaultConnection"
        }.ToDictionary(x => x, Environment.GetEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(DesignTimeDbContextFactory.ConnectionVariable, null);
            Environment.SetEnvironmentVariable(
                "ConnectionStrings__DefaultConnection", "Host=standard;Database=d;Username=u;Password=p");
            using (var context = new DesignTimeDbContextFactory().CreateDbContext([]))
                Assert.Equal("standard", new Npgsql.NpgsqlConnectionStringBuilder(
                    context.Database.GetConnectionString()).Host);

            Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", null);
            using (var context = new DesignTimeDbContextFactory().CreateDbContext([]))
                Assert.True(context.Database.IsNpgsql());
        }
        finally
        {
            foreach (var (name, value) in saved)
                Environment.SetEnvironmentVariable(name, value);
        }
    }
}
