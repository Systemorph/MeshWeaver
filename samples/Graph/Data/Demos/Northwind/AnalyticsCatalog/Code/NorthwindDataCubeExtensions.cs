// <meshweaver>
// Id: NorthwindDataCubeExtensions
// DisplayName: Data Cube Extensions
// </meshweaver>

using System.Reactive.Linq;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;

/// <summary>
/// Extension methods for accessing and filtering Northwind data cube data.
/// </summary>
public static class NorthwindDataCubeExtensions
{
    /// <summary>
    /// Gets the full data cube stream (all years).
    /// </summary>
    public static IObservable<IEnumerable<NorthwindDataCube>> GetNorthwindDataCubeData(
        this LayoutAreaHost area)
        => area.Workspace.GetStream<NorthwindDataCube>()!;

    /// <summary>
    /// Gets distinct years from the data cube as Select options.
    /// </summary>
    public static IObservable<Option[]> GetAvailableYears(this LayoutAreaHost host)
        => host.GetNorthwindDataCubeData()
            .Select(data => data.Select(x => x.OrderYear).Distinct()
                .OrderByDescending(y => y)
                .Select(y => new Option<int>(y, y.ToString()))
                .Prepend(new Option<int>(0, "All Years"))
                .Cast<Option>().ToArray())
            .DistinctUntilChanged(x => string.Join(',', x.Select(y => y.ToString())));

    /// <summary>
    /// Wraps a view with a year toolbar and provides filtered data for a single year.
    /// Year=0 defaults to the latest available year.
    /// </summary>
    public static UiControl WithYearToolbar(this LayoutAreaHost host,
        Func<int, IEnumerable<NorthwindDataCube>, UiControl> viewFactory)
    {
        host.SubscribeToDataStream(NorthwindYearToolbar.Years, host.GetAvailableYears());
        return host.Toolbar(new NorthwindYearToolbar(), (tb, area, _) =>
            area.GetNorthwindDataCubeData()
                .Select(data =>
                {
                    var year = tb.Year;
                    if (year == 0) year = data.Any() ? data.Max(x => x.OrderYear) : 0;
                    var filtered = data.Where(x => x.OrderYear == year);
                    return viewFactory(year, filtered);
                }));
    }

    /// <summary>
    /// Wraps a view with a year toolbar but passes all data (for comparison/YoY views).
    /// The year parameter indicates the "focus" year for labeling and comparison.
    /// </summary>
    public static UiControl WithYearComparisonToolbar(this LayoutAreaHost host,
        Func<int, IEnumerable<NorthwindDataCube>, UiControl> viewFactory)
    {
        host.SubscribeToDataStream(NorthwindYearToolbar.Years, host.GetAvailableYears());
        return host.Toolbar(new NorthwindYearToolbar(), (tb, area, _) =>
            area.GetNorthwindDataCubeData()
                .Select(data =>
                {
                    var year = tb.Year;
                    if (year == 0) year = data.Any() ? data.Max(x => x.OrderYear) : 0;
                    return viewFactory(year, data);
                }));
    }
}
