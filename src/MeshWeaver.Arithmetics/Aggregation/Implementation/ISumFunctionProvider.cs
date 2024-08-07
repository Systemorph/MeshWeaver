using System.Linq.Expressions;
using System.Reflection;
using MeshWeaver.Domain;
using MeshWeaver.Reflection;

namespace MeshWeaver.Arithmetics.Aggregation.Implementation;

public interface ISumFunctionProvider
{
    Delegate CreateSumFunctionWithResult(Type type);
    Delegate CreateSumDelegate(Type type);
}

public class GenericSumFunctionProvider : ISumFunctionProvider
{
    public Delegate CreateSumFunctionWithResult(Type type) => CreateSumFunctionWithResultImpl(type);

    public Delegate CreateSumDelegate(Type type) => CreateSumDelegateImpl(type);

    private static Delegate CreateSumDelegateImpl(Type tval)
    {
        //var retType = typeof(Func<,,>).MakeGenericType(tval, tval, tval);
        var x = Expression.Parameter(tval, "x");
        var y = Expression.Parameter(tval, "y");
        Expression method;
        if (tval.IsValueType)
        {
            if (tval == typeof(byte) || tval == typeof(sbyte))
                method = Expression.Convert(
                    Expression.Add(
                        Expression.Convert(x, typeof(int)),
                        Expression.Convert(y, typeof(int))
                    ),
                    tval
                );
            else
                method = Expression.Add(x, y);
        }
        else
        {
            var nullExp = Expression.Constant(null, tval);
            method = Expression.Condition(
                Expression.And(Expression.Equal(x, nullExp), Expression.Equal(y, nullExp)),
                nullExp,
                GetGenericSumMethod(tval, x, y)
            );
        }

        var lambda = Expression.Lambda(method, x, y);
        var func = lambda.Compile();
        return func;
    }

    private static Expression GetGenericSumMethod(
        Type tVal,
        ParameterExpression x,
        ParameterExpression y
    )
    {
        var enumType = GetEnumerableElementType(tVal);
        if (
            enumType != null
            && enumType.GetProperties().Any(p => p.HasAttribute<IdentityPropertyAttribute>())
        )
            return GetIdentityPropertySum(tVal, enumType, x, y);

        var ret = Expression.Variable(tVal);
        Expression newExpression;
        //            if (tVal.IsArray)
        newExpression = Expression.Constant(null, tVal);
        //            else
        //                newExpression = Expression.New(tVal);
        return Expression.Block(
            new[] { ret },
            Expression.Assign(ret, newExpression),
            GetSumActionExpression(tVal, ret, x, y),
            ret
        );
    }

    private static readonly MethodInfo ConcatMethod = typeof(Enumerable).GetMethod(
        nameof(Enumerable.Concat),
        BindingFlags.Public | BindingFlags.Static
    );
    private static readonly MethodInfo ToArrayMethod = typeof(Enumerable).GetMethod(
        nameof(Enumerable.ToArray),
        BindingFlags.Public | BindingFlags.Static
    );
    private static readonly MethodInfo ToListMethod = typeof(Enumerable).GetMethod(
        nameof(Enumerable.ToList),
        BindingFlags.Public | BindingFlags.Static
    );
    private static readonly MethodInfo GroupByIdentityPropertiesMethod =
        typeof(GrouperByIdentityProperties).GetMethod(
            nameof(GrouperByIdentityProperties.GroupByIdentityPropertiesAndExecuteLambda),
            BindingFlags.Public | BindingFlags.Static
        );
    private static readonly HashSet<Type> GenericArrayCastableTypes = new HashSet<Type>
    {
        typeof(IEnumerable<>),
        typeof(IList<>),
        typeof(ICollection<>)
    };

    private static Expression GetIdentityPropertySum(
        Type tVal,
        Type enumType,
        ParameterExpression x,
        ParameterExpression y
    )
    {
        var concat = Expression.Call(ConcatMethod.MakeGenericMethod(enumType), x, y);
        var aggFunction = AggregationFunction.GetAggregationFunction(enumType);
        var prm = Expression.Parameter(typeof(IEnumerable<>).MakeGenericType(enumType));
        var lambda = Expression.Lambda(
            Expression.Invoke(Expression.Constant(aggFunction), prm),
            prm
        );
        var identityProps = enumType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p =>
                (
                    p.HasAttribute<AggregateByAttribute>()
                    || p.HasAttribute<IdentityPropertyAttribute>()
                ) && !p.HasAttribute<AggregateOverAttribute>()
            )
            .ToArray();
        var key = identityProps.Select(p => p.Name).GetCacheKey();

