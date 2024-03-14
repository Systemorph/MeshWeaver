using OpenSmc.Charting.Builders.DataSetBuilders;
using OpenSmc.Charting.Builders.OptionsBuilders;
using OpenSmc.Charting.Enums;
using OpenSmc.Charting.Helpers;
using OpenSmc.Charting.Models;
using OpenSmc.Charting.Models.Bar;
using OpenSmc.Charting.Models.Options;
using OpenSmc.Charting.Models.Options.Scales;
using OpenSmc.Charting.Models.Options.Tooltips;
using OpenSmc.Utils;
using Systemorph.Charting.Models;

namespace OpenSmc.Charting.Builders.ChartBuilders;

public record FloatingBarChartBuilder(Chart ChartModel = null, RangeOptionsBuilder OptionsBuilder = null)
    : RangeChartBuilder<FloatingBarChartBuilder, FloatingBarDataSet, FloatingBarDataSetBuilder>(ChartModel ?? new Chart(ChartType.Bar), OptionsBuilder)
{
    public FloatingBarChartBuilder() : this(new Chart(ChartType.Bar)) { }
}

public record HorizontalFloatingBarChartBuilder(Chart ChartModel = null, RangeOptionsBuilder OptionsBuilder = null)
    : RangeChartBuilder<HorizontalFloatingBarChartBuilder, HorizontalFloatingBarDataSet, HorizontalFloatingBarDataSetBuilder>(ChartModel ?? new Chart(ChartType.Bar), OptionsBuilder)
{
    public HorizontalFloatingBarChartBuilder()
        : this(new Chart(ChartType.Bar))
    {
        OptionsBuilder = OptionsBuilder.WithIndexAxis("y");
    }
}

