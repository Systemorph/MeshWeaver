using System.Text.Json.Serialization;
using MeshWeaver.Charting.Models.Options.Scales.Ticks;

namespace MeshWeaver.Charting.Models.Options.Scales
{    // https://www.chartjs.org/docs/3.7.1/axes/#common-options-to-all-axes
    [JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
    [JsonDerivedType(typeof(Scale), typeDiscriminator: "MeshWeaver.Charting.Models.Options.Scales.Scale")]
    [JsonDerivedType(typeof(Linear), typeDiscriminator: "MeshWeaver.Charting.Models.Options.Scales.Linear")]
    [JsonDerivedType(typeof(CartesianScale), typeDiscriminator: "MeshWeaver.Charting.Models.Options.Scales.CartesianScale")]
    [JsonDerivedType(typeof(CartesianLinearScale), typeDiscriminator: "MeshWeaver.Charting.Models.Options.Scales.CartesianLinearScale")]
    [JsonDerivedType(typeof(CartesianCategoryScale), typeDiscriminator: "MeshWeaver.Charting.Models.Options.Scales.CartesianCategoryScale")]
    [JsonDerivedType(typeof(TimeScale), typeDiscriminator: "MeshWeaver.Charting.Models.Options.Scales.TimeScale")]
    [JsonDerivedType(typeof(RadialScale), typeDiscriminator: "MeshWeaver.Charting.Models.Options.Scales.RadialScale")]
    public record Scale
    {
        /// <summary>
        /// Type of scale being employed. Custom scales can be created and registered with a string key. This allows changing the type of an axis for a chart.
        /// </summary>
        public string Type { get; init; } = null!;

        /// <summary>
        /// Align pixel values to device pixels.
        /// </summary>
        public bool? AlignToPixels { get; init; }

        /// <summary>
        /// Background color of the scale area.
        /// </summary>
        public ChartColor BackgroundColor { get; init; } = null!;

        /// <summary>
        /// Controls the axis global visibility (visible when true, hidden when false). When display: 'auto', the axis is visible only if at least one associated dataset is visible.
        /// </summary>
        public bool? Display { get; init; }

        /// <summary>
        /// Grid line configuration.
        /// </summary>
        public Grid Grid { get; init; } = null!;

        /// <summary>
        /// User defined minimum number for the scale, overrides minimum value from data.
        /// </summary>
        public double? Min { get; init; }

        /// <summary>
        /// User defined maximum number for the scale, overrides maximum value from data.
        /// </summary>
        public double? Max { get; init; }

        /// <summary>
        /// Reverse the scale.
        /// </summary>
        public bool? Reverse { get; init; }

        /// <summary>
        /// Should the data be stacked.
        /// </summary>
        public object Stacked { get; init; } = null!;

        /// <summary>
        /// Adjustment used when calculating the maximum data value.
        /// </summary>
        public int? SuggestedMax { get; init; }

        /// <summary>
        /// Adjustment used when calculating the minimum data value.
        /// </summary>
        public int? SuggestedMin { get; init; }

        /// <summary>
        /// Tick configuration.
        /// </summary>
        public Tick Ticks { get; init; } = null!;

        /// <summary>
        /// The weight used to sort the axis. Higher weights are further away from the chart area.
        /// </summary>
        public string Weight { get; init; } = null!;

        /// <summary>
        /// Callback called before the update process starts.
        /// </summary>
        public object BeforeUpdate { get; init; } = null!;

        /// <summary>
        /// Callback that runs before dimensions are set.
        /// </summary>
        public object BeforeSetDimensions { get; init; } = null!;

        /// <summary>
        /// Callback that runs after dimensions are set.
        /// </summary>
        public object AfterSetDimensions { get; init; } = null!;

        /// <summary>
        /// Callback that runs before data limits are determined.
        /// </summary>
        public object BeforeDataLimits { get; init; } = null!;

        /// <summary>
        /// Callback that runs after data limits are determined.
        /// </summary>
        public object AfterDataLimits { get; init; } = null!;

        /// <summary>
        /// Callback that runs before ticks are created.
        /// </summary>
        public object BeforeBuildTicks { get; init; } = null!;

        /// <summary>
        /// Callback that runs after ticks are created. Useful for filtering ticks.
        /// </summary>
        public object AfterBuildTicks { get; init; } = null!;

        /// <summary>
        /// Callback that runs before ticks are converted into strings.
        /// </summary>
        public object BeforeTickToLabelConversion { get; init; } = null!;

        /// <summary>
        /// Callback that runs after ticks are converted into strings.
        /// </summary>
        public object AfterTickToLabelConversion { get; init; } = null!;

        /// <summary>
        /// Callback that runs before tick rotation is determined.
        /// </summary>
        public object BeforeCalculateLabelRotation { get; init; } = null!;

        /// <summary>
        /// Callback that runs after tick rotation is determined.
        /// </summary>
        public object AfterCalculateLabelRotation { get; init; } = null!;

        /// <summary>
        /// Callback that runs before the scale fits to the canvas.
        /// </summary>
        public object BeforeFit { get; init; } = null!;

        /// <summary>
        /// Callback that runs after the scale fits to the canvas.
        /// </summary>
        public object AfterFit { get; init; } = null!;

        /// <summary>
        /// Callback that runs at the end of the update process.
        /// </summary>
        public object AfterUpdate { get; init; } = null!;
    }
}