using System.Collections.Immutable;
using MeshWeaver.Charting.Models;
using MeshWeaver.Charting.Models.Bar;
using MeshWeaver.Charting.Models.Line;
using MeshWeaver.Charting.Models.Options;
using MeshWeaver.Charting.Models.Options.Tooltips;
using MeshWeaver.Charting.Waterfall;
using MeshWeaver.Utils;

namespace MeshWeaver.Charting;

internal static class WaterfallChartExtensions
{

    internal record WaterfallChartDataModel(List<double> deltas)
    {
        internal ImmutableList<(double[] range, string label, double? delta)> IncrementRanges { get; init; } = [];
        internal ImmutableList<(double[] range, string label, double? delta)> DecrementRanges { get; init; } = [];
        internal ImmutableList<(double[] range, string label, double? delta)> TotalRanges { get; init; } = [];
        internal ImmutableList<double?> FirstDottedValues { get; init; } = [];
        internal ImmutableList<double?> SecondDottedValues { get; init; } = [];
        internal ImmutableList<double?> ThirdDottedValues { get; init; } = [];
    }

    internal static WaterfallChartDataModel CalculateModel(this List<double> deltas, IReadOnlyList<string> labels, IReadOnlySet<int> totalIndexes)
    {
        var incrementRanges = new List<(double[] range, string label, double? delta)>(deltas.Count);
        var decrementRanges = new List<(double[] range, string label, double? delta)>(deltas.Count);
        var totalRanges = new List<(double[] range, string label, double? delta)>(deltas.Count);

        var firstDottedValues = new List<double?>(deltas.Count);
        var secondDottedValues = new List<double?>(deltas.Count);
        var thirdDottedValues = new List<double?>(deltas.Count);

        if (labels?.Count != deltas.Count)
            throw new ArgumentException("Labels length does not match data");

        var total = 0.0;
        var resetTotal = true;

        for (var index = 0; index < deltas.Count; index++)
        {
            var delta = deltas[index];
            var prevTotal = total;
            if (resetTotal)
                total = 0.0;

            var isTotal = totalIndexes != null && totalIndexes.Contains(index);

            if (isTotal)
            {
                totalRanges.Add(delta >= 0
                                    ? (new[] { 0, delta }, labels[index], delta)
                                    : (new[] { delta, 0 }, labels[index], delta));
                incrementRanges.Add((null, labels[index], null));
                decrementRanges.Add((null, labels[index], null));
                total = delta;
            }
            else
            {
                totalRanges.Add((null, labels[index], null));
                if (delta >= 0)
                {
                    incrementRanges.Add((new[] { total, total + delta }, labels[index], delta));
                    decrementRanges.Add((null, labels[index], null));
                }
                else
                {
                    decrementRanges.Add((new[] { total + delta, total }, labels[index], delta));
                    incrementRanges.Add((null, labels[index], null));
                }

                total += delta;
            }

            var beforeReset = index == deltas.Count - 1 || isTotal;

            if (index == 0)
            {
                firstDottedValues.Add(total);
                secondDottedValues.Add(null);
                thirdDottedValues.Add(null);
            }
            else
            {
                switch (index % 3)
                {
                    case 0:
                        if (!beforeReset)
                            firstDottedValues.Add(total);
                        else
                            firstDottedValues.Add(null);
                        secondDottedValues.Add(null);
                        thirdDottedValues.Add(prevTotal);
                        break;
                    case 1:
                        firstDottedValues.Add(prevTotal);
                        if (!beforeReset)
                            secondDottedValues.Add(total);
                        else
                            secondDottedValues.Add(null);
                        thirdDottedValues.Add(null);
                        break;
                    case 2:
                        firstDottedValues.Add(null);
                        secondDottedValues.Add(prevTotal);
                        if (!beforeReset)
                            thirdDottedValues.Add(total);
                        else
                            thirdDottedValues.Add(null);
                        break;
                }
            }

            resetTotal = beforeReset;
        }

        return new(deltas)
        {
            IncrementRanges = incrementRanges.ToImmutableList(),
            DecrementRanges = decrementRanges.ToImmutableList(),
            TotalRanges = totalRanges.ToImmutableList(),
            FirstDottedValues = firstDottedValues.ToImmutableList(),
            SecondDottedValues = secondDottedValues.ToImmutableList(),
            ThirdDottedValues = thirdDottedValues.ToImmutableList(),
        };
    }

    internal static IReadOnlyCollection<DataSet> BuildDataSets<TOptions>(
        this WaterfallChartDataModel dataModel,
        WaterfallStyling styling,
        TOptions options
    )
        where TOptions : WaterfallChartOptions<TOptions>

