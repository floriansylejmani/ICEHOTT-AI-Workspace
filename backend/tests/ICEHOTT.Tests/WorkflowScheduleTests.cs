using ICEHOTT.Application.Workflows;

namespace ICEHOTT.Tests;

public sealed class WorkflowScheduleTests
{
    private readonly WorkflowScheduleCalculator _calculator = new();

    [Fact]
    public void Five_Minute_Schedule_Computes_Next_Occurrence()
    {
        var after = new DateTimeOffset(
            2026, 9, 26, 20, 2, 0, TimeSpan.Zero);

        var result = _calculator.Validate(
            "*/5 * * * *",
            "UTC",
            after,
            TimeSpan.FromMinutes(5));

        Assert.True(result.IsValid);
        Assert.Equal(
            new DateTimeOffset(
                2026, 9, 26, 20, 5, 0, TimeSpan.Zero),
            result.NextRunAtUtc);
    }

    [Fact]
    public void Too_Frequent_Schedule_Is_Rejected()
    {
        var result = _calculator.Validate(
            "* * * * *",
            "UTC",
            new DateTimeOffset(
                2026, 9, 26, 20, 0, 0, TimeSpan.Zero),
            TimeSpan.FromMinutes(5));

        Assert.False(result.IsValid);
        Assert.Equal("schedule_too_frequent", result.ErrorCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not cron")]
    [InlineData("*/0 * * * *")]
    [InlineData("61 * * * *")]
    [InlineData("* 24 * * *")]
    [InlineData("* * 32 * *")]
    public void Invalid_Cron_Is_Rejected(string expression)
    {
        var result = _calculator.Validate(
            expression,
            "UTC",
            DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(5));

        Assert.False(result.IsValid);
        Assert.Equal("invalid_schedule", result.ErrorCode);
    }

    [Fact]
    public void Invalid_TimeZone_Is_Rejected()
    {
        var result = _calculator.Validate(
            "0 * * * *",
            "Mars/Olympus_Mons",
            DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(5));

        Assert.False(result.IsValid);
        Assert.Equal("invalid_time_zone", result.ErrorCode);
    }

    [Fact]
    public void Ambiguous_Dst_Local_Time_Fires_Only_At_Earlier_Utc_Instant()
    {
        var before = new DateTimeOffset(
            2026, 10, 24, 0, 0, 0, TimeSpan.Zero);

        var first = _calculator.GetNextOccurrence(
            "30 2 25 10 *",
            "Europe/Berlin",
            before);

        Assert.Equal(
            new DateTimeOffset(
                2026, 10, 25, 0, 30, 0, TimeSpan.Zero),
            first);

        var next = _calculator.GetNextOccurrence(
            "30 2 25 10 *",
            "Europe/Berlin",
            first!.Value);

        Assert.NotNull(next);
        Assert.True(next > first.Value.AddDays(300));
    }

    [Fact]
    public void Skipped_Dst_Local_Time_Advances_To_Next_Valid_Occurrence()
    {
        var before = new DateTimeOffset(
            2026, 3, 29, 0, 0, 0, TimeSpan.Zero);

        var next = _calculator.GetNextOccurrence(
            "30 2 * * *",
            "Europe/Berlin",
            before);

        Assert.Equal(
            new DateTimeOffset(
                2026, 3, 30, 0, 30, 0, TimeSpan.Zero),
            next);
    }

    [Fact]
    public void Full_Range_Step_Is_Treated_As_Wildcard_For_Day_Of_Week()
    {
        var after = new DateTimeOffset(
            2026, 9, 16, 10, 0, 0, TimeSpan.Zero);

        var next = _calculator.GetNextOccurrence(
            "0 9 15 * */1",
            "UTC",
            after);

        Assert.Equal(
            new DateTimeOffset(
                2026, 10, 15, 9, 0, 0, TimeSpan.Zero),
            next);
    }

    [Fact]
    public void Lists_Ranges_And_Steps_Are_Supported()
    {
        var after = new DateTimeOffset(
            2026, 9, 28, 8, 8, 0, TimeSpan.Zero);

        var next = _calculator.GetNextOccurrence(
            "10-20/5 8,9 * * 1-5",
            "UTC",
            after);

        Assert.Equal(
            new DateTimeOffset(
                2026, 9, 28, 8, 10, 0, TimeSpan.Zero),
            next);
    }
}
