using MeshWeaver.Pivot.Models;

namespace MeshWeaver.Pivot.Builder.Interfaces;

public interface IPivotBuilder
{
    IObservable<PivotModel> Execute();
}

public interface IPivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder>
    where TPivotBuilder : IPivotBuilderBase<
            T,
            TTransformed,
            TIntermediate,
            TAggregate,
            TPivotBuilder
        >
{
    public TPivotBuilder WithHierarchicalDimensionOptions(
        Func<IHierarchicalDimensionOptions, IHierarchicalDimensionOptions> optionsFunc
    );
    public TPivotBuilder Transpose<TValue>();
}
