using MeshWeaver.Charting.Enums;
using MeshWeaver.Charting.Models.Options;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;

namespace MeshWeaver.Charting.Models;

public record ChartModel : IRenderableObject
{
    public ChartModel()
    {
        Options = GetAutoLegendOptions();
    }
    public ChartModel(params IEnumerable<DataSet> dataSets) : this()
    {
        Data = Data.WithDataSets(dataSets);
    }
    public ChartType Type => Data.DataSets.FirstOrDefault()?.Type ?? default;

    /// <summary>
    /// Chart data
    /// </summary>
    public ChartData Data { get; init; } = new();

    /// <summary>
    /// Chart options configuration
    /// </summary>
    public ChartOptions Options { get; init; } = new();


    public ChartModel WithDataSet<TDataSet2>(TDataSet2 dataSet) where TDataSet2 : DataSet
        => (this with { Data = Data.WithDataSets(dataSet), })
            .WithAutoLegend();

    public virtual ChartModel WithLabels(params IEnumerable<string> labels) =>
        (this with {Data = Data.WithLabels(labels)})
            .WithAutoLegend();





    private ChartOptions GetAutoLegendOptions()
    {
        if (Data.DataSets.Count > 1 && Data.DataSets.Any(item => item.HasLabel()))
            return Options.WithPlugins(p => p.WithLegend());
        return Options;
    }

    private ChartModel WithAutoLegend() => this with { Options = GetAutoLegendOptions(), };

    public ChartModel WithLegend(Func<Legend, Legend> builder = null)
        => WithOptions(o => o.WithPlugins(p => p.WithLegend(builder)));

    public ChartModel WithTitle(string text, Func<Title, Title> titleModifier = null)
        => WithOptions(o => o.WithPlugins(p =>
        {
            p.Title = new Title { Text = text, Display = true, Font = ChartFonts.MainTitle, };
            if (titleModifier != null)
                return p with { Title = titleModifier(p.Title), };
            return p;
        }));

    public ChartModel WithSubTitle(string text, Func<Title, Title> titleModifier = null)
        => WithOptions(o => o.WithPlugins(p =>
        {
            p.Subtitle = new Title { Text = text, Display = true, Font = ChartFonts.SubTitle, };
            if (titleModifier != null)
                return p with { Subtitle = titleModifier(p.Subtitle), };
            return p;
        }));

    public ChartModel Stacked() => WithOptions(o => o.Stacked());

    public ChartModel WithColorPalette(string[] palette)
    {
        ChartHelpers.CheckPalette(palette);
        return WithOptions(o => o.WithPlugins(p => p with { ColorSchemes = new ColorSchemes { Scheme = palette }, }));
    }

    public ChartModel WithColorPalette(Palettes palette)
        => WithOptions(o => o.WithPlugins(p => p with { ColorSchemes = new ColorSchemes { Scheme = palette, }, }));

    public ChartModel WithOptions(Func<ChartOptions, ChartOptions> func) => this with { Options = func(Options), };

    public ChartModel WithDataLabels(Func<DataLabels, DataLabels> func = null) =>
        WithOptions(o => o.WithPlugins(p => p.WithDataLabels(func)));

    public ChartModel WithDataSets(IEnumerable<DataSet> dataSets)
        => this with { Data = Data.WithDataSets(dataSets) };

    public UiControl ToControl()
        => new ChartControl(this);
}
