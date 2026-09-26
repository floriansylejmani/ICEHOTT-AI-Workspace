namespace ICEHOTT.Application.Workflows;

public sealed record WorkflowScheduleValidationResult(
    bool IsValid,
    string? ErrorCode,
    DateTimeOffset? NextRunAtUtc);

public interface IWorkflowScheduleCalculator
{
    WorkflowScheduleValidationResult Validate(
        string scheduleExpression,
        string timeZoneId,
        DateTimeOffset afterUtc,
        TimeSpan minimumInterval);

    DateTimeOffset? GetNextOccurrence(
        string scheduleExpression,
        string timeZoneId,
        DateTimeOffset afterUtc);
}

public sealed class WorkflowScheduleCalculator : IWorkflowScheduleCalculator
{
    private const int SearchHorizonDays = 366 * 5;

    public WorkflowScheduleValidationResult Validate(
        string scheduleExpression,
        string timeZoneId,
        DateTimeOffset afterUtc,
        TimeSpan minimumInterval)
    {
        if (!TryResolveTimeZone(timeZoneId, out var timeZone))
            return new(false, "invalid_time_zone", null);

        if (!CronPattern.TryParse(scheduleExpression, out var cron))
            return new(false, "invalid_schedule", null);

        var first = FindNext(cron!, timeZone!, afterUtc);
        if (first is null)
            return new(false, "schedule_has_no_occurrence", null);

        var second = FindNext(cron!, timeZone!, first.Value);
        if (second is null)
            return new(false, "schedule_has_no_occurrence", null);

        var third = FindNext(cron!, timeZone!, second.Value);
        if (third is null)
            return new(false, "schedule_has_no_occurrence", null);

        if (second.Value - first.Value < minimumInterval ||
            third.Value - second.Value < minimumInterval)
            return new(false, "schedule_too_frequent", null);

        return new(true, null, first);
    }

    public DateTimeOffset? GetNextOccurrence(
        string scheduleExpression,
        string timeZoneId,
        DateTimeOffset afterUtc)
    {
        if (!TryResolveTimeZone(timeZoneId, out var timeZone) ||
            !CronPattern.TryParse(scheduleExpression, out var cron))
            return null;

        return FindNext(cron!, timeZone!, afterUtc);
    }

    private static DateTimeOffset? FindNext(
        CronPattern cron,
        TimeZoneInfo timeZone,
        DateTimeOffset afterUtc)
    {
        var localAfter = TimeZoneInfo.ConvertTime(afterUtc, timeZone);
        var local = new DateTime(
            localAfter.Year,
            localAfter.Month,
            localAfter.Day,
            localAfter.Hour,
            localAfter.Minute,
            0,
            DateTimeKind.Unspecified).AddMinutes(1);
        var end = local.AddDays(SearchHorizonDays);

        while (local <= end)
        {
            if (cron.Matches(local) &&
                !timeZone.IsInvalidTime(local))
            {
                var occurrence = ToUtc(local, timeZone);
                if (occurrence > afterUtc)
                    return occurrence;
            }

            local = local.AddMinutes(1);
        }

        return null;
    }

    private static DateTimeOffset ToUtc(
        DateTime local,
        TimeZoneInfo timeZone)
    {
        if (timeZone.IsAmbiguousTime(local))
        {
            var candidates = timeZone
                .GetAmbiguousTimeOffsets(local)
                .Select(offset =>
                    new DateTimeOffset(local, offset).ToUniversalTime())
                .OrderBy(x => x)
                .ToArray();

            return candidates[0];
        }

        var utc = TimeZoneInfo.ConvertTimeToUtc(local, timeZone);
        return new DateTimeOffset(utc, TimeSpan.Zero);
    }

    private static bool TryResolveTimeZone(
        string timeZoneId,
        out TimeZoneInfo? timeZone)
    {
        timeZone = null;
        if (string.IsNullOrWhiteSpace(timeZoneId) ||
            timeZoneId.Length > 120)
            return false;

        try
        {
            timeZone = TimeZoneInfo.FindSystemTimeZoneById(
                timeZoneId.Trim());
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            return false;
        }
    }

