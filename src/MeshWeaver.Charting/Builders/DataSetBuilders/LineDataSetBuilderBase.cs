using MeshWeaver.Charting.Models;
using MeshWeaver.Charting.Models.Line;

namespace MeshWeaver.Charting.Builders.DataSetBuilders;

public abstract record LineDataSetBuilderBase<TDataSetBuilder, TDataSet>
    : ArrayDataSetWithTensionFillPointRadiusAndRotation<TDataSetBuilder, TDataSet>
    where TDataSetBuilder : LineDataSetBuilderBase<TDataSetBuilder, TDataSet>
    where TDataSet : LineDataSetBase, new()
{
    public TDataSetBuilder WithLine(bool showLine = true)
        => (TDataSetBuilder)(this with { DataSet = DataSet with { ShowLine = showLine } });

    public TDataSetBuilder WithXAxis(string xAxisId)
        => (TDataSetBuilder)(this with { DataSet = DataSet with { XAxisID = xAxisId } });

    public TDataSetBuilder WithYAxis(string yAxisId)
        => (TDataSetBuilder)(this with { DataSet = DataSet with { YAxisID = yAxisId } });

    public override TDataSetBuilder WithArea() => (TDataSetBuilder)(this with { DataSet = DataSet with { Fill = "origin" } });

    public TDataSetBuilder Dashed()
        => (TDataSetBuilder)(this with { DataSet = DataSet with { BorderDash = new[] { 7, 3 } } });

    public TDataSetBuilder ThinLine()
        => (TDataSetBuilder)(this with { DataSet = DataSet with { BorderWidth = 1, PointRadius = 0 } });
}