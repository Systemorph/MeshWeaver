namespace MeshWeaver.Charting.Models.Options.Scales.Ticks
{
    public record CategoryTick : CartesianTick
    {
        /// <summary>
        /// The minimum item to display.
        /// </summary>
        public string Min { get; init; }

        /// <summary>
        /// The maximum item to display.
        /// </summary>
        public string Max { get; init; }

        /// <summary>
        /// An array of labels to display.
        /// </summary>
        public List<string> Labels { get; init; }
    }
}
