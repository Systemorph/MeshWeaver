namespace MeshWeaver.Charting.Models;

public static class ChartExtensions
{
    public static Chart AsHorizontal(this Chart chart) => chart.WithOptions(options => options.WithIndexAxis("y"));

    public static bool IsHorizontal(this Chart chart) => chart.Options.IndexAxis == "y";
}
