namespace MeshWeaver.Charting.Models.Options
{
    // https://www.chartjs.org/docs/3.5.1/general/fonts.html
    public record Font
    {
        /// <summary>
        /// Default font family for all text, follows CSS font-family options.
        /// </summary>
        public string Family { get; init; }

        /// <summary>
        /// Default font size (in px) for text. Does not apply to radialLinear scale point labels.
        /// </summary>
        public int? Size { get; init; }

        /// <summary>
        /// Default font style. Does not apply to tooltip title or footer. Does not apply to chart title. Follows CSS font-style options (i.e. normal, italic, oblique, initial, inherit).
        /// </summary>
        public string Style { get; init; }

        /// <summary>
        /// Default font weight (boldness).
        /// </summary>
        public string Weight { get; init; }

        /// <summary>
        /// Height of an individual line of text.
        /// </summary>
        public object LineHeight { get; init; }

        public Font WithFamily(string family)
        {
            return this with {Family = family};
        }

        public Font WithSize(int? size)
        {
            return this with {Size = size};
        }

        public Font WithStyle(string style)
        {
            return this with {Style = style};
        }

        public Font WithWeight(string weight)
        {
            return this with {Weight = weight};
        }

        public Font WithLineHeight(object lineHeight)
        {
            return this with {LineHeight = lineHeight};
        }
    }
}
