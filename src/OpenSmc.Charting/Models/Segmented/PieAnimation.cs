using OpenSmc.Charting.Models.Options.Animation;

// ReSharper disable once CheckNamespace
namespace Systemorph.Charting.Models
{
    public record PieAnimation : Animation
    {
        /// <summary>
        /// If true, will animate the rotation of the chart.
        /// </summary>
        public bool? AnimateRotate { get; init; }

        /// <summary>
        /// If true, will animate scaling of the chart from the centre.
        /// </summary>
        public bool? AnimateScale { get; init; }
    }
}