public abstract record WaterfallChartBuilderBase<TChartBuilder, TDataSet, TDataSetBuilder> : RangeChartBuilder<TChartBuilder, TDataSet, TDataSetBuilder>
    where TChartBuilder : WaterfallChartBuilderBase<TChartBuilder, TDataSet, TDataSetBuilder>, new()
    where TDataSet : BarDataSetBase, IDataSetWithStack, new()
    where TDataSetBuilder : FloatingBarDataSetBuilderBase<TDataSetBuilder, TDataSet>, new()
{
    protected WaterfallChartBuilderBase(Chart chartModel, RangeOptionsBuilder optionsBuilder)
        : base(chartModel, optionsBuilder)
    {
    }

    private List<double> deltas;
    private readonly HashSet<int> totalIndexes = new();
    private string incrementsLabel = ChartConst.Hidden;
    private string decrementsLabel = ChartConst.Hidden;
    private string totalLabel = ChartConst.Hidden;
    private bool datasetsReady;
    private bool includeConnectors;
    private Func<LineDataSetBuilder, LineDataSetBuilder> connectorDataSetModifier = d => d.ThinLine();
    private Func<TDataSetBuilder, TDataSetBuilder> barDataSetModifier;
    private WaterfallStylingBuilder StylingBuilder { get; set; } = new();

    public override Chart ToChart()
    {
        if (datasetsReady)
            return base.ToChart();
        var styling = StylingBuilder.Build();
        var incrementRanges = new List<IncrementBar>(deltas.Count);
        var decrementRanges = new List<DecrementBar>(deltas.Count);
        var totalRanges = new List<TotalBar>(deltas.Count);

        var firstDottedValues = new List<double?>(deltas.Count);
        var secondDottedValues = new List<double?>(deltas.Count);
        var thirdDottedValues = new List<double?>(deltas.Count);

        var tmp = this;
        if (ChartModel.Data.Labels is null)
            tmp = tmp.WithLabels(Enumerable.Range(1, deltas.Count).Select(i => i.ToString()));

        var labels = ChartModel.Data.Labels?.ToArray();
        if (labels?.Length != deltas.Count)
            throw new ArgumentException("Labels length does not match data");
        
        var total = 0.0;
        bool resetTotal = true;

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
                                    ? new TotalBar(new[] { 0, delta }, labels[index], delta, styling)
                                    : new TotalBar(new[] { delta, 0 }, labels[index], delta, styling));
                incrementRanges.Add(new IncrementBar(null, labels[index], null, styling));
                decrementRanges.Add(new DecrementBar(null, labels[index], null, styling));
                total = delta;
            }
            else
            {
                totalRanges.Add(new TotalBar(null, labels[index], null, styling));
                if (delta >= 0)
                {
                    incrementRanges.Add(new IncrementBar(new[] { total, total + delta }, labels[index], delta, styling));
                    decrementRanges.Add(new DecrementBar(null, labels[index], null, styling));
                }
                else
                {
                    decrementRanges.Add(new DecrementBar(new[] { total + delta, total }, labels[index], delta, styling));
                    incrementRanges.Add(new IncrementBar(null, labels[index], null, styling));
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

        datasetsReady = true;

        tmp = tmp.WithDataRange(incrementRanges, incrementsLabel, dsb => (barDataSetModifier is null ? dsb : barDataSetModifier(dsb))
                                                                         .WithParsing()
                                                                         //.WithBarThickness("flex")
                                                                         .WithBackgroundColor(ChartColor.FromHexString(styling.IncrementColor))
                                                                         .WithHoverBackgroundColor(ChartColor.FromHexString(styling.IncrementColor)))
                 .WithDataRange(decrementRanges, decrementsLabel, dsb => (barDataSetModifier is null ? dsb : barDataSetModifier(dsb))
                                                                         .WithParsing()
                                                                         //.WithBarThickness("flex")
                                                                         .WithBackgroundColor(ChartColor.FromHexString(styling.DecrementColor))
                                                                         .WithHoverBackgroundColor(ChartColor.FromHexString(styling.DecrementColor)))
                 .WithDataRange(totalRanges, totalLabel, dsb => (barDataSetModifier is null ? dsb : barDataSetModifier(dsb))
                                                                .WithParsing()
                                                                //.WithBarThickness("flex")
                                                                .WithBackgroundColor(ChartColor.FromHexString(styling.TotalColor))
                                                                .WithHoverBackgroundColor(ChartColor.FromHexString(styling.TotalColor)));
        
        if (includeConnectors)
        {
            LineDataSetBuilder Builder(LineDataSetBuilder b, IEnumerable<double?> data)
            {
                var builder = b.WithData(data).WithLabel(ChartConst.Hidden).WithDataLabels(new DataLabels { Display = false });
                return connectorDataSetModifier != null
                           ? connectorDataSetModifier(builder)
                           : builder;
            }

            tmp = tmp
                  .WithDataSet<LineDataSetBuilder, LineDataSet>(b => Builder(b, firstDottedValues))
                  .WithDataSet<LineDataSetBuilder, LineDataSet>(b => Builder(b, secondDottedValues))
                  .WithDataSet<LineDataSetBuilder, LineDataSet>(b => Builder(b, thirdDottedValues));
        }

        var palette = new[] { styling.IncrementColor, styling.DecrementColor, styling.TotalColor, styling.TotalColor, styling.TotalColor, styling.TotalColor };
        tmp = tmp.WithLegend(lm => lm with
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
        
        return tmp.ToChart();
    }

    public TChartBuilder WithLegendItems(string incrementsLabel = null, string decrementsLabel = null, string totalLabel = null)
    {
        this.incrementsLabel = incrementsLabel;
        this.decrementsLabel = decrementsLabel;
        this.totalLabel = totalLabel;
        return (TChartBuilder)this;
    }

    public TChartBuilder WithStylingOptions(Func<WaterfallStylingBuilder,WaterfallStylingBuilder> func)
    {
        StylingBuilder = func(StylingBuilder);
        return (TChartBuilder)this;
    }

    /// <summary>
    /// Show lines that connect bars.
    /// </summary>
    /// <param name="connectorLineModifier">Override default dataset modifier (by default it's b => b.Dashed()</param>
    /// <returns>The builder</returns>
    public TChartBuilder WithConnectors(Func<LineDataSetBuilder, LineDataSetBuilder> connectorLineModifier = null)
    {
        if (connectorLineModifier != null)
            connectorDataSetModifier = connectorLineModifier;
        includeConnectors = true;
        return (TChartBuilder)this;
    }

    public TChartBuilder WithTotalsAtPositions(HashSet<int> totalIndexes)
    {
        this.totalIndexes.UnionWith(totalIndexes);
        return (TChartBuilder)this;
    }

    public TChartBuilder WithTotalsAtPositions(IEnumerable<int> totalIndexes)
        => WithTotalsAtPositions(totalIndexes.ToHashSet());

    public TChartBuilder WithTotalsAtPositions(params int[] totalIndexes)
        => WithTotalsAtPositions(totalIndexes.ToHashSet());

    public TChartBuilder WithDeltas(List<double> deltas)
    {
        this.deltas = deltas;
        return (TChartBuilder)this;
    }
    public TChartBuilder WithBarDataSetOptions(Func<TDataSetBuilder, TDataSetBuilder> barDataSetModifier)
    {
        this.barDataSetModifier = barDataSetModifier;
        return (TChartBuilder)this;
    }

    public TChartBuilder WithDeltas(IEnumerable<double> deltas)
        => WithDeltas(deltas.ToList());

    public TChartBuilder WithDeltas(params double[] deltas)
        => WithDeltas(deltas.AsEnumerable());

    /// <summary>
    /// Add one more value that will be a sum and mark it as total
    /// </summary>
    /// <returns>The builder</returns>
    public TChartBuilder WithLastAsTotal()
    {
        deltas = deltas.Append(deltas.Sum()).ToList();
        totalIndexes.Add(deltas.Count - 1);
        return (TChartBuilder)this;
    }

    public TChartBuilder WithDataLabels(Func<DataLabels, DataLabels> func) => WithOptions(o => o.WithPlugins(p => p with
                                                                                                                  {
                                                                                                                      DataLabels = func(p.DataLabels ?? new DataLabels())
                                                                                                                  }));
}

public record WaterfallChartBuilder(Chart ChartModel = null, RangeOptionsBuilder OptionsBuilder = null)
    : WaterfallChartBuilderBase<WaterfallChartBuilder, FloatingBarDataSet, FloatingBarDataSetBuilder>(ChartModel, (OptionsBuilder ?? new RangeOptionsBuilder()).Stacked("x")
                                                                                                                                                               .HideAxis("y")
                                                                                                                                                               .HideGrid("x"))
{
    public WaterfallChartBuilder() : this(new Chart(ChartType.Bar)) { }
}

public record HorizontalWaterfallChartBuilder(Chart ChartModel = null, RangeOptionsBuilder OptionsBuilder = null)
    : WaterfallChartBuilderBase<HorizontalWaterfallChartBuilder, HorizontalFloatingBarDataSet, HorizontalFloatingBarDataSetBuilder>(ChartModel, (OptionsBuilder ?? new RangeOptionsBuilder())
                                                                                                                                                .Stacked("y")
                                                                                                                                                //.HideAxis("x")
                                                                                                                                                .Grace<CartesianLinearScale>("x","10%")
                                                                                                                                                // TODO V10: understand why the line below helps and find less random approach (2023/10/08, Ekaterina Mishina)
                                                                                                                                                .SuggestedMax("x", 10) // this helps in case of all negative values
                                                                                                                                                .ShortenAxisNumbers("x")
                                                                                                                                                )
{
    public HorizontalWaterfallChartBuilder()
        : this(new Chart(ChartType.Bar))
    {
        OptionsBuilder = OptionsBuilder.WithIndexAxis("y");
    }
}