        var copyer = identityProps.GetIdentityPropertiesCopier(enumType, key);
        var equalityComparer = typeof(AggregationFunctionIdentityEqualityComparer<>)
            .MakeGenericType(enumType)
            .GetMethod(
                nameof(AggregationFunctionIdentityEqualityComparer<object>.Instance),
                BindingFlags.NonPublic | BindingFlags.Static
            )
            .InvokeAsFunction(key, identityProps);
        var enumSum = Expression.Call(
            GroupByIdentityPropertiesMethod.MakeGenericMethod(enumType),
            concat,
            lambda,
            Expression.Constant(copyer),
            Expression.Constant(
                equalityComparer,
                typeof(IEqualityComparer<>).MakeGenericType(enumType)
            )
        );
        var wrapTrivialCases = Expression.Condition(
            Expression.Equal(x, Expression.Constant(null, x.Type)),
            Expression.Convert(y, enumSum.Type),
            Expression.Condition(
                Expression.Equal(y, Expression.Constant(null, y.Type)),
                Expression.Convert(x, enumSum.Type),
                enumSum
            )
        );
        if (
            tVal.IsArray
            || (
                tVal.IsGenericType
                && GenericArrayCastableTypes.Contains(tVal.GetGenericTypeDefinition())
            )
        )
            return Expression.Convert(
                Expression.Call(ToArrayMethod.MakeGenericMethod(enumType), wrapTrivialCases),
                tVal
            );
        if (tVal.IsGenericType && tVal.GetGenericTypeDefinition() == typeof(List<>))
            return Expression.Convert(
                Expression.Call(ToListMethod.MakeGenericMethod(enumType), wrapTrivialCases),
                tVal
            );
        throw new ArgumentException($"Cannot convert to output type {tVal.Name}");
    }

    private static Type GetEnumerableElementType(Type tVal)
    {
        if (tVal.IsGenericType && typeof(IEnumerable<>) == tVal.GetGenericTypeDefinition())
            return tVal.GetGenericArguments().First();
        var enumInterface = tVal.GetInterfaces()
            .FirstOrDefault(x =>
                x.IsGenericType && x.GetGenericTypeDefinition() == typeof(IEnumerable<>)
            );
        if (enumInterface == null)
            return null;
        return enumInterface.GetGenericArguments().First();
    }

    private static Delegate CreateSumFunctionWithResultImpl(Type type)
    {
        if (typeof(string) == type)
            throw new ArgumentException("Cannot add strings");

        if (FormulaFrameworkTypeExtensions.IsDictionary(type))
        {
            return DictionarySumFunctionWithResult(type);
        }

        if (type.IsArray)
        {
            return (Delegate)
                GetDelListSumFunctionWithResultMethod
                    .MakeGenericMethod(type.GetElementType(), type)
                    .Invoke(null, null);
        }

        if (type.IsList())
        {
            return (Delegate)
                GetDelListSumFunctionWithResultMethod
                    .MakeGenericMethod(type.GetGenericArguments().First(), type)
                    .Invoke(null, null);
        }

        return (Delegate)
            ClassSumFunctionWithReturnMethod.MakeGenericMethod(type).Invoke(null, null);
    }

    private static readonly MethodInfo GetDelListSumFunctionWithResultMethod =
        typeof(GenericSumFunctionProvider).GetMethod(
            nameof(ListSumFunctionWithResult),
            BindingFlags.Static | BindingFlags.NonPublic
        );

    private static Delegate ListSumFunctionWithResult<TEl, TArray>()
    {
        var sumActionElement = SumFunction.GetSumFunc(typeof(TEl));
        var sumArrayDel = SumActionListMethod.MakeGenericMethod(typeof(TEl), typeof(TArray));
        var x = Expression.Parameter(typeof(TArray));
        var y = Expression.Parameter(typeof(TArray));
        return Expression
            .Lambda<Func<TArray, TArray, TArray>>(
                Expression.Call(sumArrayDel, x, y, Expression.Constant(sumActionElement)),
                x,
                y
            )
            .Compile();
    }

    private static readonly MethodInfo SumActionListMethod =
        typeof(GenericSumFunctionProvider).GetMethod(
            nameof(SumFunctionWithResultList),
            BindingFlags.Static | BindingFlags.NonPublic
        );

    private static TArray SumFunctionWithResultList<T, TArray>(
        TArray ret,
        TArray x,
        Func<T, T, T> sum
    )
        where TArray : class, IList<T>
    {
        if (ret == null)
        {
            var newArray = new T[x.Count];
            if (
                typeof(TArray).IsArray
                || (
                    typeof(TArray).IsGenericType
                    && typeof(TArray).GetGenericTypeDefinition() == typeof(IList<>)
                )
            )
                ret = (TArray)(object)(newArray);
            else
                ret = Activator.CreateInstance<TArray>();
        }

        if ((ret.GetType().IsArray) && ret.Count < x.Count)
        {
            var newArray = new T[x.Count];
            Array.Copy((Array)(object)ret, 0, newArray, 0, ret.Count);
            ret = (TArray)(object)newArray;
        }

        for (var index = 0; index < Math.Max(ret.Count, x.Count); index++)
        {
            if (index >= ret.Count)
            {
                ret.Add(x[index]);
            }
            else if (index < x.Count)
            {
                ret[index] = sum(ret[index], x[index]);
            }
        }

        return ret;
    }

    private static readonly MethodInfo ClassSumFunctionWithReturnMethod =
        typeof(GenericSumFunctionProvider).GetMethod(
            nameof(ClassSumAction),
            BindingFlags.Static | BindingFlags.NonPublic
        );

    private static Delegate ClassSumAction<T>()
    {
        var x = Expression.Parameter(typeof(T), "x");
        var y = Expression.Parameter(typeof(T), "y");

        var props = GetAggregatableProperties(typeof(T));
        if (props.Count == 0)
            return Expression.Lambda<Func<T, T, T>>(Expression.Default(typeof(T)), x, y).Compile();

        var expressions = new List<Expression>
        {
            Expression.IfThen(
                Expression.Equal(x, Expression.Constant(null, x.Type)),
                InstantiateNew(x, y)
            )
        };
        expressions.AddRange(props.Values.Select(z => z(x, y)));
        expressions.Add(x);
        var blockExpression = Expression.Block(expressions);
        var notNull = Expression.Condition(
            Expression.NotEqual(y, Expression.Constant(null, y.Type)),
            blockExpression,
            x
        );

        return Expression.Lambda<Func<T, T, T>>(notNull, x, y).Compile();
    }

    internal static Expression InstantiateNew(ParameterExpression x, ParameterExpression y)
    {
        var ctors = x.Type.GetConstructors(
            BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public
        );
        // get ctor with least parameters, hopefully default
        // NB: auto-generated copy ctor doesn't accept `default` value and throws a NRE
        //     so we just filter out such constructors
        var ctor = ctors
            .Select(c => new { ctor = c, parameters = c.GetParameters() })
            .OrderBy(c => c.parameters.Length)
            .Where(cc => cc.parameters.Length != 1 || cc.parameters[0].ParameterType != x.Type)
            .Select(cc => cc.ctor)
            .FirstOrDefault();
        if (ctor == null)
            throw new ArgumentException($"No constructor found for type {x.Type.Name}");
        // here we must also support records of type public record MyRecord(int I, string Name)
        var newExpression = Expression.New(
            ctor,
            ctor.GetParameters().Select(p => Expression.Default(p.ParameterType))
        );
        var newStatements = new List<Expression> { Expression.Assign(x, newExpression) };
        foreach (var prop in x.Type.GetProperties().Where(IsPropertyCopiedIfEqual))
            newStatements.Add(
                Expression.Assign(Expression.Property(x, prop), Expression.Property(y, prop))
            );
        return Expression.Block(newStatements);
    }

    private static IDictionary<
        PropertyInfo,
        Func<Expression, Expression, Expression>
    > GetAggregatableProperties(Type type)
    {
        return type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(GetAddAssignExpressionMappingFunction)
            .Where(p => p != null)
            .Cast<KeyValuePair<PropertyInfo, Func<Expression, Expression, Expression>>>()
            .ToDictionary(x => x.Key, x => x.Value);
    }

    private static KeyValuePair<
        PropertyInfo,
        Func<Expression, Expression, Expression>
    >? GetAddAssignExpressionMappingFunction(PropertyInfo p)
    {
        if (!p.CanWrite || !p.CanRead)
            return null;
        if (p.PropertyType.IsNullableGeneric() && !p.HasAttribute<NotAggregatedAttribute>())
            return new KeyValuePair<PropertyInfo, Func<Expression, Expression, Expression>>(
                p,
                (x, y) => Expression.Assign(Expression.Property(x, p), WrapNullableSum(p, x, y))
            );
        if (
            (
                (p.PropertyType.IsIntegerType() || p.PropertyType.IsRealType())
                && !p.HasAttribute<NotAggregatedAttribute>()
                && !p.HasAttribute<IdentityPropertyAttribute>()
            )
        )
            return new KeyValuePair<PropertyInfo, Func<Expression, Expression, Expression>>(
                p,
                (x, y) => Expression.AddAssign(Expression.Property(x, p), Expression.Property(y, p))
            );
        if (
            (FormulaFrameworkTypeExtensions.IsDictionary(p.PropertyType) || p.PropertyType.IsList())
            && !p.HasAttribute<NotAggregatedAttribute>()
        )
            return new KeyValuePair<PropertyInfo, Func<Expression, Expression, Expression>>(
                p,
                (x, y) =>
                    Expression.Assign(
                        Expression.Property(x, p),
                        GetSumActionExpression(
                            p.PropertyType,
                            Expression.Property(x, p),
                            Expression.Property(y, p)
                        )
                    )
            );
        if (IsPropertyCopiedIfEqual(p))
            return new KeyValuePair<PropertyInfo, Func<Expression, Expression, Expression>>(
                p,
                (x, y) =>
                    Expression.Assign(
                        Expression.Property(x, p),
                        Expression.Condition(
                            Expression.Equal(Expression.Property(x, p), Expression.Property(y, p)),
                            Expression.Property(x, p),
                            Expression.Default(p.PropertyType)
                        )
                    )
            );
        return null;
    }

    private static bool IsPropertyCopiedIfEqual(PropertyInfo p)
    {
        return p.HasAttribute<IdentityPropertyAttribute>()
            || p.HasAttribute<AggregateByAttribute>()
            || p.HasAttribute<DimensionAttribute>();
    }

    private static UnaryExpression WrapNullableSum(PropertyInfo p, Expression x, Expression y)
    {
        var zeroConstant = Expression.Convert(Expression.Constant(0), p.PropertyType);
        return Expression.Convert(
            Expression.Add(
                Expression.Condition(
                    Expression.Equal(Expression.Property(x, p), Expression.Default(p.PropertyType)),
                    zeroConstant,
                    Expression.Property(x, p)
                ),
                Expression.Condition(
                    Expression.Equal(Expression.Property(y, p), Expression.Default(p.PropertyType)),
                    zeroConstant,
                    Expression.Property(y, p)
                )
            ),
            p.PropertyType
        );
    }

    private static Expression GetSumActionExpression(Type tVal, params Expression[] expression)
    {
        var ret = expression[0];
        var act = SumFunction.GetSumFunctionWithResult(tVal);

        var addExpressions = expression
            .Skip(1)
            .Select(x =>
                Expression.Condition(
                    Expression.NotEqual(x, Expression.Constant(null, x.Type)),
                    Expression.Assign(ret, Expression.Invoke(Expression.Constant(act), ret, x)),
                    ret
                )
            )
            .Cast<Expression>()
            .ToList();

        return Expression.Block(addExpressions);
    }

    private static Delegate DictionarySumFunctionWithResult(Type type)
    {
        var arguments = type.GetGenericArguments();
        var tKey = arguments[0];
        var tVal = arguments[1];

        var method = tVal.IsValueType ? SumDictionaryValueTypeMethod : SumDictionaryClassMethod;
        var sumDelegate = tVal.IsValueType
            ? SumFunction.GetSumFunc(tVal)
            : SumFunction.GetSumFunctionWithResult(tVal);
        method = method.MakeGenericMethod(tKey, tVal, type);
        var x = Expression.Parameter(type);
        var y = Expression.Parameter(type);
        return Expression
            .Lambda(Expression.Call(method, x, y, Expression.Constant(sumDelegate)), x, y)
            .Compile();
    }

    private static readonly MethodInfo SumDictionaryValueTypeMethod =
        typeof(GenericSumFunctionProvider).GetMethod(
            nameof(SumDictionaryValueType),
            BindingFlags.Static | BindingFlags.NonPublic
        );

    private static TDictionary SumDictionaryValueType<TKey, TVal, TDictionary>(
        TDictionary ret,
        TDictionary x,
        Func<TVal, TVal, TVal> sum
    )
        where TDictionary : class, IDictionary<TKey, TVal>
    {
        foreach (var summand in x)
        {
            ret = ret ?? (TDictionary)(object)new Dictionary<TKey, TVal>();
            if (!ret.TryGetValue(summand.Key, out var tmp))
                ret.Add(summand);
            else
            {
                ret[summand.Key] = sum(tmp, summand.Value);
            }
        }

        return ret;
    }

    private static readonly MethodInfo SumDictionaryClassMethod =
        typeof(GenericSumFunctionProvider).GetMethod(
            nameof(SumDictionaryClass),
            BindingFlags.Static | BindingFlags.NonPublic
        );

    private static TDictionary SumDictionaryClass<TKey, TVal, TDictionary>(
        TDictionary ret,
        TDictionary x,
        Func<TVal, TVal, TVal> sum
    )
        where TVal : new()
        where TDictionary : class, IDictionary<TKey, TVal>
    {
        foreach (var summand in x)
        {
            ret = ret ?? (TDictionary)(object)new Dictionary<TKey, TVal>();
            if (!ret.TryGetValue(summand.Key, out var tmp))
                tmp = new TVal();
            ret[summand.Key] = sum(tmp, summand.Value);
        }

        return ret;
    }
}
