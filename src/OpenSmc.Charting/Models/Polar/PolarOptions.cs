namespace OpenSmc.Charting.Models
{
    public record PolarOptions : Options.Options
    {
        /// <summary>
        /// Sets the starting angle for the first item in a dataset
        /// </summary>
        public double? StartAngle { get; init; }
    }
}
