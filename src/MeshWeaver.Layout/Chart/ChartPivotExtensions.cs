using System.Collections.Immutable;

namespace MeshWeaver.Layout.Chart;

/// <summary>
/// Extension methods for creating charts from grouped/pivoted data.
/// </summary>
public static class ChartPivotExtensions
{
    /// <summary>
    /// Creates a bar chart from grouped data by a single dimension.
    /// </summary>
    public static ChartControl ToBarChart<TSource, TKey>(
        this IEnumerable<TSource> source,
        Func<TSource, TKey> keySelector,
        Func<IEnumerable<TSource>, double> valueSelector,
        Func<TKey, string>? labelSelector = null,
        string? seriesLabel = null)
    {
        var groups = source.GroupBy(keySelector)
            .Select(g => new
            {
                Key = g.Key,
                Label = labelSelector != null ? labelSelector(g.Key) : g.Key?.ToString() ?? "",
                Value = valueSelector(g)
            })
            .OrderByDescending(x => x.Value)
            .ToArray();

        return Charts.Bar(
            groups.Select(x => x.Value),
            groups.Select(x => x.Label),
            seriesLabel
        );
    }

    /// <summary>
    /// Creates a bar chart from grouped data by a single dimension with custom ordering.
    /// </summary>
    public static ChartControl ToBarChart<TSource, TKey>(
        this IEnumerable<TSource> source,
        Func<TSource, TKey> keySelector,
        Func<IEnumerable<TSource>, double> valueSelector,
        bool orderByValueDescending,
        Func<TKey, string>? labelSelector = null,
        string? seriesLabel = null)
    {
        var groups = source.GroupBy(keySelector)
            .Select(g => new
            {
                Key = g.Key,
                Label = labelSelector != null ? labelSelector(g.Key) : g.Key?.ToString() ?? "",
                Value = valueSelector(g)
            });

        if (orderByValueDescending)
            groups = groups.OrderByDescending(x => x.Value);

        var groupsArray = groups.ToArray();

        return Charts.Bar(
            groupsArray.Select(x => x.Value),
            groupsArray.Select(x => x.Label),
            seriesLabel
        );
    }

    /// <summary>
    /// Creates a line chart from data grouped by two dimensions (rows and columns).
    /// </summary>
    public static ChartControl ToLineChart<TSource, TRowKey, TColKey>(
        this IEnumerable<TSource> source,
        Func<TSource, TRowKey> rowKeySelector,
        Func<TSource, TColKey> colKeySelector,
        Func<IEnumerable<TSource>, double> valueSelector,
        Func<TRowKey, string>? rowLabelSelector = null,
        Func<TColKey, string>? colLabelSelector = null)
    {
        // First get all unique column keys (will be X-axis labels)
        var columnKeys = source.Select(colKeySelector).Distinct().OrderBy(x => x).ToArray();
        var columnLabels = columnKeys.Select(k =>
            colLabelSelector != null ? colLabelSelector(k) : k?.ToString() ?? ""
        ).ToArray();

        // Then group by rows (will be different series)
        var rowGroups = source.GroupBy(rowKeySelector).ToArray();

        var chart = Charts.Create();

        foreach (var rowGroup in rowGroups)
        {
            var rowLabel = rowLabelSelector != null
                ? rowLabelSelector(rowGroup.Key)
                : rowGroup.Key?.ToString() ?? "";

            // For each column, get the aggregated value
            var values = columnKeys.Select(colKey =>
            {
                var cellData = rowGroup.Where(x => EqualityComparer<TColKey>.Default.Equals(colKeySelector(x), colKey));
                return cellData.Any() ? valueSelector(cellData) : 0.0;
            }).ToArray();

            chart = chart.WithSeries(new LineSeries(values, rowLabel));
        }

        return chart.WithLabels(columnLabels);
    }

    /// <summary>
    /// Creates a stacked bar chart from data grouped by two dimensions.
    /// </summary>
    public static ChartControl ToStackedBarChart<TSource, TRowKey, TColKey>(
        this IEnumerable<TSource> source,
        Func<TSource, TRowKey> rowKeySelector,
        Func<TSource, TColKey> colKeySelector,
        Func<IEnumerable<TSource>, double> valueSelector,
        Func<TRowKey, string>? rowLabelSelector = null,
        Func<TColKey, string>? colLabelSelector = null)
    {
        // Column keys become X-axis labels
        var columnKeys = source.Select(colKeySelector).Distinct().OrderBy(x => x).ToArray();
        var columnLabels = columnKeys.Select(k =>
            colLabelSelector != null ? colLabelSelector(k) : k?.ToString() ?? ""
        ).ToArray();

        // Row keys become different series (stacked)
        var rowGroups = source.GroupBy(rowKeySelector).ToArray();

        var chart = Charts.Create();

        foreach (var rowGroup in rowGroups)
        {
            var rowLabel = rowLabelSelector != null
                ? rowLabelSelector(rowGroup.Key)
                : rowGroup.Key?.ToString() ?? "";

            var values = columnKeys.Select(colKey =>
            {
                var cellData = rowGroup.Where(x => EqualityComparer<TColKey>.Default.Equals(colKeySelector(x), colKey));
                return cellData.Any() ? valueSelector(cellData) : 0.0;
            }).ToArray();

            chart = chart.WithSeries(new BarSeries(values, rowLabel));
        }

        return chart.WithLabels(columnLabels).Stacked();
    }

    /// <summary>
    /// Creates a pie chart from grouped data.
    /// </summary>
    public static ChartControl ToPieChart<TSource, TKey>(
        this IEnumerable<TSource> source,
        Func<TSource, TKey> keySelector,
        Func<IEnumerable<TSource>, double> valueSelector,
        Func<TKey, string>? labelSelector = null)
    {
        var groups = source.GroupBy(keySelector)
            .Select(g => new
            {
                Label = labelSelector != null ? labelSelector(g.Key) : g.Key?.ToString() ?? "",
                Value = valueSelector(g)
            })
            .OrderByDescending(x => x.Value)
            .ToArray();

        return Charts.Pie(
            groups.Select(x => x.Value),
            groups.Select(x => x.Label)
        );
    }

    /// <summary>
    /// Creates a doughnut chart from grouped data.
    /// </summary>
    public static ChartControl ToDoughnutChart<TSource, TKey>(
        this IEnumerable<TSource> source,
        Func<TSource, TKey> keySelector,
        Func<IEnumerable<TSource>, double> valueSelector,
        Func<TKey, string>? labelSelector = null)
    {
        var groups = source.GroupBy(keySelector)
            .Select(g => new
            {
                Label = labelSelector != null ? labelSelector(g.Key) : g.Key?.ToString() ?? "",
                Value = valueSelector(g)
            })
            .OrderByDescending(x => x.Value)
            .ToArray();

        return Charts.Doughnut(
            groups.Select(x => x.Value),
            groups.Select(x => x.Label)
        );
    }
}
