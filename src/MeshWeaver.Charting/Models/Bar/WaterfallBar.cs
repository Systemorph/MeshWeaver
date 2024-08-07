namespace MeshWeaver.Charting.Models.Bar;

public abstract record WaterfallBar
{
    protected WaterfallBar()
    {
    }

    protected WaterfallBar(double[] range, string label, string dataLabel, string dataLabelColor, string dataLabelAlignment)
    {
        Range = range;
        Label = label;
        DataLabel = dataLabel;
        DataLabelColor = dataLabelColor;
        DataLabelAlignment = dataLabelAlignment;
    }

    public double[] Range { get; init; }
    public string Label { get; init; }
    public string DataLabel { get; init; }
    public string DataLabelColor { get; init; }
    public string DataLabelAlignment { get; init; }
}

public record IncrementBar : WaterfallBar
{
    public IncrementBar()
    {
    }

    public IncrementBar(double[] range, string label, double? delta, WaterfallStyling styling)
        : base(range, label,
               delta is null ? "" : styling.FormatFuncIncrement(delta.Value),
               styling.IncrementColor, //delta > styling.LabelsFontSize ? styling.LabelsFontColor : styling.IncrementColor,
               "end") //delta > styling.LabelsFontSize ? "center" : "end")
    {
    }
}

public record DecrementBar : WaterfallBar
{
    public DecrementBar()
    {
    }

    public DecrementBar(double[] range, string label, double? delta, WaterfallStyling styling)
        : base(range, label,
               delta is null ? "" : styling.FormatFuncDecrement(delta.Value),
               styling.DecrementColor, //delta != null && Math.Abs(delta.Value) > styling.LabelsFontSize ? styling.LabelsFontColor : styling.DecrementColor,
               "end") //delta != null && Math.Abs(delta.Value) > styling.LabelsFontSize ? "center" : "end")
    {
    }
}

public record TotalBar : WaterfallBar
{
    public TotalBar()
    {
    }

    public TotalBar(double[] range, string label, double? delta, WaterfallStyling styling)
        : base(range, label,
               delta is null ? "" : styling.FormatFuncTotal(delta.Value),
               styling.TotalColor, //delta != null && Math.Abs(delta.Value) > styling.LabelsFontSize ? styling.LabelsFontColor : styling.TotalColor,
               "end") //delta != null && Math.Abs(delta.Value) > styling.LabelsFontSize ? "center" : "end")
    {
        // TODO V10: for negative value handle differently (2023/08/30, Ekaterina Mishina)
    }
}

public record WaterfallStyling(string IncrementColor, string TotalColor, string DecrementColor, string LabelsFontColor, int LabelsFontSize, Func<double,string> FormatFuncIncrement, Func<double, string> FormatFuncDecrement, Func<double, string> FormatFuncTotal);
