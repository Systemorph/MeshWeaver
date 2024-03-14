using OpenSmc.Charting.Models;

namespace OpenSmc.Charting.Builders.DataSetBuilders;

public abstract record ArrayDataSetWithTensionFillPointRadiusAndRotation<TDataSetBuilder, TDataSet> : ArrayDataSetWithStyleAndOrderBuilder<TDataSetBuilder, TDataSet>
    where TDataSetBuilder : ArrayDataSetWithTensionFillPointRadiusAndRotation<TDataSetBuilder, TDataSet>
    where TDataSet : DataSet, IDataSetWithPointStyle, IDataSetWithOrder, IDataSetWithFill, IDataSetWithTension, IDataSetWithPointRadiusAndRotation, new()
{
    public TDataSetBuilder Smoothed(double tension = 0.4)
        => (TDataSetBuilder)(this with { DataSet = DataSet with { Tension = tension } });

    public TDataSetBuilder WithoutPoint()
        => (TDataSetBuilder)(this with { DataSet = DataSet with { PointRadius = 0 } });

    public TDataSetBuilder WithPointRotation(int r)
        => (TDataSetBuilder)(this with { DataSet = DataSet with { PointRotation = r } });

    public TDataSetBuilder WithPointRadius(int r)
        => (TDataSetBuilder)(this with { DataSet = DataSet with { PointRadius = r } });

    public virtual TDataSetBuilder WithArea()
        => (TDataSetBuilder)(this with { DataSet = DataSet with { Fill = true } });

    public TDataSetBuilder WithoutFill()
        => (TDataSetBuilder)(this with { DataSet = DataSet with { Fill = false } });
}