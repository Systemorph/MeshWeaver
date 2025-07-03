namespace MeshWeaver.Charting.Models.Options.Scales
{
    public record CartesianLinearScale : CartesianScale
    {
        /// <summary>
        /// If true, scale will include 0 if it is not already included.
        /// </summary>
        public bool? BeginAtZero { get; init; }

        /// <summary>
        /// Percentage (string ending with %) or amount (number) for added room in the scale range above and below data.
        /// </summary>
        public object Grace { get; init; } = null!;
    }
}