    private sealed class CronPattern
    {
        private readonly CronField _minute;
        private readonly CronField _hour;
        private readonly CronField _dayOfMonth;
        private readonly CronField _month;
        private readonly CronField _dayOfWeek;

        private CronPattern(
            CronField minute,
            CronField hour,
            CronField dayOfMonth,
            CronField month,
            CronField dayOfWeek)
        {
            _minute = minute;
            _hour = hour;
            _dayOfMonth = dayOfMonth;
            _month = month;
            _dayOfWeek = dayOfWeek;
        }

        public bool Matches(DateTime local)
        {
            if (!_minute.Contains(local.Minute) ||
                !_hour.Contains(local.Hour) ||
                !_month.Contains(local.Month))
                return false;

            var dom = _dayOfMonth.Contains(local.Day);
            var dow = _dayOfWeek.Contains((int)local.DayOfWeek);

            if (_dayOfMonth.IsWildcard && _dayOfWeek.IsWildcard)
                return true;
            if (_dayOfMonth.IsWildcard)
                return dow;
            if (_dayOfWeek.IsWildcard)
                return dom;

            return dom || dow;
        }

        public static bool TryParse(
            string expression,
            out CronPattern? pattern)
        {
            pattern = null;
            if (string.IsNullOrWhiteSpace(expression) ||
                expression.Length > 200)
                return false;

            var parts = expression.Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries);
            if (parts.Length != 5)
                return false;

            if (!CronField.TryParse(parts[0], 0, 59, false, out var minute) ||
                !CronField.TryParse(parts[1], 0, 23, false, out var hour) ||
                !CronField.TryParse(parts[2], 1, 31, false, out var dayOfMonth) ||
                !CronField.TryParse(parts[3], 1, 12, false, out var month) ||
                !CronField.TryParse(parts[4], 0, 7, true, out var dayOfWeek))
                return false;

            pattern = new(
                minute!,
                hour!,
                dayOfMonth!,
                month!,
                dayOfWeek!);
            return true;
        }
    }

    private sealed class CronField
    {
        private readonly HashSet<int> _values;

        private CronField(
            HashSet<int> values,
            bool isWildcard)
        {
            _values = values;
            IsWildcard = isWildcard;
        }

        public bool IsWildcard { get; }

        public bool Contains(int value) => _values.Contains(value);

        public static bool TryParse(
            string text,
            int min,
            int max,
            bool normalizeSunday,
            out CronField? field)
        {
            field = null;
            if (string.IsNullOrWhiteSpace(text))
                return false;

            var values = new HashSet<int>();

            foreach (var token in text.Split(
                         ',',
                         StringSplitOptions.RemoveEmptyEntries |
                         StringSplitOptions.TrimEntries))
            {
                if (!TryAddToken(
                        token,
                        min,
                        max,
                        normalizeSunday,
                        values))
                    return false;
            }

            if (values.Count == 0)
                return false;

            var expectedValueCount = normalizeSunday
                ? max - min
                : max - min + 1;
            var wildcard = values.Count == expectedValueCount;

            field = new(values, wildcard);
            return true;
        }

        private static bool TryAddToken(
            string token,
            int min,
            int max,
            bool normalizeSunday,
            HashSet<int> values)
        {
            var slashParts = token.Split('/');
            if (slashParts.Length > 2)
                return false;

            var basePart = slashParts[0];
            var step = 1;
            if (slashParts.Length == 2 &&
                (!int.TryParse(slashParts[1], out step) || step < 1))
                return false;

            int start;
            int end;

            if (basePart == "*")
            {
                start = min;
                end = max;
            }
            else if (basePart.Contains('-'))
            {
                var range = basePart.Split('-');
                if (range.Length != 2 ||
                    !int.TryParse(range[0], out start) ||
                    !int.TryParse(range[1], out end) ||
                    start < min ||
                    end > max ||
                    start > end)
                    return false;
            }
            else
            {
                if (!int.TryParse(basePart, out start) ||
                    start < min ||
                    start > max)
                    return false;
                end = start;
            }

            for (var value = start; value <= end; value += step)
            {
                var normalized =
                    normalizeSunday && value == 7
                        ? 0
                        : value;
                values.Add(normalized);

                if (end - value < step)
                    break;
            }

            return true;
        }
    }
}
