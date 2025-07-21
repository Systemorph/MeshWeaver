using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Linq.Expressions;
using MeshWeaver.Arithmetics.Aggregation.Implementation;

namespace MeshWeaver.Arithmetics.Aggregation;

/// <summary>
/// The <see cref="AggregationFunction"/> class provides the see Aggregate method, which can aggregate (sum up) an <see cref="IEnumerable{T}"/>
/// for a large variety of types T. For details on the used summation see <see cref="SumFunction"/> and also <conceptualLink target="3b557ccf-d392-496f-933b-08672b0e2d02#aggregate" />
/// </summary>
/// <conceptualLink target="3b557ccf-d392-496f-933b-08672b0e2d02#aggregate" />
public static class AggregationFunction
{
    static AggregationFunction()
    {
        RegisterAggregationProvider(new IsValueTypeAggregationFunctionProvider(), type => type.IsValueType);
        RegisterAggregationProvider(new IsClassAggregationFunctionProvider(), type => type.IsClass);
    }

    private static readonly ConcurrentDictionary<Type, Delegate> AggregationFunctions = new();

    private static readonly ConcurrentDictionary<Type, Type> AggregationTypes = new();

    private static ImmutableList<(Func<Type, bool> Filter, IAggregationFunctionProvider Provider)> AggregationFunctionProviders = ImmutableList<(Func<Type, bool> Filter, IAggregationFunctionProvider Provider)>.Empty;

    /// <summary>
    /// Aggregates (sum up) the elements of <paramref name="toAggregate"/> and returns a new instance with the result.
    /// </summary>
    /// <typeparam name="T">Type of the elements</typeparam>
    /// <param name="toAggregate">The elements to aggregate</param>
    /// <returns>New instance of <typeparamref name="T"/> with the aggregated result</returns>
    /// <conceptualLink target="3b557ccf-d392-496f-933b-08672b0e2d02#aggregate" />
    public static T Aggregate<T>(this IEnumerable<T> toAggregate) // TODO: Consider changing signature to IList, as not all enumerables are supported (2018.12.14, Klaus Seidl)
    {
        return GetAggregationFunction<T>()(toAggregate);

    }

    public static async Task<T> AggregateAsync<T>(IAsyncEnumerable<T> toAggregate)
    {
        void InputCheck(Type type)
        {
            if (type == typeof(string)) throw new ArgumentException();

            if (type.GetInterface(nameof(ICollection)) != null &&
                type.IsGenericType &&
                type.GetGenericTypeDefinition() != typeof(Dictionary<,>) &&
                type.GetGenericArguments().Length > 0 &&
                type.GetGenericArguments()[0].IsConstructedGenericType == false)
                throw new Exception();
        }

        InputCheck(typeof(T));

        var aggregated = await toAggregate.Where(x => x != null).AggregateAsync((result, item) => SumFunction.Sum(result, item));

        if (aggregated == null) return (T)Activator.CreateInstance(typeof(T))!;

        return aggregated;
    }

    /// <summary>
    /// Aggregates (sum up) the elements of <paramref name="toAggregate"/> and returns a new instance with the result.
    /// </summary>
    /// <typeparam name="T">Type of the elements</typeparam>
    /// <param name="toAggregate">The elements to aggregate</param>
    /// <returns>New instance of <typeparamref name="T"/> with the aggregated result</returns>
    /// <conceptualLink target="3b557ccf-d392-496f-933b-08672b0e2d02#aggregate" />
    public static T Aggregate<T>(params T[] toAggregate)
    {
        return GetAggregationFunction<T>()(toAggregate);
    }


    /// <summary>
    /// Gets the delegate for the <see>
    ///     <cref>Aggregate{T}</cref>
    /// </see>
    /// method which aggregates the elements of an enumerable
    /// </summary>
    /// <typeparam name="T">Type of the elements</typeparam>
    /// <returns>Delegate to the aggregate method</returns>
    /// <conceptualLink target="3b557ccf-d392-496f-933b-08672b0e2d02#aggregate" />
    public static Func<IEnumerable<T>, T> GetAggregationFunction<T>()
    {
        return (Func<IEnumerable<T>, T>)GetAggregationFunction(typeof(T));
    }

    /// <summary>
    /// Non-generic version of <see cref="GetAggregationFunction{T}"/>
    /// </summary>
    /// <param name="elementType">Type of the elements</param>
    /// <returns>Delegate to the aggregate method</returns>
    public static Delegate GetAggregationFunction(Type elementType)
    {
        return AggregationFunctions.GetOrAdd(elementType, _ => CreateAggregationFunction(elementType));
    }


