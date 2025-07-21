#nullable enable
using System.Text.Json.Serialization;
using MeshWeaver.Charting.Enums;
using MeshWeaver.Charting.Models.Options.Scales;
using MeshWeaver.Charting.Models.Options.Scales.Ticks;

namespace MeshWeaver.Charting.Models.Options
{
    [JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
    [JsonDerivedType(typeof(ChartOptions), typeDiscriminator: "MeshWeaver.Charting.Models.Options.ChartOptions")]
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
        public string OnResize { get; init; } = null!;

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
        public string Locale { get; init; } = null!;
        #endregion Locale

        #region Interactions
        // https://www.chartjs.org/docs/3.5.1/configuration/interactions.html
        /// <summary>
        /// Configure which events trigger chart interactions
        /// </summary>
        public Interaction Interaction { get; init; } = null!;

        /// <summary>
        /// Events that the chart should listen to for tooltips and hovering.
        /// </summary>
        public IEnumerable<string> Events { get; init; } = null!;

        /// <summary>
        /// Called when any of the events fire over chartArea. Passed the event, an array of active elements (bars, points, etc), and the chart.
        /// </summary>
        public object OnHover { get; init; } = null!;

        /// <summary>
        /// Called if the event is of type 'mouseup' or 'click'. Called in the context of the chart and passed an array of active elements.
        /// </summary>
        public object OnClick { get; init; } = null!;
        #endregion Interactions

        #region Animations
        // https://www.chartjs.org/docs/3.5.1/configuration/animations.html
        public Animation.Animation Animation { get; init; } = null!;
        #endregion Animations

        #region Layout
        // https://www.chartjs.org/docs/3.5.1/configuration/layout.html
        public Layout.Layout Layout { get; init; } = null!;
        #endregion Layout

        public Dictionary<string, Scale> Scales { get; private set; } = null!;

        public Plugins Plugins { get; init; } = new();

        /// <summary>
        /// The base axis of the dataset. 'x' for vertical bars and 'y' for horizontal bars.
        /// </summary>
        public string IndexAxis { get; init; } = null!;


        public ChartOptions WithoutAnimation()
            => this with { Animation = (Animation ?? new()) with { Duration = 0, }, };

        public ChartOptions WithIndexAxis(string indexAxis)
            => this with { IndexAxis = indexAxis, };

        public ChartOptions WithScales(Dictionary<string, Scale> scales)
            => this with { Scales = scales, };

        public ChartOptions ShortenXAxisNumbers() => ShortenAxisNumbers("x");
        public ChartOptions ShortenYAxisNumbers() => ShortenAxisNumbers("y");
        public ChartOptions WithResponsive(bool responsive = true) => this with { Responsive = responsive, };

        public ChartOptions Stacked()
        {
            InitializeScale<Scale>("x");
            Scales["x"] = Scales["x"] with { Stacked = true, };

            InitializeScale<Scale>("y");
            Scales["y"] = Scales["y"] with { Stacked = true, };

            return this;
        }

        public ChartOptions Grace<TScale>(string axis, object grace)
        where TScale : CartesianLinearScale
        {
            InitializeScale<CartesianLinearScale>(axis);
            Scales[axis] = (Scales[axis] as CartesianLinearScale ?? new CartesianLinearScale()) with { Grace = grace };
            return this;
        }

        private void InitializeScale<TScale>(string key)
            where TScale : Scale, new()
        {
            // TODO V10: all of this is bad and to be done in a different way (2024/08/21, Dmitry Kalabin)
            if (Scales == default) Scales = new();
            if (!Scales.TryGetValue(key, out var scale) || scale is not TScale)
                scale = new TScale();

            Scales[key] = scale;
        }

        public ChartOptions Stacked(params string[] axes)
        {
            foreach (var axis in axes)
            {
                InitializeScale<Scale>(axis);
                Scales[axis] = Scales[axis] with { Stacked = true, };
            }

            return this;
        }

        public ChartOptions HideAxis(string axis)
        {
            InitializeScale<Scale>(axis);
            Scales[axis] = Scales[axis] with { Display = false };

            return this;
        }

        public ChartOptions HideGrid(string axis)
        {
            InitializeScale<Scale>(axis);
            Scales[axis] = Scales[axis] with { Grid = (Scales[axis].Grid ?? new Grid()) with { Display = false } };

            return this;
        }

        public ChartOptions SuggestedMax(string axis, int max)
        {
            InitializeScale<Scale>(axis);
            Scales[axis] = Scales[axis] with { SuggestedMax = max };

            return this;
        }

        public ChartOptions WithXAxisMin(int minimum) => WithAxisMin("x", minimum);
        public ChartOptions WithYAxisMin(int minimum) => WithAxisMin("y", minimum);
        public ChartOptions WithXAxisMin(double minimum) => WithAxisMin("x", minimum);
        public ChartOptions WithYAxisMin(double minimum) => WithAxisMin("y", minimum);

        public ChartOptions WithXAxisMax(int maximum) => WithAxisMax("x", maximum);
        public ChartOptions WithYAxisMax(int maximum) => WithAxisMax("y", maximum);
        public ChartOptions WithXAxisMax(double maximum) => WithAxisMax("x", maximum);
        public ChartOptions WithYAxisMax(double maximum) => WithAxisMax("y", maximum);


        public ChartOptions WithXAxisStep(int step) => WithAxisStep(step, "x");
        public ChartOptions WithYAxisStep(int step) => WithAxisStep(step, "y");
        public ChartOptions WithXAxisStep(double step) => WithAxisStep(step, "x");
        public ChartOptions WithYAxisStep(double step) => WithAxisStep(step, "y");

        public ChartOptions WithScales(params (string, Scale)[] scales)
            => this with { Scales = scales.ToDictionary(tuple => tuple.Item1, tuple => tuple.Item2), };

        public ChartOptions WithPlugins(Func<Plugins, Plugins> pluginsModifier)
            => this with { Plugins = pluginsModifier(Plugins), };

        public ChartOptions WithMaintainAspectRatio(bool maintainAspectRatio)
            => this with { MaintainAspectRatio = maintainAspectRatio, };

        public ChartOptions WithAspectRatio(double? maintainAspectRatio)
            => this with { AspectRatio = maintainAspectRatio, MaintainAspectRatio = true, };

        public ChartOptions WithLayout(Layout.Layout layout)
            => this with { Layout = layout, };

        public ChartOptions WithLayout(Func<Layout.Layout, Layout.Layout> layoutModifier)
            => this with { Layout = layoutModifier(Layout ?? new()), };

        private ChartOptions WithAxisMin(string axis, double minimum)
        {
            InitializeScale<Scale>(axis);
            Scales[axis] = Scales[axis] with { Min = minimum, };

            return this;
        }

        private ChartOptions WithAxisMax(string axis, double maximum)
        {
            InitializeScale<Scale>(axis);
            Scales[axis] = Scales[axis] with { Max = maximum, };

            return this;
        }

        private ChartOptions WithAxisStep(double step, string axis)
        {
            InitializeScale<Linear>(axis);
            Scales[axis] = Scales[axis] with { Ticks = (Scales[axis].Ticks as CartesianLinearTick ?? new CartesianLinearTick()) with { StepSize = step, } };

            return this;
        }

        public ChartOptions ShortenAxisNumbers(string axis)
        {
            InitializeScale<Scale>(axis);
            Scales[axis] = Scales[axis] with
            {
                Ticks = (Scales[axis].Ticks as CartesianLinearTick ?? new CartesianLinearTick()) with
                {
                    Format = new
                    {
                        notation = "compact",
                        minimumFractionDigits = 0,
                        maximumFractionDigits = 2,
                    }
                }
            };

            return this;
        }



        #region TimeOptions

        public ChartOptions SetTimeUnit(TimeIntervals unit)
        {
            InitializeScale<TimeScale>("x"); // TODO V10: this is also bad and to be optimized (2024/08/21, Dmitry Kalabin)
            if (!Scales.TryGetValue("x", out var rawScale) || rawScale is not TimeScale)
                rawScale = new TimeScale();

            var scale = (TimeScale)rawScale;
            scale = scale with { Time = (scale.Time ?? new Time()) with { Unit = unit } };
            Scales["x"] = scale;

            return this;
        }

        public ChartOptions SetTimeFormat(string format)
        {
            if (!Scales.TryGetValue("x", out var rawScale) || rawScale is not TimeScale)
                rawScale = new TimeScale();

            var scale = (TimeScale)rawScale;

            if (scale.Time == null)
                throw new ArgumentException("Please set the Time Unit before setting the Time Format.");

            var timeConfig = scale.Time;
            timeConfig = timeConfig with { DisplayFormats = new TimeDisplayFormat(timeConfig.Unit, format) };
            scale = scale with { Time = timeConfig };
            Scales["x"] = scale;

            return this;
        }

        #endregion TimeOptions
    }
}
