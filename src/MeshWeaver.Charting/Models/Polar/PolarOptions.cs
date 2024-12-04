namespace MeshWeaver.Charting.Models.Polar
{
    public record PolarOptions : Options.ChartOptions
    {
        /// <summary>
        /// Sets the starting angle for the first item in a dataset
        /// </summary>
        public double? StartAngle { get; init; }
    }
}