    {
        var incrementRanges = dataModel.IncrementRanges.Select(d => new IncrementBar(d.range, d.label, d.delta, styling)).ToList();
        var decrementRanges = dataModel.DecrementRanges.Select(d => new DecrementBar(d.range, d.label, d.delta, styling)).ToList();
        var totalRanges = dataModel.TotalRanges.Select(d => new TotalBar(d.range, d.label, d.delta, styling)).ToList();

        var barDataSetModifier = options.BarDataSetModifier;

        BarDataSet[] dataSets =
        [
            new FloatingBarDataSet(incrementRanges)
                .WithLabel(options.IncrementsLabel)
                .WithBackgroundColor(ChartColor.FromHexString(styling.IncrementColor))
                .WithHoverBackgroundColor(ChartColor.FromHexString(styling.IncrementColor)),
            new FloatingBarDataSet(decrementRanges)
                .WithLabel(options.DecrementsLabel)
                //.WithBarThickness("flex")
                .WithBackgroundColor(ChartColor.FromHexString(styling.DecrementColor))
                .WithHoverBackgroundColor(ChartColor.FromHexString(styling.DecrementColor)),
            new FloatingBarDataSet(totalRanges)
                .WithLabel(options.TotalLabel)
                //.WithBarThickness("flex")
                .WithBackgroundColor(ChartColor.FromHexString(styling.TotalColor))
                .WithHoverBackgroundColor(ChartColor.FromHexString(styling.TotalColor))
        ];

        if (barDataSetModifier is not null)
            dataSets = dataSets.Select(barDataSetModifier).ToArray();

        var parsing = options is HorizontalWaterfallChartOptions
            ? new Parsing($"{nameof(WaterfallBar.Range).ToCamelCase()}", $"{nameof(WaterfallBar.Label).ToCamelCase()}")
            : new Parsing($"{nameof(WaterfallBar.Label).ToCamelCase()}", $"{nameof(WaterfallBar.Range).ToCamelCase()}");

        dataSets = dataSets.Select(d => d.WithParsing(parsing)).ToArray();

        return dataSets;
    }

    internal static ChartModel ToWaterfallChart<TOptions>(this ChartModel chart, List<double> deltas,
        Func<TOptions, TOptions> optionsFunc = null
    )
        where TOptions : WaterfallChartOptions<TOptions>, new()
    {
        optionsFunc ??= (o => o);
        var options = optionsFunc(new TOptions());

        var styling = new WaterfallStyling();
        styling = options.StylingOptions?.Invoke(styling) ?? styling;

        var totalIndexes = options.TotalIndexes;
        if (options.HasLastAsTotal)
        {
            deltas = deltas.Append(deltas.Sum()).ToList();
            totalIndexes = totalIndexes.Add(deltas.Count - 1);
        }


        var labels = options.Labels ?? Enumerable.Range(1, deltas.Count).Select(i => i.ToString()).ToImmutableList();
        chart = chart.WithLabels(labels);

        var dataModel = deltas.CalculateModel(labels, totalIndexes);

        var datasets = dataModel.BuildDataSets(styling, options);

        chart = chart.WithDataSets(datasets);

        if (options.IncludeConnectors)
        {

            LineDataSet[] connectors =
            [
                new(dataModel.FirstDottedValues.Cast<object>()),
                new(dataModel.SecondDottedValues.Cast<object>()),
                new(dataModel.ThirdDottedValues.Cast<object>())
            ];
            connectors = connectors.Select(d => d.WithLabel(ChartConst.Hidden).WithDataLabels(new DataLabels { Display = false })).ToArray();
            if (options.ConnectorDataSetModifier is not null)
                connectors = connectors.Select(options.ConnectorDataSetModifier).ToArray();

            chart = chart.WithDataSets(connectors);
        }

        var palette = new[] { styling.IncrementColor, styling.DecrementColor, styling.TotalColor, styling.TotalColor, styling.TotalColor, styling.TotalColor };
        chart = chart.ApplyFinalStyling(palette);

        return chart;
    }

    private static ChartModel ApplyFinalStyling(this ChartModel chart, string[] palette)
    {
        var tmp = chart;
        tmp = tmp
            .WithLegend(lm => lm with
            {
                Labels = (lm.Labels ?? new LegendLabel()) with
                {
                    Filter = $"item => item.text !== '{ChartConst.Hidden}'"
                }
            })
            .WithDataLabels(o => o with
            {
                Color = $"context => context.dataset.data[context.dataIndex].{nameof(WaterfallBar.DataLabelColor).ToCamelCase()}",
                Align = $"context => context.dataset.data[context.dataIndex].{nameof(WaterfallBar.DataLabelAlignment).ToCamelCase()}",
                Anchor = $"context => context.dataset.data[context.dataIndex].{nameof(WaterfallBar.DataLabelAlignment).ToCamelCase()}",
                Display = true,//$"context => context.dataset.data[context.dataIndex].{nameof(WaterfallBar.Range).ToCamelCase()} != null",
                Formatter = $"(value, context) => context.dataset.data[context.dataIndex]?.{nameof(WaterfallBar.DataLabel).ToCamelCase()}"
            })
            .WithColorPalette(palette);

        tmp = tmp.WithOptions(o => o.WithPlugins(p => p with { Tooltip = (p.Tooltip ?? new ToolTip()) with { Enabled = false } }));
        return tmp;
    }
}
