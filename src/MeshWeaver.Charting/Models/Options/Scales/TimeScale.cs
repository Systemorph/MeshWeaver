using MeshWeaver.Charting.Models.Options.Scales.Ticks;

namespace MeshWeaver.Charting.Models.Options.Scales
{
    public record TimeScale : CartesianScale
    {
        /// <summary>
        /// How data is plotted.
        /// </summary>
        public string Distribution { get; init; } = null!;

        public Time Time { get; init; } = null!;

        public TimeScale()
        {
            Type = "time";
        }
    }
}
