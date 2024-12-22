namespace MeshWeaver.Charting.Models;

public static class ChartExtensions
{
    public static ChartModel AsHorizontal(this ChartModel chart) => chart.WithOptions(options => options.WithIndexAxis("y"));

    public static bool IsHorizontal(this ChartModel chart) => chart.Options.IndexAxis == "y";
}
