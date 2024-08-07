namespace MeshWeaver.Charting.Models.Options.Tooltips
{
    public record Callback
    {
        /// <summary>
        /// Text to render before the title.
        /// </summary>
        public object BeforeTitle { get; init; }

        /// <summary>
        /// Text to render as the title.
        /// </summary>
        public object Title { get; init; }

        /// <summary>
        /// Text to render after the title.
        /// </summary>
        public object AfterTitle { get; init; }

        /// <summary>
        /// Text to render before the body section.
        /// </summary>
        public object BeforeBody { get; init; }

        /// <summary>
        /// Text to render before an individual label.
        /// </summary>
        public object BeforeLabel { get; init; }

        /// <summary>
        /// Text to render for an individual item in the tooltip.
        /// </summary>
        public object Label { get; init; }

        /// <summary>
        /// Returns the colors to render for the tooltip item. Return as an object containing two parameters: borderColor and backgroundColor.
        /// </summary>
        public object LabelColor { get; init; }

        /// <summary>
        /// Returns the colors for the text of the label for the tooltip item.
        /// </summary>
        public object LabelTextColor { get; init; }

        /// <summary>
        /// Returns the point style to use instead of color boxes if usePointStyle is true (object with values pointStyle and rotation). Default implementation uses the point style from the dataset points.
        /// </summary>
        public object LabelPointStyle { get; init; }

        /// <summary>
        /// Text to render after an individual label.
        /// </summary>
        public object AfterLabel { get; init; }

        /// <summary>
        /// Text to render after the body section.
        /// </summary>
        public object AfterBody { get; init; }

        /// <summary>
        /// Text to render before the footer section.
        /// </summary>
        public object BeforeFooter { get; init; }

        /// <summary>
        /// Text to render as the footer.
        /// </summary>
        public object Footer { get; init; }

        /// <summary>
        /// Text to render after the footer section.
        /// </summary>
        public object AfterFooter { get; init; }
    }
}
