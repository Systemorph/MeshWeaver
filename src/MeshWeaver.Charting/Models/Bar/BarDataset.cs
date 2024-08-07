using System.Diagnostics.CodeAnalysis;
using MeshWeaver.Charting.Enums;
using MeshWeaver.Charting.Models.Options.Scales;

// ReSharper disable once CheckNamespace
namespace MeshWeaver.Charting.Models
{
    /// <summary>
    /// https://www.chartjs.org/docs/latest/charts/bar.html
    /// </summary>
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    public abstract record BarDataSetBase : DataSet, IDataSetWithOrder, IDataSetWithPointStyle, IDataSetWithStack
    {
        #region General
        /// <summary>
        /// Base value for the bar in data units along the value axis. If not set, defaults to the value axis base value.
        /// </summary>
        //[JsonProperty("base")]
        public int? Base { get; init; }

        /// <summary>
        /// Should the bars be grouped on index axis. When true, all the datasets at same index value will be placed next to each other centering on that index value. When false, each bar is placed on its actual index-axis value.
        /// </summary>
        public bool? Grouped { get; init; }

        /// <summary>
        /// The base axis of the dataset. 'x' for vertical bars and 'y' for horizontal bars.
        /// </summary>
        public string IndexAxis { get; init; }

        /// <summary>
        /// The drawing order of dataset. Also affects order for stacking, tooltip and legend.
        /// </summary>
        public int? Order { get; init; }

        /// <summary>
        /// If true, null or undefined values will not be used for spacing calculations when determining bar size.
        /// </summary>
        public bool? SkipNull { get; init; }

        /// <summary>
        /// The ID of the group to which this dataset belongs to (when stacked, each group will be a separate stack). Defaults to dataset type.
        /// </summary>
        public string Stack { get; init; }

        /// <summary>
        /// The ID of the x axis to plot this dataset on.
        /// </summary>
        public string XAxisID { get; init; }

        /// <summary>
        /// The ID of the y axis to plot this dataset on.
        /// </summary>
        public string YAxisID { get; init; }
        #endregion

        #region Styling
        // https://www.chartjs.org/docs/latest/charts/bar.html#styling
        /// <summary>
        /// Which edge to skip drawing the border for. Options are 'bottom', 'left', 'top', and 'right'.
        /// </summary>
        public IEnumerable<string> BorderSkipped { get; init; }

        /// <summary>
        /// The bar border radius (in pixels).
        /// </summary>
        public object BorderRadius { get; init; }

        /// <summary>
        /// Set this to ensure that bars have a minimum length in pixels.
        /// </summary>
        public double? MinBarLength { get; init; }

        /// <summary>
        /// Style of the point for legend.
        /// </summary>
        public Shapes? PointStyle { get; init; }
        #endregion Styling

        #region Interactions
        // https://www.chartjs.org/docs/latest/charts/bar.html#interactions
        /// <summary>
        /// The bar border width when hovered (in pixels).
        /// </summary>
        public int? HoverBorderWidth { get; init; }

        /// <summary>
        /// The bar border radius when hovered (in pixels).
        /// </summary>
        public int? HoverBorderRadius { get; init; }
        #endregion Interactions

        #region BarPercentage
        // https://www.chartjs.org/docs/latest/charts/bar.html#barpercentage
        /// <summary>
        /// Percent (0-1) of the available width each bar should be within the category percentage. 1.0 will take the whole category width and put the bars right next to each other.
        /// </summary>
        public double? BarPercentage { get; init; }
        #endregion BarPercentage

        #region CategoryPercentage
        // https://www.chartjs.org/docs/latest/charts/bar.html#categorypercentage
        /// <summary>
        /// Percent (0-1) of the available width each category should be within the sample width.
        /// </summary>
        public double? CategoryPercentage { get; init; }
        #endregion CategoryPercentage

        #region BarThickness
        // https://www.chartjs.org/docs/latest/charts/bar.html#barthickness
        /// <summary>
        /// Manually set width of each bar in pixels. If set to 'flex', it computes "optimal" sample widths that globally arrange bars side by side. If not set (default), bars are equally sized based on the smallest interval.
        /// </summary>
        public object BarThickness { get; init; }
        #endregion BarThickness

        #region MaxBarThickness
        // https://www.chartjs.org/docs/latest/charts/bar.html#maxbarthickness
        /// <summary>
        /// Set this to ensure that bars are not sized thicker than this.
        /// </summary>
        public double? MaxBarThickness { get; init; }
        #endregion MaxBarThickness

        public Scale Scale { get; init; }
    }

    public record BarDataSet : BarDataSetBase;

    public record HorizontalBarDataSet : BarDataSet;

    public record HorizontalFloatingBarDataSet : HorizontalBarDataSet;

    public record FloatingBarDataSet : BarDataSetBase;
}