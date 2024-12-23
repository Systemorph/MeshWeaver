using MeshWeaver.Charting.Enums;

namespace MeshWeaver.Charting.Models.Bubble
{
    /// <summary>
    /// Represents a dataset for a bubble chart.
    /// </summary>
    public record BubbleDataSet : DataSetBase<BubbleDataSet>, IDataSetWithOrder<BubbleDataSet>, IDataSetWithPointStyle<BubbleDataSet>
    {
        public BubbleDataSet(IReadOnlyCollection<BubbleData> Data, string Label = null) : base(Data, Label){}
        public BubbleDataSet(IEnumerable<(double x, double y, double radius)> values)
        : this(values.Select(e => new BubbleData(e.x, e.y, e.radius)).ToArray())
        {
        }


        public BubbleDataSet(IEnumerable<double> x, IEnumerable<double> y, IEnumerable<double> radius)
            : this(TransformBubbles(x.Select(a => (double)a).ToArray(), y.ToArray(), radius.ToArray())){}

        public BubbleDataSet(IReadOnlyCollection<double> x, IReadOnlyCollection<double> y, IReadOnlyCollection<double> radius)
        : this(TransformBubbles(x, y, radius))
        {
        }

        private static IReadOnlyCollection<BubbleData> TransformBubbles(IReadOnlyCollection<double> x, IReadOnlyCollection<double> y, IReadOnlyCollection<double> radius)
        {
            if (x.Count != y.Count || x.Count != radius.Count)
                throw new InvalidOperationException();

            return x.Zip(y, (a, b) => (a, b)).Zip(radius, (t, r) => new BubbleData(t.a, t.b, r )).ToArray();
        }


        #region General
        // https://www.chartjs.org/docs/latest/charts/bubble.html#general
        /// <summary>
        /// Draw the active points of a dataset over the other points of the dataset.
        /// </summary>
        public bool? DrawActiveElementsOnTop { get; init; }

        /// <summary>
        /// The drawing order of dataset. Also affects order for stacking, tooltip and legend.
        /// </summary>
        public int? Order { get; init; }
        #endregion General

        #region Styling
        // https://www.chartjs.org/docs/latest/charts/bubble.html#styling
        /// <summary>
        /// Bubble shape style.
        /// </summary>
        public Shapes? PointStyle { get; init; }

        /// <summary>
        /// Bubble rotation (in degrees).
        /// </summary>
        public int? Rotation { get; init; }

        /// <summary>
        /// Bubble radius (in pixels).
        /// </summary>
        public int? Radius { get; init; }
        #endregion Styling

        #region Interactions
        // https://www.chartjs.org/docs/latest/charts/bubble.html#interactions
        /// <summary>
        /// Bubble additional radius for hit detection (in pixels).
        /// </summary>
        public int? HitRadius { get; init; }

        /// <summary>
        /// Bubble border width when hovered (in pixels).
        /// </summary>
        public int? HoverBorderWidth { get; init; }

        /// <summary>
        /// Bubble additional radius when hovered (in pixels).
        /// </summary>
        public int? HoverRadius { get; init; }
        #endregion Interactions

        /// <summary>
        /// Sets the drawing order of the dataset.
        /// </summary>
        /// <param name="order">The drawing order of the dataset.</param>
        /// <returns>A new instance of <see cref="BubbleDataSet"/> with the specified order.</returns>
        public BubbleDataSet WithOrder(int? order) =>
            this with { Order = order };

        /// <summary>
        /// Sets the style of the point for the legend.
        /// </summary>
        /// <param name="pointStyle">The style of the point for the legend.</param>
        /// <returns>A new instance of <see cref="BubbleDataSet"/> with the specified point style.</returns>
        public BubbleDataSet WithPointStyle(Shapes? pointStyle) =>
            this with { PointStyle = pointStyle };

        internal override ChartType ChartType => ChartType.Bubble;
    }
}
