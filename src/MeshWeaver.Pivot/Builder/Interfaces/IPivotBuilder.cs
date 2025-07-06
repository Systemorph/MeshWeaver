using System.Linq.Expressions;
using MeshWeaver.Pivot.Aggregations;

namespace MeshWeaver.Pivot.Builder.Interfaces
{
    public interface IPivotBuilder<T, TIntermediate, TAggregate, TPivotBuilder> : IPivotBuilderBase<T, T, TIntermediate, TAggregate, TPivotBuilder>
        where TPivotBuilder : IPivotBuilder<T, TIntermediate, TAggregate, TPivotBuilder>
    {
        public TPivotBuilder GroupRowsBy<TSelected>(Expression<Func<T?, TSelected?>> selector);
        public TPivotBuilder GroupColumnsBy<TSelected>(Expression<Func<T?, TSelected?>> selector);
        public TPivotBuilder WithAggregation(Func<Aggregations<T, TIntermediate, TAggregate>, Aggregations<T, TIntermediate, TAggregate>> aggregationsFunc);
    }
}
