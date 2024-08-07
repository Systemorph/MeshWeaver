using MeshWeaver.Charting.Models.Options.Animation;

namespace MeshWeaver.Charting.Models
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
