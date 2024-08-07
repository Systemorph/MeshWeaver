using MeshWeaver.Charting.Enums;
using MeshWeaver.Charting.Models;

namespace MeshWeaver.Charting.Builders.DataSetBuilders;

public interface IDataSetWithPointStyleAndOrderBuilder<out TDataSetBuilder, TDataSet>
    where TDataSetBuilder : DataSetBuilderBase<TDataSetBuilder, TDataSet>
    where TDataSet : DataSet, IDataSetWithPointStyle, IDataSetWithOrder
{
    public TDataSetBuilder WithPointStyle(Shapes shape);
    public TDataSetBuilder InOrder(int order);
}