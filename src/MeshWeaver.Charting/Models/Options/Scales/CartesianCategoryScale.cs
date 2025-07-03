using System.Text.Json.Serialization;

namespace MeshWeaver.Charting.Models.Options.Scales
{
    public record CartesianCategoryScale : CartesianScale
    {
        /// <summary>
        /// The minimum item to display.
        /// </summary>
        public new object Min { get; init; } = null!;

        /// <summary>
        /// The maximum item to display.
        /// </summary>
        public new object Max { get; init; } = null!;

        /// <summary>
        /// An array of labels to display. When an individual label is an array of strings, each item is rendered on a new line.
        /// </summary>
        [JsonPropertyName("labels")]
        public IEnumerable<string> Labels { get; init; } = null!;
    }
}
