using MeshWeaver.Charting.Models.Options.Scales;

namespace MeshWeaver.Charting.Models.Radar
{
    public record RadarOptions : MeshWeaver.Charting.Models.Options.ChartOptions
    {
        /// <summary>
        /// The number of degrees to rotate the chart clockwise.
        /// </summary>
        public int? StartAngle { get; init; }

        public RadialScale Scale { get; init; } = null!;
    }
}
