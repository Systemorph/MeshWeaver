using MeshWeaver.Charting.Models;
using MeshWeaver.Charting.Models.Bar;
using MeshWeaver.Utils;

namespace MeshWeaver.Charting.Builders.DataSetBuilders;

public abstract record FloatingBarDataSetBuilderBase<TFloatingBarDataSetBuilder, TFloatingBarDataSet> : RangeDataSetBuilder<TFloatingBarDataSetBuilder, TFloatingBarDataSet>
    where TFloatingBarDataSetBuilder : FloatingBarDataSetBuilderBase<TFloatingBarDataSetBuilder, TFloatingBarDataSet>
    where TFloatingBarDataSet : BarDataSetBase, IDataSetWithStack, new()
{
    public TFloatingBarDataSetBuilder WithBarPercentage(double percentage)
        => (TFloatingBarDataSetBuilder)(this with { DataSet = DataSet with { BarPercentage = percentage } });
    
    public TFloatingBarDataSetBuilder WithBarThickness(object value)
        => (TFloatingBarDataSetBuilder)(this with { DataSet = DataSet with { BarThickness = value } });

    public TFloatingBarDataSetBuilder WithCategoryPercentage(double percentage)
        => (TFloatingBarDataSetBuilder)(this with { DataSet = DataSet with { CategoryPercentage = percentage } });

    public TFloatingBarDataSetBuilder WithXAxis(string xAxisId)
        => (TFloatingBarDataSetBuilder)(this with { DataSet = DataSet with { XAxisID = xAxisId } });

    public TFloatingBarDataSetBuilder WithYAxis(string yAxisId)
        => (TFloatingBarDataSetBuilder)(this with { DataSet = DataSet with { YAxisID = yAxisId } });

    public abstract TFloatingBarDataSetBuilder WithParsing();
}

public record FloatingBarDataSetBuilder : FloatingBarDataSetBuilderBase<FloatingBarDataSetBuilder, FloatingBarDataSet>
{
    public override FloatingBarDataSetBuilder WithParsing() 
        => WithParsing(new Parsing($"{nameof(WaterfallBar.Label).ToCamelCase()}", $"{nameof(WaterfallBar.Range).ToCamelCase()}"));
}

public record HorizontalFloatingBarDataSetBuilder : FloatingBarDataSetBuilderBase<HorizontalFloatingBarDataSetBuilder, HorizontalFloatingBarDataSet>
{
    public override HorizontalFloatingBarDataSetBuilder WithParsing()
        => WithParsing(new Parsing($"{nameof(WaterfallBar.Range).ToCamelCase()}", $"{nameof(WaterfallBar.Label).ToCamelCase()}"));
}