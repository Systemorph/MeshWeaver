using OpenSmc.Charting.Builders.DataSetBuilders;
using OpenSmc.Charting.Builders.OptionsBuilders;
using OpenSmc.Charting.Enums;
using OpenSmc.Charting.Helpers;
using OpenSmc.Charting.Models;
using OpenSmc.Charting.Models.Options;
using OpenSmc.Charting.Models.Segmented;

namespace OpenSmc.Charting.Builders.ChartBuilders;

public abstract record ChartBuilderBase<TChartBuilder, TDataSet, TOptionsBuilder, TDataSetBuilder>
    where TChartBuilder : ChartBuilderBase<TChartBuilder, TDataSet, TOptionsBuilder, TDataSetBuilder>, new()
    where TDataSet : DataSet, new()
    where TOptionsBuilder : OptionsBuilderBase<TOptionsBuilder>
    where TDataSetBuilder : DataSetBuilderBase<TDataSetBuilder, TDataSet>, new()
{
    protected internal Chart ChartModel { get; init; }
    protected internal TOptionsBuilder OptionsBuilder { get; init; }
    protected internal List<DataSet> DataSets { get; init; }

    protected ChartBuilderBase(Chart chartModel, TOptionsBuilder builder)
    {
        ChartModel = chartModel;
        OptionsBuilder = builder;
        DataSets = new List<DataSet>();
    }

    public TChartBuilder WithDataSet(Func<TDataSetBuilder, TDataSetBuilder> func)
    {
        var builder = func(new TDataSetBuilder());
        DataSets.Add(builder.Build());
        return (TChartBuilder)this;
    }

    public TChartBuilder WithDataSet<TMixedDataSetBuilder, TMixedDataSet>(Func<TMixedDataSetBuilder, TMixedDataSetBuilder> func)
        where TMixedDataSetBuilder:DataSetBuilderBase<TMixedDataSetBuilder, TMixedDataSet>, new()
        where TMixedDataSet : DataSet
    {
        var dataSet = func(new TMixedDataSetBuilder())
                      .SetType(GetType(typeof(TMixedDataSet)))
                      .Build();
        DataSets.Add(dataSet);
        return (TChartBuilder)this;
    }

    private static ChartType GetType(Type type) =>
        type switch
        {
            not null when type == typeof(BarDataSet) => ChartType.Bar,
            not null when type == typeof(BubbleDataSet) => ChartType.Bubble,
            not null when type == typeof(RadarDataSet) => ChartType.Radar,
            not null when type == typeof(PolarDataSet) => ChartType.PolarArea,
            not null when type == typeof(PieDataSet) => ChartType.Pie,
            not null when type == typeof(LineDataSet) => ChartType.Line,
            not null when type == typeof(DoughnutDataSet) => ChartType.Doughnut,
            not null when type == typeof(HorizontalBarDataSet) => ChartType.HorizontalBar,
            not null when type == typeof(LineScatterDataSet) => ChartType.Scatter,
            _ => throw new ArgumentException(nameof(type))
        };

    public virtual TChartBuilder WithLabels(params string[] labels) =>
        WithLabels(labels.AsEnumerable());

    public virtual TChartBuilder WithLabels(IEnumerable<string> labels) =>
        (TChartBuilder)(this with { ChartModel = ChartModel with { Data = ChartModel.Data.WithLabels(labels) } });

    public TChartBuilder WithLegend(Func<Legend, Legend> legendModifier = null)
        => WithOptions(o => o.WithPlugins(p =>
                                          {
                                              p.Legend = new Legend { Display = true };
                                              if (legendModifier != null)
                                                  return p with { Legend = legendModifier(p.Legend) };
                                              return p;
                                          }));

    public TChartBuilder WithTitle(string text, Func<Title, Title> titleModifier = null)
        => WithOptions(o => o.WithPlugins(p =>
                                          {
                                              p.Title = new Title { Text = text, Display = true, Font = ChartFonts.MainTitle};
                                              if (titleModifier != null)
                                                  return p with { Title = titleModifier(p.Title) };
                                              return p;
                                          }));
    
    public TChartBuilder WithSubTitle(string text, Func<Title, Title> titleModifier = null)
        => WithOptions(o => o.WithPlugins(p =>
                                          {
                                              p.Subtitle = new Title { Text = text, Display = true, Font = ChartFonts.SubTitle};
                                              if (titleModifier != null)
                                                  return p with { Subtitle = titleModifier(p.Subtitle) };
                                              return p;
                                          }));

    public TChartBuilder Stacked() => WithOptions(o => o.Stacked());

    public TChartBuilder WithColorPalette(string[] palette)

    {
        ChartHelpers.CheckPalette(palette);
        return WithOptions(o => o.WithPlugins(p => p with { ColorSchemes = new ColorSchemes { Scheme = palette } }));
    }

    public TChartBuilder WithColorPalette(Palettes palette)
        => WithOptions(o => o.WithPlugins(p => p with { ColorSchemes = new ColorSchemes { Scheme = palette } }));

    public TChartBuilder WithOptions(Func<TOptionsBuilder, TOptionsBuilder> func) => (TChartBuilder)(this with { OptionsBuilder = func(OptionsBuilder) });

    public TChartBuilder WithDataLabels(Func<DataLabels, DataLabels> func) => WithOptions(o => o.WithPlugins(p => p with
    {
        DataLabels = func(p.DataLabels ?? new DataLabels())
    }));

    public virtual Chart ToChart()
    {
        var tmp = this with
                  {
                      ChartModel = ChartModel with
                                   {
                                       Options = OptionsBuilder.Build(),
                                       Data = ChartModel.Data with { DataSets = DataSets }
                                   }
                  };
        if (tmp.ChartModel.Options?.Plugins?.Legend is null && tmp.ChartModel.Data.DataSets.Any(item => item.HasLabel()))
            return tmp.WithLegend().ChartModel;

        return tmp.ChartModel;
    }
}
