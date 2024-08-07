namespace MeshWeaver.Charting.Models.Options.Scales
{
    public record CartesianCategoryScale : CartesianScale
    {
        /// <summary>
        /// The minimum item to display.
        /// </summary>
        public new object Min { get; init; }

        /// <summary>
        /// The maximum item to display.
        /// </summary>
        public new object Max { get; init; }

        /// <summary>
        /// An array of labels to display. When an individual label is an array of strings, each item is rendered on a new line.
        /// </summary>
        public IEnumerable<string> Labels { get; init; }
    }
}
