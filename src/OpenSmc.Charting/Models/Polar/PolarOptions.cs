// ReSharper disable once CheckNamespace
namespace Systemorph.Charting.Models
{
    public record PolarOptions : OpenSmc.Charting.Models.Options.Options
    {
        /// <summary>
        /// Sets the starting angle for the first item in a dataset
        /// </summary>
        public double? StartAngle { get; init; }
    }
}
