using OpenSmc.Charting.Models.Options.Scales;

namespace OpenSmc.Charting.Models
{
    public record RadarOptions : OpenSmc.Charting.Models.Options.ChartOptions
    {
        /// <summary>
        /// The number of degrees to rotate the chart clockwise.
        /// </summary>
        public int? StartAngle { get; init; }

        public RadialScale Scale { get; init; }
    }
}
