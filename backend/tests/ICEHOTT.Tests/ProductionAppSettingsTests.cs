using ICEHOTT.API.Hosting;
using Microsoft.Extensions.Configuration;

namespace ICEHOTT.Tests;

/// <summary>
/// Guards the committed appsettings.Production.json: safe non-secret defaults only, and
/// the base file plus the production file must not be startable without operator input.
/// </summary>
public sealed class ProductionAppSettingsTests
{
    private static string ApiProjectDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "ICEHOTT.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "src", "ICEHOTT.API");
    }

    private static IConfigurationRoot Load() =>
        new ConfigurationBuilder()
            .SetBasePath(ApiProjectDirectory())
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.Production.json", optional: false)
            .Build();

    [Fact]
    public void Production_Defaults_Disable_Auto_Migration_And_Require_Tls()
    {
        var config = Load();
        Assert.False(config.GetValue<bool>("Database:AutoMigrate"));
        Assert.True(config.GetValue<bool>("Database:RequireTransportSecurity"));
    }

    [Fact]
    public void Production_Defaults_Do_Not_Fall_Back_To_Local_Artifact_Storage()
    {
        var config = Load();
        var provider = config["ArtifactStorage:Provider"];
        Assert.False(string.IsNullOrWhiteSpace(provider));
        Assert.NotEqual("Local", provider, StringComparer.OrdinalIgnoreCase);
        Assert.False(config.GetValue<bool>("ProductionSafety:AllowLocalArtifactStorage"));
    }

    [Fact]
    public void Production_Defaults_Enable_Proxy_Trust_But_Never_Ship_A_Trusted_Network()
    {
        var config = Load();
        Assert.True(config.GetValue<bool>("ForwardedHeaders:Enabled"));
        Assert.Equal(1, config.GetValue<int>("ForwardedHeaders:ForwardLimit"));
        // The proxy network is platform specific and must be supplied by the operator.
        Assert.Empty(config.GetSection("ForwardedHeaders:TrustedNetworks").GetChildren());
    }

    [Fact]
    public void Production_Defaults_Contain_No_Secrets_Or_Origins()
    {
        var config = Load();
        Assert.True(string.IsNullOrEmpty(config["Jwt:Key"]));
        Assert.True(string.IsNullOrEmpty(config["ConnectionStrings:DefaultConnection"]));
        Assert.True(string.IsNullOrEmpty(config["OpenAiEmbedding:ApiKey"]));
        Assert.True(string.IsNullOrEmpty(config["ObjectStorage:Endpoint"]));
        Assert.True(string.IsNullOrEmpty(config["ObjectStorage:Bucket"]));
        Assert.True(string.IsNullOrEmpty(config["ObjectStorage:AccessKeyId"]));
        Assert.True(string.IsNullOrEmpty(config["ObjectStorage:SecretAccessKey"]));
        Assert.Equal("us-east-1", config["ObjectStorage:Region"]);
        Assert.True(config.GetValue<bool>("ObjectStorage:ForcePathStyle"));
        Assert.Equal(string.Empty, config["ObjectStorage:Prefix"]);
        Assert.Empty(config.GetSection("Cors:AllowedOrigins").GetChildren());

        var raw = File.ReadAllText(Path.Combine(ApiProjectDirectory(), "appsettings.Production.json"));
        Assert.DoesNotContain("password", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("development-only", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("localhost", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Production_Defaults_Alone_Fail_Closed_Validation()
    {
        var config = Load();
        var errors = ProductionConfigurationValidator.Validate(config, "Production");
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Production_Logging_Does_Not_Enable_Sql_Command_Logging()
    {
        var level = Load()["Logging:LogLevel:Microsoft.EntityFrameworkCore.Database.Command"];
        Assert.Contains(level, new[] { "Warning", "Error", "Critical", "None" });
    }

    [Fact]
    public void Compose_And_Dockerfile_Do_Not_Bake_Secrets_Into_The_Image()
    {
        var root = Path.GetFullPath(Path.Combine(ApiProjectDirectory(), "..", "..", ".."));
        var dockerfile = File.ReadAllText(Path.Combine(root, "backend", "Dockerfile"));
        Assert.DoesNotContain(".env", dockerfile.Replace("ASPNETCORE", ""));
        Assert.Contains("USER ", dockerfile);
        Assert.DoesNotContain("USER root", dockerfile);
        Assert.Contains("ARG GIT_SHA", dockerfile);
        Assert.Contains("ASPNETCORE_ENVIRONMENT=Production", dockerfile);
        Assert.DoesNotContain("Jwt__Key", dockerfile);
        Assert.DoesNotContain("Password", dockerfile);
    }
}
