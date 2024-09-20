using MeshWeaver.Charting.Builders;
using MeshWeaver.Charting.Enums;
using MeshWeaver.Charting.Models;
using MeshWeaver.Charting.Models.Options;

namespace MeshWeaver.Charting.Pivot;

public interface IPivotChartBuilder
{
    IPivotChartBuilder WithLegend(Func<Legend, Legend> legendModifier = null);
    IPivotChartBuilder WithTitle(string title, Func<Title, Title> titleModifier = null);
    IPivotChartBuilder WithSubTitle(string title, Func<Title, Title> titleModifier = null);
    IPivotChartBuilder WithColorScheme(string[] scheme);
    IPivotChartBuilder WithColorScheme(Palettes scheme);
    IPivotChartBuilder WithOptions(Func<PivotChartModel, PivotChartModel> postProcessor);
    IPivotChartBuilder WithRows(params string[] lineRows);
    Chart Execute();
}
