namespace OpenSmc.Pivot.Builder.Interfaces
{
    public interface IPivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder>
        where TPivotBuilder : IPivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder>
    {
        public TPivotBuilder WithHierarchicalDimensionOptions(Func<IHierarchicalDimensionOptions, IHierarchicalDimensionOptions> optionsFunc);
        public TPivotBuilder Transpose<TValue>();
    }
}
