namespace MeshWeaver.Charting.Models
{
    public record PolarOptions : Options.ChartOptions
    {
        /// <summary>
        /// Sets the starting angle for the first item in a dataset
        /// </summary>
        public double? StartAngle { get; init; }
    }
}
