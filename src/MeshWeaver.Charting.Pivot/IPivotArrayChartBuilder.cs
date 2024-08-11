using MeshWeaver.Charting.Models.Options;

namespace MeshWeaver.Charting.Pivot;

public interface IPivotArrayChartBuilder : IPivotChartBuilder
{
    IPivotArrayChartBuilder WithSmoothedLines(params string[] linesToSmooth);
    IPivotArrayChartBuilder WithSmoothedLines(Dictionary<string, double> smoothDictionary);
    IPivotArrayChartBuilder WithFilledArea(params string[] rows);

}