    /// <summary>
    /// Aggregates (sums) all elements into the <paramref name="target"/>
    /// </summary>
    /// <typeparam name="T">Type of the class to aggregate</typeparam>
    /// <param name="toAggregate">Enumerable of all elements to aggregate into <paramref name="target"/></param>
    /// <param name="target">Reference to the target instance.</param>
    /// <conceptualLink target="3b557ccf-d392-496f-933b-08672b0e2d02#aggregate" />
    public static void AggregateInto<T>(IEnumerable<T> toAggregate, T target)
        where T : class
    {
        GetAggregationAction<T>()(toAggregate, target);
    }

    public static void RegisterAggregationProvider(IAggregationFunctionProvider provider, Func<Type, bool> filter)
    {
        AggregationFunctionProviders = AggregationFunctionProviders.Add((filter, provider));
    }

    public static void RegisterAggregationProviderAfter<T>(IAggregationFunctionProvider provider, Func<Type, bool> filter)
    {
        var insertPosition = AggregationFunctionProviders.FindIndex(x => x.Provider is T);
        AggregationFunctionProviders = AggregationFunctionProviders.Insert(insertPosition + 1, (filter, provider));
    }
    public static void RegisterAggregationProviderBefore<T>(IAggregationFunctionProvider provider, Func<Type, bool> filter)
    {
        var insertPosition = AggregationFunctionProviders.FindIndex(x => x.Provider is T);
        AggregationFunctionProviders = AggregationFunctionProviders.Insert(insertPosition, (filter, provider));
    }


    /// <summary>
    /// Gets the delegate for the <see cref="AggregateInto{T}"/> method which aggregates the enumerable into the second parameter instance
    /// </summary>
    /// <typeparam name="TElement"></typeparam>
    /// <returns>Delegate that aggregates the enumerable into the second parameter instance</returns>
    /// <conceptualLink target="3b557ccf-d392-496f-933b-08672b0e2d02#aggregate" />
    public static Action<IEnumerable<TElement>, TElement> GetAggregationAction<TElement>()
        where TElement : class
    {
        return (Action<IEnumerable<TElement>, TElement>)GetAggregationAction(typeof(TElement));
    }


    /// <summary>
    /// Non-generic version of <see cref="GetAggregationAction{TElement}"/>. 
    /// </summary>
    /// <param name="elementType"></param>
    /// <returns>Delegate that aggregates the enumerable into the second parameter instance</returns>
    /// <conceptualLink target="3b557ccf-d392-496f-933b-08672b0e2d02#aggregate" />
    public static Delegate GetAggregationAction(Type elementType)
    {
        return AggregateActionClassDelegateStore.GetOrAdd(elementType, CreateAggregateActionClass);
    }


    /// <summary>
    /// Unknown usage. Is this legacy Formula Framework? Strange signature.
    /// </summary>
    public static TResult Aggregate<TElement, TResult>(IEnumerable<TElement> toAggregate)
    {
        return GetAggregationFunction<TElement, TResult>()(toAggregate);
    }

    /// <summary>
    /// Unknown usage. Is this legacy Formula Framework? Strange signature.
    /// </summary>
    public static Func<IEnumerable<TElement>, TResult> GetAggregationFunction<TElement, TResult>()
    {
        return (Func<IEnumerable<TElement>, TResult>)AggregationFunctions.GetOrAdd(typeof(TElement), _ => CreateAggregationFunction(typeof(TElement)));
    }

    /// <summary>
    /// Unknown. Is this really the concern of the AggregationFunction class?
    /// </summary>
    public static void Register<TElement, TResult>(Func<IEnumerable<TElement>, TResult> func)
    {
        AggregationFunctions[typeof(TElement)] = func;
        if (typeof(TElement) != typeof(TResult))
            AggregationTypes[typeof(TElement)] = typeof(TResult);
    }

    private static Delegate CreateAggregationFunction(Type elementType)
    {
        var aggregationFunctionProvider = AggregationFunctionProviders.FirstOrDefault(x => x.Filter(elementType)).Provider;

        if (aggregationFunctionProvider != null)
            return aggregationFunctionProvider.GetDelegate(elementType);

        throw new NotSupportedException();
    }

