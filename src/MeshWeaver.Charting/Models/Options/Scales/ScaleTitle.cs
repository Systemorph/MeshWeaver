﻿namespace MeshWeaver.Charting.Models.Options.Scales
{
    // https://www.chartjs.org/docs/3.7.1/axes/labelling.html#scale-title-configuration
    public record ScaleTitle
    {
        /// <summary>
        /// If true, display the axis title.
        /// </summary>
        public bool? Display { get; init; }

        /// <summary>
        /// Alignment of the axis title. Possible options are 'start', 'center' and 'end'
        /// </summary>
        public string Align { get; init; } = null!;

        /// <summary>
        /// The text for the title. (i.e. "# of People" or "Response Choices").
        /// </summary>
        public string Text { get; init; } = null!;

        /// <summary>
        /// Color of label.
        /// </summary>
        public ChartColor Color { get; init; } = null!;

        public Font Font { get; init; } = null!;

        /// <summary>
        /// Padding to apply around scale labels. Only top, bottom and y are implemented.
        /// </summary>
        public int? Padding { get; init; }
    }
}
