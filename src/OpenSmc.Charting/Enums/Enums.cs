using System.Runtime.Serialization;

namespace OpenSmc.Charting.Enums;

public enum TimeIntervals
{
    [EnumMember(Value = "millisecond")] Millisecond,
    [EnumMember(Value = "second")] Second,
    [EnumMember(Value = "minute")] Minute,
    [EnumMember(Value = "hour")] Hour,
    [EnumMember(Value = "day")] Day,
    [EnumMember(Value = "week")] Week,
    [EnumMember(Value = "month")] Month,
    [EnumMember(Value = "quarter")] Quarter,
    [EnumMember(Value = "year")] Year
}

public enum Easing
{
    [EnumMember(Value = "linear")] Linear,
    [EnumMember(Value = "easeInQuad")] EaseInQuad,
    [EnumMember(Value = "easeOutQuad")] EaseOutQuad,
    [EnumMember(Value = "easeInOutQuad")] EaseInOutQuad,
    [EnumMember(Value = "easeInCubic")] EaseInCubic,
    [EnumMember(Value = "easeOutCubic")] EaseOutCubic,
    [EnumMember(Value = "easeInOutCubic")] EaseInOutCubic,
    [EnumMember(Value = "easeInQuart")] EaseInQuart,
    [EnumMember(Value = "easeOutQuart")] EaseOutQuart,
    [EnumMember(Value = "easeInOutQuart")] EaseInOutQuart,
    [EnumMember(Value = "easeInQuint")] EaseInQuint,
    [EnumMember(Value = "easeOutQuint")] EaseOutQuint,
    [EnumMember(Value = "easeInOutQuint")] EaseInOutQuint,
    [EnumMember(Value = "easeInSine")] EaseInSine,
    [EnumMember(Value = "easeOutSine")] EaseOutSine,
    [EnumMember(Value = "easeInOutSine")] EaseInOutSine,
    [EnumMember(Value = "easeInExpo")] EaseInExpo,
    [EnumMember(Value = "easeOutExpo")] EaseOutExpo,
    [EnumMember(Value = "easeInOutExpo")] EaseInOutExpo,
    [EnumMember(Value = "easeInCirc")] EaseInCirc,
    [EnumMember(Value = "easeOutCirc")] EaseOutCirc,
    [EnumMember(Value = "easeInOutCirc")] EaseInOutCirc,
    [EnumMember(Value = "easeInElastic")] EaseInElastic,
    [EnumMember(Value = "easeOutElastic")] EaseOutElastic,
    [EnumMember(Value = "easeInOutElastic")] EaseInOutElastic,
    [EnumMember(Value = "easeInBack")] EaseInBack,
    [EnumMember(Value = "easeOutBack")] EaseOutBack,
    [EnumMember(Value = "easeInOutBack")] EaseInOutBack,
    [EnumMember(Value = "easeInBounce")] EaseInBounce,
    [EnumMember(Value = "easeOutBounce")] EaseOutBounce,
    [EnumMember(Value = "easeInOutBounce")] EaseInOutBounce
}

public enum Positions
{
    [EnumMember(Value = "top")] Top,
    [EnumMember(Value = "bottom")] Bottom,
    [EnumMember(Value = "left")] Left,
    [EnumMember(Value = "right")] Right
}

public enum ChartType
{
    [EnumMember(Value = "bar")] Bar,
    [EnumMember(Value = "bubble")] Bubble,
    [EnumMember(Value = "radar")] Radar,
    [EnumMember(Value = "polarArea")] PolarArea,
    [EnumMember(Value = "pie")] Pie,
    [EnumMember(Value = "line")] Line,
    [EnumMember(Value = "doughnut")] Doughnut,
    [EnumMember(Value = "horizontalBar")] HorizontalBar,
    [EnumMember(Value = "scatter")] Scatter
}


public enum Shapes
{
    [EnumMember(Value = "circle")] Circle,
    [EnumMember(Value = "cross")] Cross,
    [EnumMember(Value = "crossRot")] RotatedCross,
    [EnumMember(Value = "dash")] Dash,
    [EnumMember(Value = "line")] Line,
    [EnumMember(Value = "rect")] Rectangle,
    [EnumMember(Value = "rectRounded")] RectangleRounded,
    [EnumMember(Value = "rectRot")] RectangleRotated,
    [EnumMember(Value = "star")] Star,
    [EnumMember(Value = "triangle")] Triangle
}

internal enum DataTypes
{
    Array, Range, Point, PointValue, Time
}