    internal static Delegate FunctionAggregateClass(Type type)
    {
        var enumerableType = typeof(IEnumerable<>).MakeGenericType(type);

        var elementsParam = Expression.Parameter(enumerableType, "elements");
        var ret = Expression.Variable(type, "ret");
        var loopVar = Expression.Variable(type, "el");

        var addAction = SumFunction.GetSumFunctionWithResult(type);

        var enumeratorType = typeof(IEnumerator<>).MakeGenericType(type);

        var enumeratorVar = Expression.Variable(enumeratorType, "enumerator");
        var getEnumeratorCall = Expression.Call(elementsParam, enumerableType.GetMethod("GetEnumerator")!);

        // The MoveNext method's actually on IEnumerator, not IEnumerator<T>
        var moveNextCall = Expression.Call(enumeratorVar, typeof(IEnumerator).GetMethod("MoveNext")!);

        var breakLabel = Expression.Label("LoopBreak");

        var isFirst = Expression.Variable(typeof(bool));

        var currentProp = Expression.Property(enumeratorVar, "Current");

        var sumUp = Expression.IfThen(
            Expression.NotEqual(loopVar, Expression.Default(loopVar.Type)),
            Expression.Block(Expression.IfThen(
                    Expression.Equal(ret, Expression.Default(ret.Type)),
                    InstantiateResult(ret, loopVar)),
                Expression.Invoke(Expression.Constant(addAction), ret, loopVar)
            ));

        var loop = Expression.Block(new[] { enumeratorVar, ret, isFirst },
            Expression.Assign(enumeratorVar, getEnumeratorCall),
            Expression.Assign(ret, Expression.Default(ret.Type)),
            Expression.Assign(isFirst, Expression.Constant(true)),
            Expression.IfThen(moveNextCall,
                Expression.Loop(
                    Expression.Block(new[] { loopVar },
                        Expression.Assign(loopVar, currentProp),
                        Expression.IfThen(isFirst, Expression.IfThenElse(moveNextCall,
                            Expression.Block(sumUp, Expression.Assign(loopVar, currentProp), Expression.Assign(isFirst, Expression.Constant(false))),
                            Expression.Block(Expression.Assign(ret, loopVar), Expression.Break(breakLabel)))),
                        sumUp,
                        Expression.IfThen(Expression.Not(moveNextCall), Expression.Break(breakLabel))
                    ),
                    breakLabel
                )),

            ret
        );

        var lambda = Expression.Lambda(loop, elementsParam);

        return lambda.Compile();
    }

    private static Expression InstantiateResult(ParameterExpression ret, ParameterExpression currentElement)
    {
        if (currentElement.Type.IsArray)
            return Expression.Assign(ret, Expression.NewArrayBounds(currentElement.Type.GetElementType()!, Expression.Property(currentElement, nameof(Array.Length))));
        return GenericSumFunctionProvider.InstantiateNew(ret, currentElement);
    }

    private static readonly ConcurrentDictionary<Type, Delegate> AggregateActionClassDelegateStore = new ConcurrentDictionary<Type, Delegate>();



    private static Delegate CreateAggregateActionClass(Type type)
    {
        var enumerableType = typeof(IEnumerable<>).MakeGenericType(type);

        var elementsParam = Expression.Parameter(enumerableType, "elements");
        var instanceParam = Expression.Parameter(type, "instance");
        var loopVar = Expression.Variable(type, "el");

        var addAction = SumFunction.GetSumFunctionWithResult(type);

        var enumeratorType = typeof(IEnumerator<>).MakeGenericType(type);

        var enumeratorVar = Expression.Variable(enumeratorType, "enumerator");
        var getEnumeratorCall = Expression.Call(elementsParam, enumerableType.GetMethod("GetEnumerator")!);
        var enumeratorAssign = Expression.Assign(enumeratorVar, getEnumeratorCall);

        // The MoveNext method's actually on IEnumerator, not IEnumerator<T>
        var moveNextCall = Expression.Call(enumeratorVar, typeof(IEnumerator).GetMethod("MoveNext")!);

        var breakLabel = Expression.Label("LoopBreak");

        var loop = Expression.Block(new[] { enumeratorVar },
            enumeratorAssign,
            Expression.Loop(
                Expression.IfThenElse(
                    Expression.Equal(moveNextCall, Expression.Constant(true)),
                    Expression.Block(new[] { loopVar },
                        Expression.Assign(loopVar, Expression.Property(enumeratorVar, "Current")),
                        Expression.IfThen(Expression.NotEqual(loopVar, Expression.Constant(null, loopVar.Type)),
                            Expression.Invoke(Expression.Constant(addAction), instanceParam, loopVar))
                    ),
                    Expression.Break(breakLabel)
                ),
                breakLabel)
        );


        var lambda = Expression.Lambda(loop, elementsParam, instanceParam);

        return lambda.Compile();
    }

    public static Type GetAggregatedType(Type type)
    {
        return AggregationTypes.TryGetValue(type, out var ret) ? ret : type;
    }

}
