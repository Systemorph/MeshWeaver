using MeshWeaver.Charting.Models;
using MeshWeaver.Charting.Models.Segmented;

namespace MeshWeaver.Charting.Builders.DataSetBuilders;

public abstract record SegmentDataSetBuilder<TDataSetBuilder, TDataSet> : ArrayDataSetBuilder<TDataSetBuilder, TDataSet>
    where TDataSetBuilder : SegmentDataSetBuilder<TDataSetBuilder, TDataSet>
    where TDataSet : SegmentDataSetBase, new()
{
    public TDataSetBuilder WithInnerHoleSizeInPercent(int percent)
        => (TDataSetBuilder)(this with { DataSet = DataSet with { Cutout = percent } });

    public TDataSetBuilder WithInnerHoleSizeInPercent(string percent)
        => (TDataSetBuilder)(this with { DataSet = DataSet with { Cutout = percent } });

    public TDataSetBuilder WithStartingAngle(int angle)
        => (TDataSetBuilder)(this with { DataSet = DataSet with { Rotation = angle } });

    public TDataSetBuilder WithTotalCircumference(int angle)
        => (TDataSetBuilder)(this with { DataSet = DataSet with { Circumference = angle } });
}