using OpenSmc.Charting.Builders;
using OpenSmc.Charting.Enums;
using OpenSmc.Charting.Models;
using OpenSmc.Charting.Models.Options;

namespace OpenSmc.Charting.Pivot;

public interface IPivotChartBuilder
{
    IPivotChartBuilder WithLegend(Func<Legend, Legend> legendModifier);
    IPivotChartBuilder WithTitle(string title, Func<Title, Title> titleModifier = null);
    IPivotChartBuilder WithSubTitle(string title, Func<Title, Title> titleModifier = null);
    IPivotChartBuilder WithColorScheme(string[] scheme);
    IPivotChartBuilder WithColorScheme(Palettes scheme);
    IPivotChartBuilder WithOptions(Func<PivotChartModel, PivotChartModel> postProcessor);
    IPivotChartBuilder WithRows(params string[] lineRows);
    Chart Execute();
}
