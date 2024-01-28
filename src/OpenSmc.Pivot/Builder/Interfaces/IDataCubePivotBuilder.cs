using OpenSmc.DataCubes;
using OpenSmc.Pivot.Aggregations;

namespace OpenSmc.Pivot.Builder.Interfaces
{
    public interface IDataCubePivotBuilder<TCube, TElement, TIntermediate, TAggregate, TCubePivotBuilder> : IPivotBuilderBase<TCube, DataSlice<TElement>, TIntermediate, TAggregate, TCubePivotBuilder>
        where TCube : IDataCube<TElement>
        where TCubePivotBuilder : IDataCubePivotBuilder<TCube, TElement, TIntermediate, TAggregate, TCubePivotBuilder>
    {
        public TCubePivotBuilder SliceColumnsBy(params string[] dimensions);
        public TCubePivotBuilder SliceRowsBy(params string[] dimensions);
        public TCubePivotBuilder WithAggregation(Func<Aggregations<TElement, TIntermediate, TAggregate>,
                                                      Aggregations<TElement, TIntermediate, TAggregate>> aggregationsFunc);
    }
}
