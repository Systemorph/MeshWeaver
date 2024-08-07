using MeshWeaver.Charting.Models.Options.Animation;

namespace MeshWeaver.Charting.Models
{
    public record PolarAnimation : Animation
    {
        /// <summary>
        /// If true, will animate the rotation of the chart.
        /// </summary>
        public bool? AnimateRotate { get; init; }

        /// <summary>
        /// If true, will animate scaling the chart.
        /// </summary>
        public bool? AnimateScale { get; init; }
    }
}
