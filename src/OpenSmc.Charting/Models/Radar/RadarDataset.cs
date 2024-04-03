using OpenSmc.Charting.Enums;
using OpenSmc.Charting.Helpers;
using OpenSmc.Charting.Models.Options.Scales;

namespace OpenSmc.Charting.Models
{
    public record RadarDataSet : DataSet, IDataSetWithOrder, IDataSetWithPointRadiusAndRotation, IDataSetWithTension, IDataSetWithFill, IDataSetWithPointStyle
    {
        #region General
        /// <summary>
        /// The drawing order of dataset. Also affects order for stacking, tooltip and legend.
        /// </summary>
        public int? Order { get; init; }
        #endregion General

        #region PointStyling
        // https://www.chartjs.org/docs/latest/charts/radar.html#point-styling
        /// <summary>
        /// The fill color for points.
        /// </summary>
        public IEnumerable<ChartColor> PointBackgroundColor { get; init; }

        /// <summary>
        /// The border color for points.
        /// </summary>
        public IEnumerable<ChartColor> PointBorderColor { get; init; }

        /// <summary>
        /// The width of the point border in pixels.
        /// </summary>
        public IEnumerable<int> PointBorderWidth { get; init; }

        /// <summary>
        /// The pixel size of the non-displayed point that reacts to mouse events.
        /// </summary>
        public IEnumerable<int> PointHitRadius { get; init; }

        /// <summary>
        /// The radius of the point shape. If set to 0, nothing is rendered.
        /// </summary>
        public int? PointRadius { get; init; }

        /// <summary>
        /// The rotation of the point in degrees.
        /// </summary>
        public int? PointRotation { get; init; }

        /// <summary>
        /// The style of point. Options are 'circle', 'triangle', 'rect', 'rectRot', 'cross', 'crossRot', 'star', 'line', and 'dash'. If the option is an image, that image is drawn on the canvas using drawImage.
        /// </summary>
        public Shapes? PointStyle { get; init; }
        #endregion PointStyling

        #region LineStyling
        // https://www.chartjs.org/docs/latest/charts/radar.html#line-styling
        /// <summary>
        /// Cap style of the line.
        /// </summary>
        public string BorderCapStyle { get; init; }

        /// <summary>
        /// Length and spacing of dashes.
        /// </summary>
        public IEnumerable<int> BorderDash { get; init; }

        /// <summary>
        /// Offset for line dashes.
        /// </summary>
        public double? BorderDashOffset { get; init; }

        /// <summary>
        /// Line joint style.
        /// </summary>
        public string BorderJoinStyle { get; init; }

        /// <summary>
        /// If true, fill the area under the line.
        /// </summary>
        public object Fill { get; init; }

        /// <summary>
        /// Bezier curve tension of the line. Set to 0 to draw straight lines. This option is ignored if monotone cubic interpolation is used. Note This was renamed from 'tension' but the old name still works.
        /// </summary>
        public double? Tension { get; init; }

        /// <summary>
        /// If true, lines will be drawn between points with no or null data. If false, points with null data will create a break in the line. Can also be a number specifying the maximum gap length to span. The unit of the value depends on the scale used.
        /// </summary>
        public bool? SpanGaps { get; init; }
        #endregion LineStyling

        #region Interactions
        // https://www.chartjs.org/docs/latest/charts/radar.html#interactions
        /// <summary>
        /// Point background color when hovered.
        /// </summary>
        public IEnumerable<ChartColor> PointHoverBackgroundColor { get; init; }

        /// <summary>
        /// Point border color when hovered.
        /// </summary>
        public IEnumerable<ChartColor> PointHoverBorderColor { get; init; }

        /// <summary>
        /// Border width of point when hovered.
        /// </summary>
        public IEnumerable<int> PointHoverBorderWidth { get; init; }

        /// <summary>
        /// The radius of the point when hovered.
        /// </summary>
        public IEnumerable<int> PointHoverRadius { get; init; }
        #endregion Interactions

        #region Scale
        // https://www.chartjs.org/docs/latest/charts/radar.html#scale-options
        /// <summary>
        /// The radar chart supports only a single scale. The options for this scale are defined in the scales.r property
        /// </summary>
        public Scale Scale { get; init; }
        #endregion Scale
    }
}
