namespace OpenSmc.Charting.Models.Options
{
    public record Interaction
    {
        /// <summary>
        /// Sets which elements appear in the interaction. See Interaction Modes for details.
        /// </summary>
        public string Mode { get; init; }

        /// <summary>
        /// If true, the interaction mode only applies when the mouse position intersects an item on the chart.
        /// </summary>
        public bool? Intersect { get; init; }

        /// <summary>
        /// Can be set to 'x', 'y', or 'xy' to define which directions are used in calculating distances. Defaults to 'x' for 'index' mode and 'xy' in dataset and 'nearest' modes.
        /// </summary>
        public string Axis { get; init; }
    }
}
