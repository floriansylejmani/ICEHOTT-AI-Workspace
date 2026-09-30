using ICEHOTT.Infrastructure.Artifacts;

namespace ICEHOTT.Tests;

public sealed class ObjectStorageOptionsTests
{
    [Fact]
    public void Defaults_Are_Safe_And_Non_Secret()
    {
        var options = new ObjectStorageOptions();

        Assert.Equal("us-east-1", options.Region);
        Assert.True(options.ForcePathStyle);
        Assert.Equal(string.Empty, options.Endpoint);
        Assert.Equal(string.Empty, options.Bucket);
        Assert.Equal(string.Empty, options.AccessKeyId);
        Assert.Equal(string.Empty, options.SecretAccessKey);
        Assert.Equal(string.Empty, options.Prefix);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("icehott", "icehott")]
    [InlineData("icehott/production", "icehott/production")]
    [InlineData("  tenant_1/artifacts.v1  ", "tenant_1/artifacts.v1")]
    public void Prefix_Normalization_Accepts_Safe_Values(string? value, string expected)
    {
        Assert.True(ObjectStorageOptions.TryNormalizePrefix(value, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("/icehott")]
    [InlineData("icehott/")]
    [InlineData("icehott//prod")]
    [InlineData("icehott/../prod")]
    [InlineData("icehott/./prod")]
    [InlineData("icehott\\prod")]
    [InlineData("icehott/prod space")]
    [InlineData("icehott/\u0001prod")]
    public void Prefix_Normalization_Rejects_Unsafe_Values(string value)
    {
        Assert.False(ObjectStorageOptions.TryNormalizePrefix(value, out _));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("icehott-prod-artifacts")]
    [InlineData("icehott.prod.artifacts")]
    [InlineData("a1-b2-c3")]
    public void Bucket_Validation_Accepts_Conservative_S3_Names(string bucket)
    {
        Assert.True(ObjectStorageOptions.IsValidBucketName(bucket));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ABCD")]
    [InlineData("ab")]
    [InlineData("-abc")]
    [InlineData("abc-")]
    [InlineData(".abc")]
    [InlineData("abc.")]
    [InlineData("abc..def")]
    [InlineData("abc.-def")]
    [InlineData("abc-.def")]
    [InlineData("192.168.1.1")]
    [InlineData("bucket_with_underscore")]
    public void Bucket_Validation_Rejects_Unsafe_Or_Nonportable_Names(string bucket)
    {
        Assert.False(ObjectStorageOptions.IsValidBucketName(bucket));
    }
}
