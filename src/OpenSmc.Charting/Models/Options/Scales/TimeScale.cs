namespace OpenSmc.Charting.Models.Options.Scales
{
    public record TimeScale : CartesianScale
    {
        /// <summary>
        /// How data is plotted.
        /// </summary>
        public string Distribution { get; init; }

        public Time Time { get; init; }

        public TimeScale()
        {
            Type = "time";
        }
    }
}
