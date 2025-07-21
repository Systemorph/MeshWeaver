﻿namespace MeshWeaver.Charting.Plugins.Zoom
{
    public record Pan
    {
        /// <summary>
        /// Boolean to enable panning.
        /// </summary>
        public bool? Enabled { get; set; }

        /// <summary>
        /// Panning directions. Remove the appropriate direction to disable Eg. 'y' would only allow panning in the y direction.
        /// </summary>
        public string Mode { get; set; } = null!;

        public double? Sensitivity { get; set; }

        /// <summary>
        /// Format the min pan range depending on scale type.
        /// </summary>
        public Range RangeMin { get; set; } = null!;

        /// <summary>
        /// Format the max pan range depending on scale type.
        /// </summary>
        public Range RangeMax { get; set; } = null!;
    }
}
