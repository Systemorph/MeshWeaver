using OpenSmc.Charting.Builders.ChartBuilders;
using OpenSmc.Charting.Builders.DataSetBuilders;
using OpenSmc.Charting.Builders.OptionsBuilders;
using OpenSmc.Charting.Enums;
using OpenSmc.Charting.Models;
using OpenSmc.Charting.Models.Options;
using OpenSmc.Pivot.Builder;
using OpenSmc.Pivot.Models;

namespace OpenSmc.Charting.Pivot;

public abstract record PivotChartBuilderBase<
    T,
    TTransformed,
    TIntermediate,
    TAggregate,
    TPivotBuilder,
    TChartBuilder,
    TDataSet,
    TOptionsBuilder,
    TDataSetBuilder
>(PivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder> pivotBuilder)
    : IPivotChartBuilder
    where TPivotBuilder : PivotBuilderBase<
            T,
            TTransformed,
            TIntermediate,
            TAggregate,
            TPivotBuilder
        >
    where TChartBuilder : ChartBuilderBase<
            TChartBuilder,
            TDataSet,
            TOptionsBuilder,
            TDataSetBuilder
        >,
        new()
    where TDataSet : DataSet, new()
    where TOptionsBuilder : OptionsBuilderBase<TOptionsBuilder>
    where TDataSetBuilder : DataSetBuilderBase<TDataSetBuilder, TDataSet>, new()
{
    private readonly PivotBuilderBase<
        T,
        TTransformed,
        TIntermediate,
        TAggregate,
        TPivotBuilder
    > pivotBuilder = pivotBuilder;
    protected TChartBuilder ChartBuilder;
    protected PivotChartModelBuilder PivotChartModelBuilder { get; init; } = new();

    public IPivotChartBuilder WithLegend(Func<Legend, Legend> legendModifier = null)
    {
        ChartBuilder = ChartBuilder.WithLegend(legendModifier);
        return this;
    }

    public IPivotChartBuilder WithTitle(string title, Func<Title, Title> titleModifier)
    {
        ChartBuilder = ChartBuilder.WithTitle(title, titleModifier);
        return this;
    }

    public IPivotChartBuilder WithSubTitle(string title, Func<Title, Title> titleModifier)
    {
        ChartBuilder = ChartBuilder.WithSubTitle(title, titleModifier);
        return this;
    }

    public IPivotChartBuilder WithColorScheme(string[] scheme)
    {
        ChartBuilder = ChartBuilder.WithColorPalette(scheme);
        return this;
    }

    public IPivotChartBuilder WithColorScheme(Palettes scheme)
    {
        ChartBuilder = ChartBuilder.WithColorPalette(scheme);
        return this;
    }

    public Chart Execute()
    {
        var pivotModel = pivotBuilder.Execute();
        var pivotChartModel = CreatePivotModel(pivotModel);
        AddDataSets(pivotChartModel);
        AddOptions(pivotChartModel);

        return ChartBuilder.ToChart();
    }

    public IPivotChartBuilder WithOptions(Func<PivotChartModel, PivotChartModel> postProcessor)
    {
        PivotChartModelBuilder.AddPostProcessor(postProcessor);
        return this;
    }

    public IPivotChartBuilder WithRows(params string[] lineRows)
    {
        PivotChartModelBuilder.AddPostProcessor(model =>
            model with
            {
                Rows = model
                    .Rows.Where(row =>
                        lineRows.Contains(row.Descriptor.Id)
                        || lineRows.Contains(row.Descriptor.DisplayName)
                    )
                    .ToList()
            }
        );
        return this;
    }

    protected abstract PivotChartModel CreatePivotModel(PivotModel pivotModel);

    protected abstract void AddDataSets(PivotChartModel pivotChartModel);

    protected abstract void AddOptions(PivotChartModel pivotChartModel);
}
