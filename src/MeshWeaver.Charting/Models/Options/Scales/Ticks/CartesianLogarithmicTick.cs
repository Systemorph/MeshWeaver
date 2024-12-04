namespace MeshWeaver.Charting.Models.Options.Scales.Ticks
{
    // https://www.chartjs.org/docs/3.7.1/axes/cartesian/logarithmic.html#logarithmic-axis-specific-options
    public record CartesianLogarithmicTick : CartesianTick
    {
        /// <summary>
        /// The Intl.NumberFormat options used by the default label formatter.
        /// </summary>
        public object Format { get; init; }
    }
}
