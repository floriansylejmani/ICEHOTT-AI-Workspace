using ICEHOTT.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ICEHOTT.Tests;

/// <summary>
/// The EF migration bundle must run with nothing but a connection string, and plain local
/// <c>dotnet ef</c> must keep resolving the connection from the API's appsettings exactly as
/// it did before the factory existed (the factory takes precedence over the web host).
/// </summary>
public sealed class MigrationDesignTimeFactoryTests
{
    private static string Host(ICEHOTTDbContext context) =>
        new NpgsqlConnectionStringBuilder(context.Database.GetConnectionString()).Host!;

    private sealed class TempSettings : IDisposable
    {
        public TempSettings(string? baseJson = null, string? developmentJson = null)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "icehott-ef-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            if (baseJson is not null)
                File.WriteAllText(System.IO.Path.Combine(Path, "appsettings.json"), baseJson);
            if (developmentJson is not null)
                File.WriteAllText(
                    System.IO.Path.Combine(Path, "appsettings.Development.json"), developmentJson);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, true); } catch { }
        }
    }

    private static string Conn(string host) =>
        "{\"ConnectionStrings\":{\"DefaultConnection\":\"Host=" + host +
        ";Database=d;Username=u;Password=p\"}}";

    private static IDisposable Env(params (string Name, string? Value)[] values)
    {
        var saved = values.ToDictionary(x => x.Name, x => Environment.GetEnvironmentVariable(x.Name));
        foreach (var (name, value) in values)
            Environment.SetEnvironmentVariable(name, value);
        return new Restore(saved);
    }

    private sealed class Restore(Dictionary<string, string?> saved) : IDisposable
    {
        public void Dispose()
        {
            foreach (var (name, value) in saved)
                Environment.SetEnvironmentVariable(name, value);
        }
    }

    [Fact]
    public void Explicit_Migration_Connection_Wins_Over_Everything()
    {
        using var settings = new TempSettings(developmentJson: Conn("from-json"));
        using var _ = Env(
            (DesignTimeDbContextFactory.ConnectionVariable, "Host=explicit;Database=d;Username=u;Password=p"),
            ("ConnectionStrings__DefaultConnection", "Host=standard;Database=d;Username=u;Password=p"));

        using var context = DesignTimeDbContextFactory.Create(settings.Path, "Development");
        Assert.True(context.Database.IsNpgsql());
        Assert.Equal("explicit", Host(context));
    }

    [Fact]
    public void Standard_Environment_Variable_Wins_Over_Appsettings()
    {
        using var settings = new TempSettings(developmentJson: Conn("from-json"));
        using var _ = Env(
            (DesignTimeDbContextFactory.ConnectionVariable, null),
            ("ConnectionStrings__DefaultConnection", "Host=standard;Database=d;Username=u;Password=p"));

        using var context = DesignTimeDbContextFactory.Create(settings.Path, "Development");
        Assert.Equal("standard", Host(context));
    }

    [Fact]
    public void Local_Development_Falls_Back_To_The_Api_Appsettings_Like_Before()
    {
        using var settings = new TempSettings(
            baseJson: Conn("from-base"), developmentJson: Conn("from-development"));
        using var _ = Env(
            (DesignTimeDbContextFactory.ConnectionVariable, null),
            ("ConnectionStrings__DefaultConnection", null));

        using var context = DesignTimeDbContextFactory.Create(settings.Path, "Development");
        Assert.Equal("from-development", Host(context));

        using var production = DesignTimeDbContextFactory.Create(settings.Path, "Production");
        Assert.Equal("from-base", Host(production));
    }

    [Fact]
    public void Without_Any_Source_A_Never_Dialled_Placeholder_Is_Used()
    {
        using var settings = new TempSettings();
        using var _ = Env(
            (DesignTimeDbContextFactory.ConnectionVariable, null),
            ("ConnectionStrings__DefaultConnection", null));

        using var context = DesignTimeDbContextFactory.Create(settings.Path, "Development");
        Assert.True(context.Database.IsNpgsql());
        Assert.EndsWith(".invalid", Host(context));
    }

    [Fact]
    public void Empty_Base_Connection_String_Does_Not_Mask_The_Placeholder()
    {
        using var settings = new TempSettings(
            baseJson: "{\"ConnectionStrings\":{\"DefaultConnection\":\"\"}}");
        using var _ = Env(
            (DesignTimeDbContextFactory.ConnectionVariable, null),
            ("ConnectionStrings__DefaultConnection", null));

        using var context = DesignTimeDbContextFactory.Create(settings.Path, "Production");
        Assert.EndsWith(".invalid", Host(context));
    }
}
