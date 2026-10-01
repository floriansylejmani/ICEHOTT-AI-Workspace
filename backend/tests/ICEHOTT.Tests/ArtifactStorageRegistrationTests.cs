using Amazon.S3;
using ICEHOTT.API.Hosting;
using ICEHOTT.Application.Abstractions;
using ICEHOTT.Infrastructure.Artifacts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ICEHOTT.Tests;

public sealed class ArtifactStorageRegistrationTests
{
    private static Dictionary<string, string?> ValidS3() => new()
    {
        ["ArtifactStorage:Provider"] = "S3",
        ["ObjectStorage:Endpoint"] = "https://objects.example",
        ["ObjectStorage:Bucket"] = "icehott-artifacts",
        ["ObjectStorage:Region"] = "eu-west-1",
        ["ObjectStorage:AccessKeyId"] = "access-sentinel",
        ["ObjectStorage:SecretAccessKey"] = "secret-sentinel",
        ["ObjectStorage:ForcePathStyle"] = "false",
        ["ObjectStorage:Prefix"] = "icehott/prod"
    };

    private static ServiceProvider Build(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        var artifact = configuration
            .GetSection(ArtifactStorageOptions.SectionName)
            .Get<ArtifactStorageOptions>() ?? new ArtifactStorageOptions();

        var services = new ServiceCollection();
        services.Configure<ArtifactStorageOptions>(
            configuration.GetSection(ArtifactStorageOptions.SectionName));
        services.Configure<ObjectStorageOptions>(
            configuration.GetSection(ObjectStorageOptions.SectionName));
        services.AddArtifactStore(configuration, artifact);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Local_Provider_Registers_Local_Store_And_No_S3_Client()
    {
        using var provider = Build(new()
        {
            ["ArtifactStorage:Provider"] = "Local",
            ["ArtifactStorage:RootPath"] = Path.GetTempPath()
        });

        Assert.IsType<LocalArtifactStore>(
            Assert.IsType<InstrumentedArtifactStore>(provider.GetRequiredService<IArtifactStore>()).Inner);
        Assert.Null(provider.GetService<IAmazonS3>());
    }

    [Fact]
    public void Local_Provider_Requires_RootPath()
    {
        Assert.Throws<InvalidOperationException>(() => Build(new()
        {
            ["ArtifactStorage:Provider"] = "Local",
            ["ArtifactStorage:RootPath"] = " "
        }));
    }

    [Fact]
    public void S3_Provider_Does_Not_Require_Local_RootPath_And_Registers_S3_Store()
    {
        var values = ValidS3();
        values["ArtifactStorage:RootPath"] = "";
        using var provider = Build(values);

        Assert.IsType<S3ArtifactStore>(
            Assert.IsType<InstrumentedArtifactStore>(provider.GetRequiredService<IArtifactStore>()).Inner);

        var client = Assert.IsType<AmazonS3Client>(provider.GetRequiredService<IAmazonS3>());
        Assert.Same(client, provider.GetRequiredService<IAmazonS3>());
        var config = Assert.IsType<AmazonS3Config>(client.Config);
        Assert.Equal("https://objects.example", config.ServiceURL.TrimEnd('/'));
        Assert.False(config.ForcePathStyle);
        Assert.Equal("eu-west-1", config.AuthenticationRegion);
    }

    [Fact]
    public void S3_Client_Is_Owned_And_Disposed_By_The_Container()
    {
        var provider = Build(ValidS3());
        var client = provider.GetRequiredService<IAmazonS3>();

        provider.Dispose();

        Assert.ThrowsAny<ObjectDisposedException>(() =>
            client.ListBucketsAsync().GetAwaiter().GetResult());
    }

    [Theory]
    [InlineData("Azure")]
    [InlineData("")]
    [InlineData("s4")]
    public void Unknown_Provider_Fails_Startup(string providerName)
    {
        var values = ValidS3();
        values["ArtifactStorage:Provider"] = providerName;
        values["ArtifactStorage:RootPath"] = Path.GetTempPath();

        Assert.Throws<InvalidOperationException>(() => Build(values));
    }

    [Theory]
    [InlineData("ObjectStorage:Endpoint", "not-a-url")]
    [InlineData("ObjectStorage:Endpoint", "https://user:pw-sentinel@objects.example")]
    [InlineData("ObjectStorage:Bucket", "BAD_BUCKET")]
    [InlineData("ObjectStorage:Region", "")]
    [InlineData("ObjectStorage:AccessKeyId", "")]
    [InlineData("ObjectStorage:SecretAccessKey", "")]
    [InlineData("ObjectStorage:Prefix", "../escape")]
    public void S3_Provider_Fails_Startup_On_Invalid_Object_Storage_Settings(
        string key,
        string value)
    {
        var values = ValidS3();
        values[key] = value;

        var exception = Assert.Throws<InvalidOperationException>(() => Build(values));

        Assert.DoesNotContain("secret-sentinel", exception.Message);
        Assert.DoesNotContain("access-sentinel", exception.Message);
        Assert.DoesNotContain("pw-sentinel", exception.Message);
    }
}
