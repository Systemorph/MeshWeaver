using OpenSmc.Charting.Models;
using Systemorph.Charting.Models;

namespace OpenSmc.Charting.Builders.DataSetBuilders;

public abstract record BarDataSetBuilderBase<TBuilder, TDataSet> : ArrayDataSetWithStyleAndOrderBuilder<TBuilder, TDataSet>
    where TBuilder : BarDataSetBuilderBase<TBuilder, TDataSet>
    where TDataSet : BarDataSet, IDataSetWithPointStyle, IDataSetWithOrder, new()
{
    public TBuilder WithBarPercentage(double percentage)
        => (TBuilder)(this with { DataSet = DataSet with { BarPercentage = percentage } });

    public TBuilder WithCategoryPercentage(double percentage)
        => (TBuilder)(this with { DataSet = DataSet with { CategoryPercentage = percentage } });

    public TBuilder WithXAxis(string xAxisId)
        => (TBuilder)(this with { DataSet = DataSet with { XAxisID = xAxisId } });

    public TBuilder WithYAxis(string yAxisId)
        => (TBuilder)(this with { DataSet =  DataSet with { YAxisID = yAxisId } });

    public TBuilder WithStack(string stack)
        => (TBuilder)(this with { DataSet = DataSet with { Stack = stack } });

    public TBuilder WithGrouped(bool grouped)
        => (TBuilder)(this with { DataSet = DataSet with { Grouped = grouped } });
}