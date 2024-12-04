using MeshWeaver.Charting.Enums;

namespace MeshWeaver.Charting.Models.Options.Scales.Ticks
{
    public record Time
    {
        /// <summary>
        /// Sets how different time units are displayed.
        /// </summary>
        public TimeDisplayFormat DisplayFormats { get; init; }

        /// <summary>
        /// If true and the unit is set to 'week', then the first day of the week will be Monday. Otherwise, it will be Sunday.
        /// </summary>
        public bool? IsoWeekday { get; init; }

        /// <summary>
        /// If defined, this will override the data maximum.
        /// </summary>
        public string Max { get; init; }

        /// <summary>
        /// If defined, this will override the data minimum.
        /// </summary>
        public string Min { get; init; }

        /// <summary>
        /// If defined as a string, it is interpreted as a custom format to be used by moment to parse the date. If this is a function, it must return a moment.js object given the appropriate data value.
        /// </summary>
        public object Parser { get; init; }

        /// <summary>
        /// If defined, dates will be rounded to the start of this unit.
        /// </summary>
        public string Round { get; init; }

        /// <summary>
        /// The moment js format string to use for the tooltip.
        /// </summary>
        public string TooltipFormat { get; init; }

        /// <summary>
        /// If defined, will force the unit to be a certain type.
        /// </summary>
        public TimeIntervals Unit { get; init; }

        /// <summary>
        /// The number of units between grid lines.
        /// </summary>
        public int? StepSize { get; init; }

        /// <summary>
        /// The minimum display format to be used for a time unit.
        /// </summary>
        public string MinUnit { get; init; }
    }
}
