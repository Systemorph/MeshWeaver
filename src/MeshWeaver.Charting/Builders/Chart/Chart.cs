using System.Collections.Immutable;
using MeshWeaver.Charting.Enums;
using MeshWeaver.Charting.Helpers;
using MeshWeaver.Charting.Models;
using MeshWeaver.Charting.Models.Options;

namespace MeshWeaver.Charting.Builders.Chart;

// TODO V10: DataSets and ChartType are temporary ignored here to still try to match with old benchmarks (2024/08/21, Dmitry Kalabin)
public abstract record Chart<TChart, TDataSet> : Models.Chart
    where TChart : Chart<TChart, TDataSet>
    where TDataSet : DataSet, new()
{
    public Chart(IReadOnlyCollection<TDataSet> dataSets/*, ChartOptions Options*/, ChartType chartType) : base(chartType)
    {
        DataSets = dataSets.Cast<DataSet>().ToImmutableList();
    }

    protected TChart This => (TChart)this;

    public TChart WithDataSet<TDataSet2>(TDataSet2 dataSet) where TDataSet2 : DataSet
        => This with { DataSets = DataSets.Add(dataSet) };

    public virtual TChart WithLabels(params string[] labels) =>
        WithLabels(labels.AsReadOnly());

    public virtual TChart WithLabels(IReadOnlyCollection<string> labels) =>
        This with { Data = Data.WithLabels(labels), };

    public TChart WithLegend(Func<Legend, Legend> builder = null)
        => WithOptions(o => o.WithPlugins(p => p.WithLegend(builder)));

    public TChart WithTitle(string text, Func<Title, Title> titleModifier = null)
        => WithOptions(o => o.WithPlugins(p =>
                                          {
                                              p.Title = new Title { Text = text, Display = true, Font = ChartFonts.MainTitle, };
                                              if (titleModifier != null)
                                                  return p with { Title = titleModifier(p.Title), };
                                              return p;
                                          }));

    public TChart WithSubTitle(string text, Func<Title, Title> titleModifier = null)
        => WithOptions(o => o.WithPlugins(p =>
                                          {
                                              p.Subtitle = new Title { Text = text, Display = true, Font = ChartFonts.SubTitle, };
                                              if (titleModifier != null)
                                                  return p with { Subtitle = titleModifier(p.Subtitle), };
                                              return p;
                                          }));

    public TChart Stacked() => WithOptions(o => o.Stacked());

    public TChart WithColorPalette(string[] palette)
    {
        ChartHelpers.CheckPalette(palette);
        return WithOptions(o => o.WithPlugins(p => p with { ColorSchemes = new ColorSchemes { Scheme = palette, }, }));
    }

    public TChart WithColorPalette(Palettes palette)
        => WithOptions(o => o.WithPlugins(p => p with { ColorSchemes = new ColorSchemes { Scheme = palette, }, }));

    public TChart WithOptions(Func<ChartOptions, ChartOptions> func) => This with { Options = func(Options), };

    public TChart WithDataLabels(Func<DataLabels, DataLabels> func = null) =>
        WithOptions(o => o.WithPlugins(p => p.WithDataLabels(func)));

    public virtual Models.Chart ToChart()
    {
        var plugins = Options.Plugins;

        var options = Options;

        if (plugins?.Legend is null && DataSets.Count > 1 && DataSets.Any(item => item.HasLabel()))
        {
            options = Options.WithPlugins(p => p.WithLegend());
        }

        return this with
        {
            Options = options,
            Data = Data with { DataSets = DataSets, },
        };
    }
}
