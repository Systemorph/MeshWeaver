using MeshWeaver.Data;
using MeshWeaver.Pivot.Aggregations;
using MeshWeaver.Pivot.Builder.Interfaces;
using MeshWeaver.Pivot.Models;
using MeshWeaver.Pivot.Processors;

namespace MeshWeaver.Pivot.Builder;

public abstract record PivotBuilderBase<
    T,
    TTransformed,
    TIntermediate,
    TAggregate,
    TPivotBuilder
> : IPivotBuilder, IPivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder>
    where TPivotBuilder : PivotBuilderBase<
        T,
        TTransformed,
        TIntermediate,
        TAggregate,
        TPivotBuilder
    >
{
    public IHierarchicalDimensionOptions HierarchicalDimensionOptions { get; private init; }
    public IList<T> Objects { get; }

    public Aggregations<TTransformed, TIntermediate, TAggregate> Aggregations;
    public Func<IEnumerable<T>, IEnumerable<TTransformed>> Transformation { get; init; } = (x => x.Cast<TTransformed>());
    protected Type TransposedValue { get; init; }
    public IWorkspace Workspace { get; init; }
    protected PivotBuilderBase(IWorkspace workspace, IEnumerable<T> objects)
    {
        Objects = objects as IList<T> ?? objects.ToArray();
        Aggregations = new Aggregations<TTransformed, TIntermediate, TAggregate>();
        HierarchicalDimensionOptions = new HierarchicalDimensionOptions();
        Workspace = workspace;
    }

    public TPivotBuilder WithHierarchicalDimensionOptions(
        Func<IHierarchicalDimensionOptions, IHierarchicalDimensionOptions> optionsFunc
    )
    {
        return (TPivotBuilder)this with
        {
            HierarchicalDimensionOptions = optionsFunc(HierarchicalDimensionOptions)
        };
    }

    public virtual TPivotBuilder Transpose<TValue>()
    {
        return (TPivotBuilder)this with { TransposedValue = typeof(TValue) };
    }

    public virtual IObservable<PivotModel> Execute()
    {
        var reportRenderer = GetReportProcessor();
        var ret = reportRenderer.Execute();
        return ret;
    }

    protected abstract PivotProcessorBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder>
        GetReportProcessor();

}
