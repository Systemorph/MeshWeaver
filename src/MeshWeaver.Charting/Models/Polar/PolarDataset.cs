namespace MeshWeaver.Charting.Models.Polar
{
    public record PolarDataSet : DataSet
    {
        #region Styling
        // https://www.chartjs.org/docs/latest/charts/polar.html#styling
        /// <summary>
        /// Arc border join style.
        /// </summary>
        public string BorderJoinStyle { get; init; }
        #endregion Styling

        #region BorderAlign
        // https://www.chartjs.org/docs/latest/charts/polar.html#border-alignment
        /// <summary>
        /// The following values are supported for borderAlign.
        /// 'center' (default)
        /// 'inner'
        /// When 'center' is set, the borders of arcs next to each other will overlap.When 'inner' is set, it is guaranteed that all the borders do not overlap.
        /// </summary>
        public string BorderAlign { get; init; }
        #endregion BorderAlign

        #region Interactions
        // https://www.chartjs.org/docs/latest/charts/polar.html#interactions
        /// <summary>
        /// Arc border join style when hovered.
        /// </summary>
        public string HoverBorderJoinStyle { get; init; }

        /// <summary>
        /// Arc border width when hovered (in pixels).
        /// </summary>
        public int? HoverBorderWidth { get; init; }
        #endregion Interactions

        internal override bool HasLabel() => false;
    }
}
