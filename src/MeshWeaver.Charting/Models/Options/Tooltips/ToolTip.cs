namespace MeshWeaver.Charting.Models.Options.Tooltips
{
    /// <summary>
    /// Tooltip configuration.
    /// <see href="https://www.chartjs.org/docs/3.9.1/configuration/tooltip.html"/>
    /// </summary>
    public record ToolTip
    {
        /// <summary>
        /// Are on-canvas tooltips enabled?
        /// </summary>
        public bool? Enabled { get; init; }

        public object External { get; init; } = null!;

        /// <summary>
        /// Sets which elements appear in the tooltip.
        /// </summary>
        public string Mode { get; init; } = null!;

        /// <summary>
        /// If true, the tooltip mode applies only when the mouse position intersects with an element. If false, the mode will be applied at all times.
        /// </summary>
        public bool? Intersect { get; init; }

        /// <summary>
        /// The mode for positioning the tooltip.
        /// 'average' mode will place the tooltip at the average position of the items displayed in the tooltip.
        /// 'nearest' will place the tooltip at the position of the element closest to the event position.
        /// New modes can be defined by adding functions to the Chart.Tooltip.positioners map.
        /// </summary>
        public string Position { get; init; } = null!;

        /// <summary>
        /// Allows sorting of tooltip items. Must implement at minimum a function that can be passed to Array.prototype.sort. This function can also accept a third parameter that is the data object passed to the chart.
        /// </summary>
        public object ItemSort { get; init; } = null!;

        /// <summary>
        /// Allows filtering of tooltip items. Must implement at minimum a function that can be passed to Array.prototype.filter. This function can also accept a second parameter that is the data object passed to the chart.
        /// </summary>
        public object Filter { get; init; } = null!;

        /// <summary>
        /// Background color of the tooltip.
        /// </summary>
        public ChartColor BackgroundColor { get; init; } = null!;

        /// <summary>
        /// Color of title text.
        /// </summary>
        public ChartColor TitleColor { get; init; } = null!;

        /// <summary>
        /// Font of the tooltip.
        /// </summary>
        public Font TitleFont { get; init; } = null!;

        /// <summary>
        /// Horizontal alignment of the title text lines.
        /// </summary>
        public string TitleAlign { get; init; } = null!;

        /// <summary>
        /// Spacing to add to top and bottom of each title line.
        /// </summary>
        public int? TitleSpacing { get; init; }

        /// <summary>
        /// Margin to add on bottom of title section.
        /// </summary>
        public int? TitleMarginBottom { get; init; }

        /// <summary>
        /// Color of body text.
        /// </summary>
        public ChartColor BodyColor { get; init; } = null!;

        /// <summary>
        /// Font of the tooltip body.
        /// </summary>
        public Font BodyFont { get; init; } = null!;

        /// <summary>
        /// Horizontal alignment of the body text lines.
        /// </summary>
        public string BodyAlign { get; init; } = null!;

        /// <summary>
        /// Spacing to add to top and bottom of each tooltip item.
        /// </summary>
        public int? BodySpacing { get; init; }

        /// <summary>
        /// Color of footer text.
        /// </summary>
        public ChartColor FooterColor { get; init; } = null!;

        /// <summary>
        /// Horizontal alignment of the footer text lines.
        /// </summary>
        public string FooterAlign { get; init; } = null!;

        /// <summary>
        /// Spacing to add to top and bottom of each footer line.
        /// </summary>
        public int? FooterSpacing { get; init; }

        /// <summary>
        /// Margin to add before drawing the footer.
        /// </summary>
        public int? FooterMarginTop { get; init; }

        /// <summary>
        /// Padding inside the tooltip.
        /// </summary>
        public object Padding { get; init; } = null!;

        /// <summary>
        /// Extra distance to move the end of the tooltip arrow away from the tooltip point.
        /// </summary>
        public int? CaretPadding { get; init; }

        /// <summary>
        /// Size, in px, of the tooltip arrow.
        /// </summary>
        public int? CaretSize { get; init; }

        /// <summary>
        /// Radius of tooltip corner curves.
        /// </summary>
        public int? CornerRadius { get; init; }

        /// <summary>
        /// Color to draw behind the colored boxes when multiple items are in the tooltip.
        /// </summary>
        public ChartColor MultiKeyBackground { get; init; } = null!;

        /// <summary>
        /// if true, color boxes are shown in the tooltip.
        /// </summary>
        public bool? DisplayColors { get; init; }

        /// <summary>
        /// Width of the color box if displayColors is true.
        /// </summary>
        public int? BoxWidth { get; init; }

        /// <summary>
        /// Height of the color box if displayColors is true.
        /// </summary>
        public int? BoxHeight { get; init; }

        /// <summary>
        /// Use the corresponding point style (from dataset options) instead of color boxes, ex: star, triangle etc. (size is based on the minimum value between boxWidth and boxHeight).
        /// </summary>
        public bool? UsePointStyle { get; init; }

        /// <summary>
        /// Color of the border.
        /// </summary>
        public ChartColor BorderColor { get; init; } = null!;

        /// <summary>
        /// Size of the border.
        /// </summary>
        public int? BorderWidth { get; init; }

        /// <summary>
        /// true for rendering the tooltip from right to left.
        /// </summary>
        public bool? Rtl { get; init; }

        public string TextDirection { get; init; } = null!;

        /// <summary>
        /// Position of the tooltip caret in the X direction.
        /// </summary>
        public string XAlign { get; init; } = null!;

        /// <summary>
        /// Position of the tooltip caret in the Y direction.
        /// </summary>
        public string YAlign { get; init; } = null!;

        public Callback Callbacks { get; init; } = null!;
    }
}
