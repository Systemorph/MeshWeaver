using System.Collections.Immutable;
using MeshWeaver.Charting.Builders.DataSetBuilders;
using MeshWeaver.Charting.Builders.Options;
using MeshWeaver.Charting.Builders.OptionsBuilders;
using MeshWeaver.Charting.Enums;
using MeshWeaver.Charting.Helpers;
using MeshWeaver.Charting.Models;
using MeshWeaver.Charting.Models.Bar;
using MeshWeaver.Charting.Models.Options;
using MeshWeaver.Charting.Models.Options.Scales;
using MeshWeaver.Charting.Models.Options.Tooltips;
using MeshWeaver.Utils;

namespace MeshWeaver.Charting.Builders.Chart;

public static class WaterfallChartExtensions
{
    public static BarChart ToWaterfallChart(this BarChart chart, List<double> deltas,
        Func<FloatingBarDataSetBuilder, FloatingBarDataSetBuilder> barDataSetModifier = null,
        Func<WaterfallChartOptions, WaterfallChartOptions> options = null
    )
        => chart
            .ToWaterfallChart<FloatingBarDataSet, FloatingBarDataSetBuilder, WaterfallChartOptions>(deltas, barDataSetModifier, options)
            .WithOptions(o => o
                .Stacked("x")
                .HideAxis("y")
                .HideGrid("x")
            );

    public static BarChart ToHorizontalWaterfallChart(this BarChart chart, List<double> deltas,
        Func<HorizontalFloatingBarDataSetBuilder, HorizontalFloatingBarDataSetBuilder> barDataSetModifier = null,
        Func<HorizontalWaterfallChartOptions, HorizontalWaterfallChartOptions> options = null
    )
        => chart
            .ToWaterfallChart<HorizontalFloatingBarDataSet, HorizontalFloatingBarDataSetBuilder, HorizontalWaterfallChartOptions>(deltas, barDataSetModifier, options)
            .WithOptions(o => o
                .Stacked("y")
                //.HideAxis("x")
                .Grace<CartesianLinearScale>("x", "10%")
                // TODO V10: understand why the line below helps and find less random approach (2023/10/08, Ekaterina Mishina)
                .SuggestedMax("x", 10) // this helps in case of all negative values
                .ShortenAxisNumbers("x")
                .WithIndexAxis("y")
            );

    internal record WaterfallChartDataModel(List<double> deltas)
    {
        internal ImmutableList<(double[] range, string label, double? delta)> IncrementRanges { get; init; } = [];
        internal ImmutableList<(double[] range, string label, double? delta)> DecrementRanges { get; init; } = [];
        internal ImmutableList<(double[] range, string label, double? delta)> TotalRanges { get; init; } = [];
        internal ImmutableList<double?> FirstDottedValues { get; init; } = [];
        internal ImmutableList<double?> SecondDottedValues { get; init; } = [];
        internal ImmutableList<double?> ThirdDottedValues { get; init; } = [];
    }

