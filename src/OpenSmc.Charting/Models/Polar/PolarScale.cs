using OpenSmc.Charting.Models.Options.Scales;

namespace OpenSmc.Charting.Models
{
    public record PolarScale : Scale
    {
        /// <summary>
        /// When true, lines are circular.
        /// </summary>
        public bool? LineArc { get; init; }
    }
}
