using MeshWeaver.Charting.Models.Options.Scales;

namespace MeshWeaver.Charting.Models.Polar
{
    public record PolarScale : Scale
    {
        /// <summary>
        /// When true, lines are circular.
        /// </summary>
        public bool? LineArc { get; init; }
    }
}
