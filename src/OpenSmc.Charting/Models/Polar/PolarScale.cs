using OpenSmc.Charting.Models.Options.Scales;
using Systemorph.Charting.Models.Options.Scales;

// ReSharper disable once CheckNamespace
namespace Systemorph.Charting.Models
{
    public record PolarScale : Scale
    {
        /// <summary>
        /// When true, lines are circular.
        /// </summary>
        public bool? LineArc { get; init; }
    }
}
