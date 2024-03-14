using OpenSmc.Charting.Models.Options.Scales;
using Systemorph.Charting.Models.Options.Scales;

// ReSharper disable once CheckNamespace
namespace Systemorph.Charting.Models
{
    public record RadarOptions : OpenSmc.Charting.Models.Options.Options
    {
        /// <summary>
        /// The number of degrees to rotate the chart clockwise.
        /// </summary>
        public int? StartAngle { get; init; }

        public RadialScale Scale { get; init; }
    }
}
