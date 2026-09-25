using ICEHOTT.Application.Knowledge;

namespace ICEHOTT.Tests;

public sealed class LiveProviderBenchmarkGuardTests
{
    [Fact]
    public void Preflight_Accepts_Explicitly_Authorized_Benchmark_Database()
    {
        LiveProviderBenchmarkGuard.ValidatePreflight(
            "icehott_benchmark_20260925",
            LiveProviderBenchmarkGuard.RequiredOptIn,
            "test-key-not-real",
            approvedMaxCostUsd: 1.00m,
            usdPerMillionInputTokens: 0.02m);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("yes")]
    public void Preflight_Rejects_Missing_Or_Weak_OptIn(string? optIn)
    {
        Assert.Throws<InvalidOperationException>(() =>
            LiveProviderBenchmarkGuard.ValidatePreflight(
                "icehott_benchmark",
                optIn,
                "test-key-not-real",
                1.00m,
                0.02m));
    }

    [Fact]
    public void Preflight_Rejects_NonBenchmark_Database()
    {
        Assert.Throws<InvalidOperationException>(() =>
            LiveProviderBenchmarkGuard.ValidatePreflight(
                "icehott_prod",
                LiveProviderBenchmarkGuard.RequiredOptIn,
                "test-key-not-real",
                1.00m,
                0.02m));
    }

    [Fact]
    public void Preflight_Rejects_Missing_Key_Or_Cost_Metadata()
    {
        Assert.Throws<InvalidOperationException>(() =>
            LiveProviderBenchmarkGuard.ValidatePreflight(
                "icehott_benchmark",
                LiveProviderBenchmarkGuard.RequiredOptIn,
                "",
                1.00m,
                0.02m));

        Assert.Throws<InvalidOperationException>(() =>
            LiveProviderBenchmarkGuard.ValidatePreflight(
                "icehott_benchmark",
                LiveProviderBenchmarkGuard.RequiredOptIn,
                "test-key-not-real",
                0,
                0.02m));

        Assert.Throws<InvalidOperationException>(() =>
            LiveProviderBenchmarkGuard.ValidatePreflight(
                "icehott_benchmark",
                LiveProviderBenchmarkGuard.RequiredOptIn,
                "test-key-not-real",
                1.00m,
                0));
    }

    [Fact]
    public void FreshDatabase_Guard_Rejects_Existing_Product_Data()
    {
        LiveProviderBenchmarkGuard.ValidateFreshDatabase(0, 0, 0);

        Assert.Throws<InvalidOperationException>(() =>
            LiveProviderBenchmarkGuard.ValidateFreshDatabase(1, 0, 0));
        Assert.Throws<InvalidOperationException>(() =>
            LiveProviderBenchmarkGuard.ValidateFreshDatabase(0, 1, 0));
        Assert.Throws<InvalidOperationException>(() =>
            LiveProviderBenchmarkGuard.ValidateFreshDatabase(0, 0, 1));
    }

    [Fact]
    public void Estimated_Cost_Guard_Fails_Before_Live_Run_When_Cap_Is_Too_Low()
    {
        LiveProviderBenchmarkGuard.EnsureEstimatedMaximumWithinApprovedCost(
            0.01m,
            0.02m);

        Assert.Throws<InvalidOperationException>(() =>
            LiveProviderBenchmarkGuard.EnsureEstimatedMaximumWithinApprovedCost(
                0.03m,
                0.02m));
    }

    [Fact]
    public void Cost_Guard_Requires_Measured_Usage_Within_Cap()
    {
        LiveProviderBenchmarkGuard.EnsureWithinApprovedCost(0.01m, 0.02m);

        Assert.Throws<InvalidOperationException>(() =>
            LiveProviderBenchmarkGuard.EnsureWithinApprovedCost(null, 0.02m));
        Assert.Throws<InvalidOperationException>(() =>
            LiveProviderBenchmarkGuard.EnsureWithinApprovedCost(0.03m, 0.02m));
    }
}
