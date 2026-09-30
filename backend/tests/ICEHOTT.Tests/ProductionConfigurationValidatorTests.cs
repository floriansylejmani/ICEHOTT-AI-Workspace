using ICEHOTT.API.Hosting;
using Microsoft.Extensions.Configuration;

namespace ICEHOTT.Tests;

public sealed class ProductionConfigurationValidatorTests
{
    private const string PasswordSentinel = "S3cr3t-Pw-Sentinel-7431";
    private const string JwtSentinel = "jwt-Sentinel-Key-Zk29xQ7mPv41LrTa8Ns0Wd5HbYc3Ge6U";

    private static Dictionary<string, string?> Safe() => new()
    {
        ["Deployment:Tier"] = "production",
        ["Service:Role"] = "Api",
        ["ConnectionStrings:DefaultConnection"] =
            $"Host=db.internal;Port=5432;Database=icehott;Username=icehott_app;Password={PasswordSentinel};SSL Mode=Require",
        ["Jwt:Key"] = JwtSentinel,
        ["Jwt:Issuer"] = "ICEHOTT",
        ["Jwt:Audience"] = "ICEHOTT.Web",
        ["AiRuntime:BaseUrl"] = "http://icehott-ai.internal:8000",
        ["Cors:AllowedOrigins:0"] = "https://app.icehott.example",
        ["AllowedHosts"] = "api.icehott.example",
        ["Database:AutoMigrate"] = "false",
        ["Database:RequireTransportSecurity"] = "true",
        ["ArtifactStorage:Provider"] = "S3",
        ["ObjectStorage:Endpoint"] = "https://objects.example",
        ["ObjectStorage:Bucket"] = "icehott-prod-artifacts",
        ["Release:GitSha"] = "c63a6b2337a1e17c056d5adbefc1daba83e8abf1",
        ["ForwardedHeaders:Enabled"] = "true",
        ["ForwardedHeaders:TrustedNetworks:0"] = "10.0.0.0/8"
    };

    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static IReadOnlyList<string> Validate(
        Dictionary<string, string?> values, string environment = "Production") =>
        ProductionConfigurationValidator.Validate(Config(values), environment);

    [Fact]
    public void Safe_Production_Configuration_Passes()
    {
        Assert.Empty(Validate(Safe()));
        ProductionConfigurationValidator.Enforce(Config(Safe()), "Production");
    }

    [Fact]
    public void Staging_Is_Validated_Like_Production()
    {
        var values = Safe();
        values["Deployment:Tier"] = "staging";
        Assert.Empty(Validate(values, "Staging"));
        values["Jwt:Key"] = "";
        Assert.NotEmpty(Validate(values, "Staging"));
    }

    [Fact]
    public void Local_Development_Configuration_Is_Not_Restricted()
    {
        var values = new Dictionary<string, string?>
        {
            ["Jwt:Key"] = "ICEHOTT-development-only-key-never-use-in-production-2026",
            ["Database:AutoMigrate"] = "true",
            ["ConnectionStrings:DefaultConnection"] =
                "Host=localhost;Database=icehott;Username=icehott;Password=change-me"
        };
        Assert.Empty(Validate(values, "Development"));
        ProductionConfigurationValidator.Enforce(Config(values), "Development");
    }

    [Fact]
    public void Production_Environment_Requires_Explicit_Deployment_Tier()
    {
        var values = Safe();
        values.Remove("Deployment:Tier");
        Assert.Contains(Validate(values), x => x.StartsWith("Deployment:Tier"));
    }

