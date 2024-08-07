using System.Linq.Expressions;

namespace MeshWeaver.Arithmetics.Aggregation.Implementation
{
    public interface IAggregationFunctionProvider
    {
        Delegate GetDelegate(Type type);
    }

    public class IsValueTypeAggregationFunctionProvider : IAggregationFunctionProvider
    {
        public Delegate GetDelegate(Type elementType)
        {
            var sumFunction = typeof(Enumerable).GetMethod(nameof(Enumerable.Sum),
                new[] { typeof(IEnumerable<>).MakeGenericType(elementType) });
            var prm = Expression.Parameter(typeof(IEnumerable<>).MakeGenericType(elementType));
            return Expression.Lambda(Expression.Call(sumFunction, prm), prm).Compile();
        }
    }

    public class IsClassAggregationFunctionProvider : IAggregationFunctionProvider
    {
        public Delegate GetDelegate(Type elementType) => AggregationFunction.FunctionAggregateClass(elementType);
    }
}