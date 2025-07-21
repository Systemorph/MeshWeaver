using MeshWeaver.Charting.Enums;

namespace MeshWeaver.Charting.Models.Options.Scales.Ticks
{
    public record TimeDisplayFormat
    {
        public TimeDisplayFormat(TimeIntervals? timeUnit, string timeFormat)
        {
            switch (timeUnit)
            {
                case TimeIntervals.Millisecond:
                    Millisecond = timeFormat;
                    break;
                case TimeIntervals.Second:
                    Second = timeFormat;
                    break;
                case TimeIntervals.Minute:
                    Minute = timeFormat;
                    break;
                case TimeIntervals.Hour:
                    Hour = timeFormat;
                    break;
                case TimeIntervals.Day:
                    Day = timeFormat;
                    break;
                case TimeIntervals.Week:
                    Week = timeFormat;
                    break;
                case TimeIntervals.Month:
                    Month = timeFormat;
                    break;
                case TimeIntervals.Quarter:
                    Quarter = timeFormat;
                    break;
                case TimeIntervals.Year:
                    Year = timeFormat;
                    break;
                default:
                    Millisecond = timeFormat;
                    break;
            }
        }

        public string Millisecond { get; init; } = null!;

        public string Second { get; init; } = null!;

        public string Minute { get; init; } = null!;

        public string Hour { get; init; } = null!;

        public string Day { get; init; } = null!;

        public string Week { get; init; } = null!;

        public string Month { get; init; } = null!;

        public string Quarter { get; init; } = null!;

        public string Year { get; init; } = null!;
    }
}