    internal static WaterfallChartDataModel CalculateModel(this List<double> deltas, string[] labels, IReadOnlySet<int> totalIndexes)
    {
        var incrementRanges = new List<(double[] range, string label, double? delta)>(deltas.Count);
        var decrementRanges = new List<(double[] range, string label, double? delta)>(deltas.Count);
        var totalRanges = new List<(double[] range, string label, double? delta)>(deltas.Count);

        var firstDottedValues = new List<double?>(deltas.Count);
        var secondDottedValues = new List<double?>(deltas.Count);
        var thirdDottedValues = new List<double?>(deltas.Count);

        if (labels?.Length != deltas.Count)
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

    internal static ImmutableList<DataSet> BuildDataSets<TDataSet, TDataSetBuilder, TOptions>(
        this WaterfallChartDataModel dataModel,
        WaterfallStyling styling,
        Func<TDataSetBuilder, TDataSetBuilder> barDataSetModifier,
        TOptions options
    )
        where TDataSet : BarDataSetBase, IDataSetWithStack, new()
        where TDataSetBuilder : FloatingBarDataSetBuilderBase<TDataSetBuilder, TDataSet>, new()
        where TOptions : WaterfallChartOptions<TOptions, TDataSetBuilder>

    {
        var incrementRanges = dataModel.IncrementRanges.Select(d => new IncrementBar(d.range, d.label, d.delta, styling)).ToList();
        var decrementRanges = dataModel.DecrementRanges.Select(d => new DecrementBar(d.range, d.label, d.delta, styling)).ToList();
        var totalRanges = dataModel.TotalRanges.Select(d => new TotalBar(d.range, d.label, d.delta, styling)).ToList();

        var dataset1 = new TDataSetBuilder()
            .WithDataRange(incrementRanges, options.IncrementsLabel, dsb => (barDataSetModifier is null ? dsb : barDataSetModifier(dsb))
                                                                .WithParsing()
                                                                //.WithBarThickness("flex")
                                                                .WithBackgroundColor(ChartColor.FromHexString(styling.IncrementColor))
                                                                .WithHoverBackgroundColor(ChartColor.FromHexString(styling.IncrementColor)))
            .Build();
        var dataset2 = new TDataSetBuilder()
            .WithDataRange(decrementRanges, options.DecrementsLabel, dsb => (barDataSetModifier is null ? dsb : barDataSetModifier(dsb))
                                                                .WithParsing()
                                                                //.WithBarThickness("flex")
                                                                .WithBackgroundColor(ChartColor.FromHexString(styling.DecrementColor))
                                                                .WithHoverBackgroundColor(ChartColor.FromHexString(styling.DecrementColor)))
            .Build();
        var dataset3 = new TDataSetBuilder()
            .WithDataRange(totalRanges, options.TotalLabel, dsb => (barDataSetModifier is null ? dsb : barDataSetModifier(dsb))
                                                                .WithParsing()
                                                                //.WithBarThickness("flex")
                                                                .WithBackgroundColor(ChartColor.FromHexString(styling.TotalColor))
                                                                .WithHoverBackgroundColor(ChartColor.FromHexString(styling.TotalColor)))
            .Build();

        return [dataset1, dataset2, dataset3];
    }

    private static BarChart ToWaterfallChart<TDataSet, TDataSetBuilder, TOptions>(this BarChart chart, List<double> deltas,
        Func<TDataSetBuilder, TDataSetBuilder> barDataSetModifier = null,
        Func<TOptions, TOptions> optionsFunc = null
    )
        where TDataSet : BarDataSetBase, IDataSetWithStack, new()
        where TDataSetBuilder : FloatingBarDataSetBuilderBase<TDataSetBuilder, TDataSet>, new()
        where TOptions : WaterfallChartOptions<TOptions, TDataSetBuilder>, new()
    {
        optionsFunc ??= (o => o);
        var options = optionsFunc(new TOptions());

        var stylingBuilder = new WaterfallStylingBuilder();
        stylingBuilder = options.StylingOptions?.Invoke(stylingBuilder) ?? stylingBuilder;
        var styling = stylingBuilder.Build();

        var totalIndexes = options.TotalIndexes;
        if (options.HasLastAsTotal)
        {
            deltas = deltas.Append(deltas.Sum()).ToList();
            totalIndexes = totalIndexes.Add(deltas.Count - 1);
        }

        var tmp = chart;
        if (tmp.Data.Labels is null)
            tmp = tmp.WithLabels(Enumerable.Range(1, deltas.Count).Select(i => i.ToString()).ToArray());

        var labels = tmp.Data.Labels?.ToArray();

        var dataModel = deltas.CalculateModel(labels, totalIndexes);

        var datasets = dataModel.BuildDataSets<TDataSet, TDataSetBuilder, TOptions>(styling, barDataSetModifier, options);

        tmp = tmp with { DataSets = datasets, };

        if (options.IncludeConnectors)
        {
            LineDataSetBuilder Builder(LineDataSetBuilder b, IEnumerable<double?> data)
            {
                var builder = b.WithData(data).WithLabel(ChartConst.Hidden).WithDataLabels(new DataLabels { Display = false }).SetType(ChartType.Line);
                return options.ConnectorDataSetModifier != null
                           ? options.ConnectorDataSetModifier(builder)
                           : builder;
            }

            tmp = tmp
                  .WithDataSet(Builder(new(), dataModel.FirstDottedValues).Build())
                  .WithDataSet(Builder(new(), dataModel.SecondDottedValues).Build())
                  .WithDataSet(Builder(new(), dataModel.ThirdDottedValues).Build());
        }

        var palette = new[] { styling.IncrementColor, styling.DecrementColor, styling.TotalColor, styling.TotalColor, styling.TotalColor, styling.TotalColor };
        tmp = tmp.ApplyFinalStyling(palette);

        return tmp;
    }

    private static BarChart ApplyFinalStyling(this BarChart chart, string[] palette)
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
