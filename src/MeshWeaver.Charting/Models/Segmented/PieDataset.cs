using System.Collections;
using MeshWeaver.Charting.Enums;

namespace MeshWeaver.Charting.Models.Segmented
{
    public record PieDataSet(IReadOnlyCollection<object> Data, string Label = null) : SegmentDataSetBase<PieDataSet>(Data, Label)
    {
        public PieDataSet(IEnumerable data) : this(data.Cast<object>().ToArray()) { }
        #region Styling
        // https://www.chartjs.org/docs/latest/charts/doughnut.html#styling
        /// <summary>
        /// Arc border join style..
        /// </summary>
        public IEnumerable<string> BorderJoinStyle { get; init; }

        /// <summary>
        /// Arc offset (in pixels).
        /// </summary>
        public int? Offset { get; init; }

        /// <summary>
        /// Fixed arc offset (in pixels). Similar to offset but applies to all arcs.
        /// </summary>
        public int? Spacing { get; init; }

        /// <summary>
        /// The relative thickness of the dataset. Providing a value for weight will cause the pie or doughnut dataset to be drawn with a thickness relative to the sum of all the dataset weight values.
        /// </summary>
        public int? Weight { get; init; }
        #endregion Styling

        #region BorderAlign
        // https://www.chartjs.org/docs/latest/charts/doughnut.html#border-alignment
        /// <summary>
        /// The following values are supported for borderAlign.
        /// 'center' (default)
        /// 'inner'
        /// When 'center' is set, the borders of arcs next to each other will overlap.When 'inner' is set, it is guaranteed that all borders will not overlap.
        /// </summary>
        public string BorderAlign { get; init; }
        #endregion BorderAlign

        #region BorderRadius
        // https://www.chartjs.org/docs/latest/charts/doughnut.html#border-radius
        /// <summary>
        /// If this value is a number, it is applied to all corners of the arc (outerStart, outerEnd, innerStart, innerRight). If this value is an object, the outerStart property defines the outer-start corner's border radius. Similarly, the outerEnd, innerStart, and innerEnd properties can also be specified.
        /// </summary>
        public object BorderRadius { get; init; }
        #endregion BorderRadius

        #region Interactions
        // https://www.chartjs.org/docs/latest/charts/doughnut.html#interactions
        /// <summary>
        /// Arc border join style when hovered.
        /// </summary>
        public string HoverBorderJoinStyle { get; init; }

        /// <summary>
        /// Arc border width when hovered (in pixels).
        /// </summary>
        public int? HoverBorderWidth { get; init; }

        /// <summary>
        /// Arc offset when hovered (in pixels).
        /// </summary>
        public int? HoverOffset { get; init; }
        #endregion Interactions

        public override ChartType? Type => ChartType.Pie;
        internal override bool HasLabel() => false;
    }
}
