namespace ICEHOTT.Application.Knowledge;

public static class LiveProviderBenchmarkGuard
{
    public const string RequiredOptIn =
        "I_UNDERSTAND_THIS_USES_PAID_API";

    public static void ValidatePreflight(
        string? databaseName,
        string? optIn,
        string? apiKey,
        decimal approvedMaxCostUsd,
        decimal usdPerMillionInputTokens)
    {
        if (!string.Equals(
                optIn,
                RequiredOptIn,
                StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Live provider benchmark requires explicit paid-API opt-in.");

        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException(
                "Live provider benchmark requires a server-side API key.");

        if (approvedMaxCostUsd <= 0)
            throw new InvalidOperationException(
                "Live provider benchmark requires a positive approved cost cap.");

        if (usdPerMillionInputTokens <= 0)
            throw new InvalidOperationException(
                "Live provider benchmark requires positive provider pricing metadata.");

        if (string.IsNullOrWhiteSpace(databaseName) ||
            !databaseName.Contains(
                "benchmark",
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Live provider benchmark must use a dedicated database whose name contains 'benchmark'.");
    }

    public static void ValidateFreshDatabase(
        long userCount,
        long workspaceCount,
        long documentCount)
    {
        if (userCount != 0 ||
            workspaceCount != 0 ||
            documentCount != 0)
            throw new InvalidOperationException(
                "Benchmark database must be fresh and contain no users, workspaces, or knowledge documents.");
    }

    public static void EnsureEstimatedMaximumWithinApprovedCost(
        decimal estimatedMaximumCostUsd,
        decimal approvedMaxCostUsd)
    {
        if (estimatedMaximumCostUsd <= 0)
            throw new InvalidOperationException(
                "Benchmark maximum-cost estimate must be positive.");

        if (estimatedMaximumCostUsd > approvedMaxCostUsd)
            throw new InvalidOperationException(
                $"Conservative benchmark cost estimate {estimatedMaximumCostUsd:F6} USD exceeds the approved cap {approvedMaxCostUsd:F6} USD.");
    }

    public static void EnsureWithinApprovedCost(
        decimal? measuredCostUsd,
        decimal approvedMaxCostUsd)
    {
        if (measuredCostUsd is null)
            throw new InvalidOperationException(
                "Provider did not return measurable usage; benchmark cost could not be verified.");

        if (measuredCostUsd.Value > approvedMaxCostUsd)
            throw new InvalidOperationException(
                $"Measured benchmark cost {measuredCostUsd.Value:F6} USD exceeded the approved cap {approvedMaxCostUsd:F6} USD.");
    }
}
