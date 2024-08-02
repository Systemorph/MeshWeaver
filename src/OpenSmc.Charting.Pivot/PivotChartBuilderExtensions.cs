namespace OpenSmc.Charting.Pivot;

public static class PivotChartBuilderExtensions
{
    public static ChartControl ToChartControl(this IPivotChartBuilder builder)
    {
        return new (builder.Execute());
    }
}
