namespace MeshWeaver.Charting.Models.Options.Scales.Ticks
{
    // https://www.chartjs.org/docs/3.7.1/axes/cartesian/linear.html#linear-axis-specific-tick-options
    public record CartesianLinearTick : CartesianTick
    {
        /// <summary>
        /// The number of ticks to generate. If specified, this overrides the automatic generation.
        /// </summary>
        public int? Count { get; init; }

        /// <summary>
        /// The Intl.NumberFormat options used by the default label formatter.
        /// </summary>
        public object Format { get; init; }

        /// <summary>
        /// If defined and stepSize is not specified, the step size will be rounded to this many decimal places.
        /// </summary>
        public int? Precision { get; init; }

        /// <summary>
        /// User-defined fixed step size for the scale.
        /// </summary>
        public double? StepSize { get; init; }
    }
}
