﻿using MeshWeaver.Charting.Enums;

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

        public string Millisecond { get; init; }

        public string Second { get; init; }

        public string Minute { get; init; }

        public string Hour { get; init; }

        public string Day { get; init; }

        public string Week { get; init; }

        public string Month { get; init; }

        public string Quarter { get; init; }

        public string Year { get; init; }
    }
}
