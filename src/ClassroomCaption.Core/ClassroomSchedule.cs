namespace ClassroomCaption.Core;

internal static class ClassroomSchedule
{
    private static readonly (TimeOnly Start, TimeOnly End)[] Windows =
    [
        (new(8, 7), new(9, 48)),
        (new(10, 12), new(11, 53)),
        (new(14, 27), new(16, 8)),
        (new(16, 27), new(18, 8))
    ];

    internal static bool IsClassTime(DateTimeOffset instant)
    {
        var time = TimeOnly.FromDateTime(instant.ToOffset(TimeSpan.FromHours(8)).DateTime);
        return Windows.Any(window => time >= window.Start && time < window.End);
    }
}
