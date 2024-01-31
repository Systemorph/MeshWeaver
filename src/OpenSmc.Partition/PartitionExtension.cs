using System.Linq.Expressions;
using System.Reflection;
using OpenSmc.Collections;

namespace OpenSmc.Partition;

public static class PartitionExtension
{
    private static readonly CreatableObjectStore<object, Type, Expression> PartitionExpressions = new(GetWhereExpression);
    private static readonly CreatableObjectStore<object, Type, Delegate> PartitionFunctions = new(GetWhereFunction);

    public static IQueryable<T> WithPartition<T, TPartition>(this IQueryable<T> queryable, TPartition partition)
    {
        // ReSharper disable once PossibleNullReferenceException
        var methodInfo = ((MethodInfo)MethodBase.GetCurrentMethod()).MakeGenericMethod(typeof(T), typeof(TPartition));
        var partitionExpression = Expression.Constant(partition, typeof(TPartition));

        var newExpression = Expression.Call(methodInfo, queryable.Expression, partitionExpression);

        return (IQueryable<T>)queryable.Provider.CreateQuery(newExpression);
    }

    public static IQueryable<T> WithPartitionKey<T>(this IQueryable<T> queryable, object partitionKey)
    {
        return PartitionExpressions.GetInstance(partitionKey, typeof(T)) is Expression<Func<T, bool>> expression
                   ? queryable.Where(expression)
                   : queryable;
    }

    public static IEnumerable<T> WithPartitionKey<T>(this IEnumerable<T> enumerable, object partitionKey)
    {
        return PartitionFunctions.GetInstance(partitionKey, typeof(T)) is Func<T, bool> func
                   ? enumerable.Where(func)
                   : enumerable;
    }
    public static IAsyncEnumerable<T> WithPartitionKey<T>(this IAsyncEnumerable<T> enumerable, object partitionKey)
    {
        return PartitionFunctions.GetInstance(partitionKey, typeof(T)) is Func<T, bool> func
                   ? enumerable.Where(func)
                   : enumerable;
    }

    private static Delegate GetWhereFunction(object partitionKey, Type instanceType)
    {
        return PartitionExpressions.GetInstance(partitionKey, instanceType) is LambdaExpression expr
                   ? expr.Compile() 
                   : null;
    }

    private static Expression GetWhereExpression(object partitionKey, Type instanceType)
    {
        var type = instanceType;
        var partitionKeyProperty = PartitionHelper.PartitionKeyProperties.GetInstance(type);
        if (partitionKeyProperty == null)
            return null;//unpartitioned

        if (partitionKey == null)
            throw new PartitionException(PartitionErrorMessages.PartitionMustBeSet);

        if (partitionKey.GetType() != partitionKeyProperty.PropertyType)
            throw new PartitionException(string.Format(PartitionErrorMessages.PartitionKeyMismatch, partitionKeyProperty.Name, partitionKeyProperty.PropertyType.Name, partitionKey.GetType().Name));

        var parameter = Expression.Parameter(instanceType);
        var left = Expression.Property(parameter, partitionKeyProperty);
        var right = Expression.Constant(partitionKey);
        var equal = Expression.Equal(left, right);
        var lambda = Expression.Lambda(equal, parameter);

        return lambda;
    }
}