    [Fact]
    public void Development_Environment_Cannot_Carry_A_Production_Tier()
    {
        Assert.Contains(
            Validate(Safe(), "Development"),
            x => x.StartsWith("ASPNETCORE_ENVIRONMENT"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("short-key")]
    [InlineData("ICEHOTT-development-only-key-never-use-in-production-2026")]
    [InlineData("ICEHOTT-ci-only-key-with-at-least-32-characters-2026")]
    [InlineData("replace-with-at-least-32-random-characters")]
    [InlineData("please-change-me-please-change-me-please-change-me")]
    public void Unsafe_Jwt_Keys_Are_Rejected(string key)
    {
        var values = Safe();
        values["Jwt:Key"] = key;
        var errors = Validate(values);
        Assert.Contains(errors, x => x.StartsWith("Jwt:Key"));
        Assert.DoesNotContain(errors, x => x.Contains(key) && key.Length > 12);
    }

    [Fact]
    public void Missing_Jwt_Key_Is_Rejected()
    {
        var values = Safe();
        values.Remove("Jwt:Key");
        Assert.Contains(Validate(values), x => x.StartsWith("Jwt:Key"));
    }

    [Theory]
    [InlineData("Host=db;Database=icehott;Username=icehott;Password=change-me;SSL Mode=Require")]
    [InlineData("Host=db;Database=icehott;Username=icehott;Password=postgres;SSL Mode=Require")]
    [InlineData("Host=db;Database=icehott;Username=icehott;Password=;SSL Mode=Require")]
    [InlineData("Host=db;Database=icehott;Username=postgres;Password=Str0ng-Prod-Pw-1;SSL Mode=Require")]
    [InlineData("Host=localhost;Database=icehott;Username=icehott;Password=Str0ng-Prod-Pw-1;SSL Mode=Require")]
    [InlineData("Host=db;Database=icehott;Username=icehott;Password=Str0ng-Prod-Pw-1;SSL Mode=Disable")]
    [InlineData("Host=db;Database=icehott;Username=icehott;Password=Str0ng-Prod-Pw-1;SSL Mode=Prefer")]
    [InlineData("Host=db;Database=icehott;Username=icehott;Password=Str0ng-Prod-Pw-1")]
    [InlineData("this is not a connection string")]
    [InlineData("")]
    public void Unsafe_Database_Connections_Are_Rejected(string connection)
    {
        var values = Safe();
        values["ConnectionStrings:DefaultConnection"] = connection;
        Assert.Contains(
            Validate(values),
            x => x.StartsWith("ConnectionStrings:DefaultConnection"));
    }

    [Fact]
    public void Transport_Security_Can_Be_Relaxed_Only_Explicitly()
    {
        var values = Safe();
        values["ConnectionStrings:DefaultConnection"] =
            "Host=db.internal;Database=icehott;Username=icehott_app;Password=Str0ng-Prod-Pw-1;SSL Mode=Disable";
        values["Database:RequireTransportSecurity"] = "false";
        Assert.Empty(Validate(values));
    }

    [Theory]
    [InlineData("true")]
    [InlineData("True")]
    [InlineData("1")]
    public void Auto_Migration_Is_Rejected(string flag)
    {
        var values = Safe();
        values["Database:AutoMigrate"] = flag;
        Assert.Contains(Validate(values), x => x.StartsWith("Database:AutoMigrate"));
    }

    [Fact]
    public void Unparseable_Auto_Migration_Flag_Fails_Closed()
    {
        var values = Safe();
        values["Database:AutoMigrate"] = "maybe";
        Assert.Contains(Validate(values), x => x.StartsWith("Database:AutoMigrate"));
    }

    [Theory]
    [InlineData("Local")]
    [InlineData("local")]
    [InlineData("")]
    public void Local_Filesystem_Artifact_Storage_Is_Rejected(string provider)
    {
        var values = Safe();
        values["ArtifactStorage:Provider"] = provider;
        Assert.Contains(Validate(values), x => x.StartsWith("ArtifactStorage:Provider"));
    }

    [Fact]
    public void Local_Artifact_Storage_Requires_Explicit_Safety_Override()
    {
        var values = Safe();
        values["ArtifactStorage:Provider"] = "Local";
        values["ProductionSafety:AllowLocalArtifactStorage"] = "true";
        Assert.Empty(Validate(values));
    }

    [Fact]
    public void Object_Storage_Provider_Requires_Object_Storage_Configuration()
    {
        var values = Safe();
        values.Remove("ObjectStorage:Bucket");
        Assert.Contains(Validate(values), x => x.StartsWith("ObjectStorage"));
    }

    [Theory]
    [InlineData("Service:Role", "")]
    [InlineData("Service:Role", "All")]
    [InlineData("Service:Role", "Sidecar")]
    [InlineData("Release:GitSha", "")]
    [InlineData("Release:GitSha", "not-a-sha")]
    [InlineData("AiRuntime:BaseUrl", "http://localhost:8000")]
    [InlineData("AiRuntime:BaseUrl", "not a url")]
    [InlineData("AllowedHosts", "*")]
    [InlineData("AllowedHosts", "")]
    [InlineData("Cors:AllowedOrigins:0", "*")]
    [InlineData("Cors:AllowedOrigins:0", "http://app.icehott.example")]
    [InlineData("Cors:AllowedOrigins:0", "https://localhost:3000")]
    [InlineData("Cors:AllowedOrigins:0", "")]
    public void Unsafe_Or_Missing_Required_Values_Are_Rejected(string key, string value)
    {
        var values = Safe();
        values[key] = value;
        var prefix = key.StartsWith("Cors") ? "Cors:AllowedOrigins" : key;
        Assert.Contains(Validate(values), x => x.StartsWith(prefix));
    }

    [Fact]
    public void Missing_Cors_Origins_Are_Rejected()
    {
        var values = Safe();
        values.Remove("Cors:AllowedOrigins:0");
        Assert.Contains(Validate(values), x => x.StartsWith("Cors:AllowedOrigins"));
    }

    [Fact]
    public void Worker_Role_Is_Accepted_And_Does_Not_Require_Cors()
    {
        var values = Safe();
        values["Service:Role"] = "Worker";
        values.Remove("Cors:AllowedOrigins:0");
        values.Remove("AllowedHosts");
        Assert.Empty(Validate(values));
    }

    [Theory]
    [InlineData("abc1234")]
    [InlineData("c63a6b2337a1e17c056d5adbefc1daba83e8abf")]
    [InlineData("c63a6b2337a1e17c056d5adbefc1daba83e8abf12")]
    [InlineData("c63a6b2337a1e17c056d5adbefc1daba83e8abf1c63a6b2337a1e17c056d5adb")]
    [InlineData("c63a6b2337a1e17c056d5adbefc1daba83e8abfg")]
    public void Hosted_Release_Requires_The_Exact_Full_Git_Sha(string sha)
    {
        var values = Safe();
        values["Release:GitSha"] = sha;
        Assert.Contains(Validate(values), x => x.StartsWith("Release:GitSha"));
    }

    [Fact]
    public void Hosted_Release_Accepts_A_Full_Upper_Case_Sha()
    {
        var values = Safe();
        values["Release:GitSha"] = "C63A6B2337A1E17C056D5ADBEFC1DABA83E8ABF1";
        Assert.Empty(Validate(values));
    }

    [Fact]
    public void Worker_Does_Not_Require_Jwt_Signing_Material()
    {
        var values = Safe();
        values["Service:Role"] = "Worker";
        values.Remove("Jwt:Key");
        Assert.Empty(Validate(values));
    }

    [Fact]
    public void Worker_Still_Rejects_A_Present_But_Unsafe_Jwt_Key_Only_When_Api()
    {
        var values = Safe();
        values["Jwt:Key"] = "";
        Assert.Contains(Validate(values), x => x.StartsWith("Jwt:Key"));
    }

    [Fact]
    public void Worker_Still_Requires_Database_And_Ai_Runtime()
    {
        var values = Safe();
        values["Service:Role"] = "Worker";
        values.Remove("ConnectionStrings:DefaultConnection");
        values.Remove("AiRuntime:BaseUrl");
        var errors = Validate(values);
        Assert.Contains(errors, x => x.StartsWith("ConnectionStrings:DefaultConnection"));
        Assert.Contains(errors, x => x.StartsWith("AiRuntime:BaseUrl"));
    }

    [Fact]
    public void Api_Requires_Forwarded_Headers_From_Explicit_Trusted_Networks()
    {
        var values = Safe();
        values.Remove("ForwardedHeaders:Enabled");
        Assert.Contains(Validate(values), x => x.StartsWith("ForwardedHeaders:Enabled"));

        values = Safe();
        values["ForwardedHeaders:Enabled"] = "false";
        Assert.Contains(Validate(values), x => x.StartsWith("ForwardedHeaders:Enabled"));

        values = Safe();
        values.Remove("ForwardedHeaders:TrustedNetworks:0");
        Assert.Contains(Validate(values), x => x.StartsWith("ForwardedHeaders:TrustedNetworks"));
    }

    [Theory]
    [InlineData("0.0.0.0/0")]
    [InlineData("::/0")]
    [InlineData("10.0.0.0")]
    [InlineData("10.0.0.0/33")]
    [InlineData("not-a-network/8")]
    [InlineData("")]
    public void Trusted_Networks_Must_Be_Valid_And_Not_The_Whole_Internet(string network)
    {
        var values = Safe();
        values["ForwardedHeaders:TrustedNetworks:0"] = network;
        Assert.Contains(Validate(values), x => x.StartsWith("ForwardedHeaders:TrustedNetworks"));
    }

    [Fact]
    public void Worker_Does_Not_Require_Forwarded_Headers()
    {
        var values = Safe();
        values["Service:Role"] = "Worker";
        values.Remove("ForwardedHeaders:Enabled");
        values.Remove("ForwardedHeaders:TrustedNetworks:0");
        Assert.Empty(Validate(values));
    }

    [Fact]
    public void Errors_Never_Contain_Supplied_Secret_Values()
    {
        var values = Safe();
        values["Jwt:Key"] = "development-only-" + JwtSentinel;
        values["Database:AutoMigrate"] = "true";
        values["ConnectionStrings:DefaultConnection"] =
            $"Host=localhost;Username=postgres;Password={PasswordSentinel};SSL Mode=Disable";
        values["ArtifactStorage:Provider"] = "Local";

        var errors = Validate(values);
        Assert.NotEmpty(errors);
        foreach (var error in errors)
        {
            Assert.DoesNotContain(PasswordSentinel, error);
            Assert.DoesNotContain(JwtSentinel, error);
            Assert.DoesNotContain("localhost", error);
        }

        var exception = Assert.Throws<ProductionConfigurationException>(() =>
            ProductionConfigurationValidator.Enforce(Config(values), "Production"));
        Assert.DoesNotContain(PasswordSentinel, exception.Message);
        Assert.DoesNotContain(JwtSentinel, exception.Message);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public void Malformed_Connection_String_Does_Not_Leak_Through_Parser_Errors()
    {
        var values = Safe();
        values["ConnectionStrings:DefaultConnection"] =
            $"Host=db;Password={PasswordSentinel};BogusKeyword=1";
        var exception = Assert.Throws<ProductionConfigurationException>(() =>
            ProductionConfigurationValidator.Enforce(Config(values), "Production"));
        Assert.DoesNotContain(PasswordSentinel, exception.Message);
    }
}
