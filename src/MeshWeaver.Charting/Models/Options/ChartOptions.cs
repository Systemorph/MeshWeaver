using MeshWeaver.Charting.Models.Options.Scales;

namespace MeshWeaver.Charting.Models.Options
{
    public record ChartOptions
    {
        #region Responsive Charts
        //https://www.chartjs.org/docs/3.5.1/configuration/responsive.html
        /// <summary>
        /// Resizes the chart canvas when its container does.
        /// </summary>
        public bool? Responsive { get; init; }

        /// <summary>
        /// Maintain the original canvas aspect ratio (width / height) when resizing.
        /// </summary>
        public bool? MaintainAspectRatio { get; init; }

        /// <summary>
        /// Canvas aspect ratio (i.e. width / height, a value of 1 representing a square canvas). Note that this option is ignored if the height is explicitly defined either as attribute or via the style.
        /// </summary>
        public double? AspectRatio { get; init; }

        /// <summary>
        /// Called when a resize occurs. Gets passed two arguments: the chart instance and the new size.
        /// </summary>
        public string OnResize { get; init; }

        /// <summary>
        /// Delay the resize update by give amount of milliseconds. This can ease the resize process by debouncing update of the elements.
        /// </summary>
        public int? ResizeDelay { get; init; }
        #endregion Responsive Charts

        #region Device Pixel Ratio
        // https://www.chartjs.org/docs/3.5.1/configuration/device-pixel-ratio.html
        /// <summary>
        /// Override the window's default devicePixelRatio.
        /// </summary>
        public int? DevicePixelRatio { get; init; }
        #endregion Device Pixel Ratio

        #region Locale
        // https://www.chartjs.org/docs/3.5.1/configuration/locale.html
        /// <summary>
        /// A string with a BCP 47 language tag, leveraging on INTL NumberFormat
        /// </summary>
        public string Locale { get; init; }
        #endregion Locale

        #region Interactions
        // https://www.chartjs.org/docs/3.5.1/configuration/interactions.html
        /// <summary>
        /// Configure which events trigger chart interactions
        /// </summary>
        public Interaction Interaction { get; init; }

        /// <summary>
        /// Events that the chart should listen to for tooltips and hovering.
        /// </summary>
        public IEnumerable<string> Events { get; init; }

        /// <summary>
        /// Called when any of the events fire over chartArea. Passed the event, an array of active elements (bars, points, etc), and the chart.
        /// </summary>
        public object OnHover { get; init; }

        /// <summary>
        /// Called if the event is of type 'mouseup' or 'click'. Called in the context of the chart and passed an array of active elements.
        /// </summary>
        public object OnClick { get; init; }
        #endregion Interactions

        #region Animations
        // https://www.chartjs.org/docs/3.5.1/configuration/animations.html
        public Animation.Animation Animation { get; init; }
        #endregion Animations

        #region Layout
        // https://www.chartjs.org/docs/3.5.1/configuration/layout.html
        public Layout.Layout Layout { get; init; }
        #endregion Layout

        public Dictionary<string, Scale> Scales { get; init; }

        public Plugins Plugins { get; init; } = new();

        /// <summary>
        /// The base axis of the dataset. 'x' for vertical bars and 'y' for horizontal bars.
        /// </summary>
        public string IndexAxis { get; init; }
    }
}
