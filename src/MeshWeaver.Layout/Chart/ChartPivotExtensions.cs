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

            chart = chart.WithSeries(new ColumnSeries(values, rowLabel));
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

    /// <summary>
    /// Begins a fluent pivot chart builder by slicing data by the first dimension.
    /// </summary>
    public static ChartPivotSliceBuilder<TSource, TKey> SliceBy<TSource, TKey>(
        this IEnumerable<TSource> source,
        Func<TSource, TKey> keySelector,
        Func<TKey, string>? labelSelector = null)
    {
        return new ChartPivotSliceBuilder<TSource, TKey>(source, keySelector, labelSelector);
    }
}

/// <summary>
/// Fluent builder for creating charts from pivoted/sliced data.
/// </summary>
public class ChartPivotSliceBuilder<TSource, TKey1>
{
    private readonly IEnumerable<TSource> _source;
    private readonly Func<TSource, TKey1> _key1Selector;
    private readonly Func<TKey1, string>? _label1Selector;

    internal ChartPivotSliceBuilder(
        IEnumerable<TSource> source,
        Func<TSource, TKey1> key1Selector,
        Func<TKey1, string>? label1Selector)
    {
        _source = source;
        _key1Selector = key1Selector;
        _label1Selector = label1Selector;
    }

    /// <summary>
    /// Adds a second dimension slice to create a multi-dimensional pivot.
    /// </summary>
    public ChartPivotSliceBuilder<TSource, TKey1, TKey2> SliceBy<TKey2>(
        Func<TSource, TKey2> keySelector,
        Func<TKey2, string>? labelSelector = null)
    {
        return new ChartPivotSliceBuilder<TSource, TKey1, TKey2>(
            _source, _key1Selector, _label1Selector, keySelector, labelSelector);
    }

    /// <summary>
    /// Creates a column chart (vertical bars) from the single-dimension pivot.
    /// </summary>
    public ChartControl ToColumnChart(Func<IEnumerable<TSource>, double> valueSelector)
    {
        var groups = _source.GroupBy(_key1Selector)
            .Select(g => new
            {
                Label = _label1Selector != null ? _label1Selector(g.Key) : g.Key?.ToString() ?? "",
                Value = valueSelector(g)
            })
            .OrderByDescending(x => x.Value)
            .ToArray();

        return Charts.Column(
            groups.Select(x => x.Value),
            groups.Select(x => x.Label)
        );
    }

    /// <summary>
    /// Creates a bar chart (horizontal bars) from the single-dimension pivot.
    /// </summary>
    public ChartControl ToBarChart(Func<IEnumerable<TSource>, double> valueSelector)
    {
        var groups = _source.GroupBy(_key1Selector)
            .Select(g => new
            {
                Label = _label1Selector != null ? _label1Selector(g.Key) : g.Key?.ToString() ?? "",
                Value = valueSelector(g)
            })
            .OrderByDescending(x => x.Value)
            .ToArray();

        return Charts.Bar(
            groups.Select(x => x.Value),
            groups.Select(x => x.Label)
        );
    }

    /// <summary>
    /// Creates a pie chart from the single-dimension pivot.
    /// </summary>
    public ChartControl ToPieChart(Func<IEnumerable<TSource>, double> valueSelector)
    {
        var groups = _source.GroupBy(_key1Selector)
            .Select(g => new
            {
                Label = _label1Selector != null ? _label1Selector(g.Key) : g.Key?.ToString() ?? "",
                Value = valueSelector(g)
            })
            .OrderByDescending(x => x.Value)
            .ToArray();

        return Charts.Pie(
            groups.Select(x => x.Value),
            groups.Select(x => x.Label)
        );
    }
}

/// <summary>
/// Fluent builder for creating charts from two-dimensional pivoted data.
/// </summary>
public class ChartPivotSliceBuilder<TSource, TKey1, TKey2>
{
    private readonly IEnumerable<TSource> _source;
    private readonly Func<TSource, TKey1> _key1Selector;
    private readonly Func<TKey1, string>? _label1Selector;
    private readonly Func<TSource, TKey2> _key2Selector;
    private readonly Func<TKey2, string>? _label2Selector;

    internal ChartPivotSliceBuilder(
        IEnumerable<TSource> source,
        Func<TSource, TKey1> key1Selector,
        Func<TKey1, string>? label1Selector,
        Func<TSource, TKey2> key2Selector,
        Func<TKey2, string>? label2Selector)
    {
        _source = source;
        _key1Selector = key1Selector;
        _label1Selector = label1Selector;
        _key2Selector = key2Selector;
        _label2Selector = label2Selector;
    }

    /// <summary>
    /// Creates a grouped column chart (vertical bars) from the two-dimensional pivot.
    /// First dimension becomes x-axis, second dimension becomes series.
    /// </summary>
    public ChartControl ToColumnChart(Func<IEnumerable<TSource>, double> valueSelector)
    {
        // Key1 becomes X-axis labels
        var key1Values = _source.Select(_key1Selector).Distinct().OrderBy(x => x).ToArray();
        var key1Labels = key1Values.Select(k =>
            _label1Selector != null ? _label1Selector(k) : k?.ToString() ?? ""
        ).ToArray();

        // Key2 becomes different series
        var key2Groups = _source.GroupBy(_key2Selector).ToArray();

        var chart = Charts.Create();

        foreach (var key2Group in key2Groups)
        {
            var key2Label = _label2Selector != null
                ? _label2Selector(key2Group.Key)
                : key2Group.Key?.ToString() ?? "";

            var values = key1Values.Select(key1 =>
            {
                var cellData = key2Group.Where(x => EqualityComparer<TKey1>.Default.Equals(_key1Selector(x), key1));
                return cellData.Any() ? valueSelector(cellData) : 0.0;
            }).ToArray();

            chart = chart.WithSeries(new ColumnSeries(values, key2Label));
        }

        return chart.WithLabels(key1Labels).WithLegend(true);
    }

    /// <summary>
    /// Creates a stacked column chart (vertical bars) from the two-dimensional pivot.
    /// First dimension becomes x-axis, second dimension becomes stacked series.
    /// </summary>
    public ChartControl ToStackedColumnChart(Func<IEnumerable<TSource>, double> valueSelector)
    {
        return ToColumnChart(valueSelector).Stacked();
    }

    /// <summary>
    /// Creates a grouped bar chart (horizontal bars) from the two-dimensional pivot.
    /// First dimension becomes y-axis, second dimension becomes series.
    /// </summary>
    public ChartControl ToBarChart(Func<IEnumerable<TSource>, double> valueSelector)
    {
        // Key1 becomes Y-axis labels
        var key1Values = _source.Select(_key1Selector).Distinct().OrderBy(x => x).ToArray();
        var key1Labels = key1Values.Select(k =>
            _label1Selector != null ? _label1Selector(k) : k?.ToString() ?? ""
        ).ToArray();

        // Key2 becomes different series
        var key2Groups = _source.GroupBy(_key2Selector).ToArray();

        var chart = Charts.Create();

        foreach (var key2Group in key2Groups)
        {
            var key2Label = _label2Selector != null
                ? _label2Selector(key2Group.Key)
                : key2Group.Key?.ToString() ?? "";

            var values = key1Values.Select(key1 =>
            {
                var cellData = key2Group.Where(x => EqualityComparer<TKey1>.Default.Equals(_key1Selector(x), key1));
                return cellData.Any() ? valueSelector(cellData) : 0.0;
            }).ToArray();

            chart = chart.WithSeries(new BarSeries(values, key2Label));
        }

        return chart.WithLabels(key1Labels).WithLegend(true);
    }

    /// <summary>
    /// Creates a stacked bar chart (horizontal bars) from the two-dimensional pivot.
    /// First dimension becomes y-axis, second dimension becomes stacked series.
    /// </summary>
    public ChartControl ToStackedBarChart(Func<IEnumerable<TSource>, double> valueSelector)
    {
        return ToBarChart(valueSelector).Stacked();
    }

    /// <summary>
    /// Creates a line chart from the two-dimensional pivot.
    /// First dimension becomes x-axis, second dimension becomes series.
    /// </summary>
    public ChartControl ToLineChart(Func<IEnumerable<TSource>, double> valueSelector)
    {
        // Key1 becomes X-axis labels
        var key1Values = _source.Select(_key1Selector).Distinct().OrderBy(x => x).ToArray();
        var key1Labels = key1Values.Select(k =>
            _label1Selector != null ? _label1Selector(k) : k?.ToString() ?? ""
        ).ToArray();

        // Key2 becomes different series
        var key2Groups = _source.GroupBy(_key2Selector).ToArray();

        var chart = Charts.Create();

        foreach (var key2Group in key2Groups)
        {
            var key2Label = _label2Selector != null
                ? _label2Selector(key2Group.Key)
                : key2Group.Key?.ToString() ?? "";

            var values = key1Values.Select(key1 =>
            {
                var cellData = key2Group.Where(x => EqualityComparer<TKey1>.Default.Equals(_key1Selector(x), key1));
                return cellData.Any() ? valueSelector(cellData) : 0.0;
            }).ToArray();

            chart = chart.WithSeries(new LineSeries(values, key2Label));
        }

        return chart.WithLabels(key1Labels).WithLegend(true);
    }
}
