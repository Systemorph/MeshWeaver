using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using MeshWeaver.Domain;
using MeshWeaver.Reflection;
using MeshWeaver.Utils;

namespace MeshWeaver.Arithmetics.Aggregation
{
    /// <summary>
    /// The <see cref="IdentityEqualityComparer{T, TEntity}"/> class is a generic EqualityComparer which tests the equality of two instances of <typeparamref name="T"/>
    /// by using all <see cref="IdentityPropertyAttribute"/>s declared on the type <typeparamref name="TTarget"/>.
    /// </summary>
    /// <typeparam name="T">Type of the instances to compare</typeparam>
    /// <typeparam name="TTarget">Entity type</typeparam>
    public class IdentityEqualityComparer<T, TTarget> : IEqualityComparer<T>
    {
        public static IdentityEqualityComparer<T, TTarget> Instance = new();

        private IdentityEqualityComparer()
        {
            subtypeEquality = new CreatableObjectStore<(Type, Type), Func<T, T, bool>>(
                CreateSubtypeAction
            );
            subtypeHashCode = new CreatableObjectStore<Type, Func<T, int>>(CreateHashCode);
            comparisonFunctionsLazy = new Lazy<(
                Func<T, T, bool> equalityFunc,
                Func<T, int> hashCodeFunc
            )>(CreateComparisonFunctions);
        }

        private static (
            Func<T, T, bool> equalityFunc,
            Func<T, int> hashCodeFunc
        ) CreateComparisonFunctions()
        {
            var identityProps = typeof(TTarget).GetIdentityOrSimilarProperties();
            if (typeof(T) != typeof(TTarget))
            {
                identityProps = identityProps
                    .Join(
                        typeof(T).GetProperties(BindingFlags.Instance | BindingFlags.Public),
                        s => s.Name,
                        t => t.Name,
                        (_, t) => t
                    )
                    .ToArray();
            }

            return IdentityEqualityComparerHelper.GetHashAndEqualityForType<T>(identityProps);
        }

        private Func<T, int> CreateHashCode(Type type)
        {
            if (type == typeof(T))
                return comparisonFunctionsLazy.Value.hashCodeFunc;

            var equalityComparerType = typeof(IdentityEqualityComparer<,>).MakeGenericType(
                type,
                GetTargetTypeOfPocoType(type)
            );

            var method = equalityComparerType.GetMethod(
                nameof(GetHashCodeImpl),
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { type },
                null
            );
            var instance = equalityComparerType.GetField(nameof(Instance))?.GetValue(null);
            var prm1 = Expression.Parameter(typeof(T));
            return Expression
                .Lambda<Func<T, int>>(
                    Expression.Call(
                        Expression.Constant(instance),
                        method!,
                        Expression.Convert(prm1, type)
                    ),
                    prm1
                )
                .Compile();
        }

        private readonly Lazy<(
            Func<T, T, bool> equalityFunc,
            Func<T, int> hashCodeFunc
        )> comparisonFunctionsLazy;

        private readonly CreatableObjectStore<(Type, Type), Func<T, T, bool>> subtypeEquality;
        private readonly CreatableObjectStore<Type, Func<T, int>> subtypeHashCode;

        private Func<T, T, bool> CreateSubtypeAction((Type t1, Type t2) types)
        {
            var t1Type = types.t1;
            var t2Type = types.t2;
            if (t1Type != t2Type)
            {
                if (!t1Type.HasIdentityProperties() && !t2Type.HasIdentityProperties())
                    return comparisonFunctionsLazy.Value.equalityFunc;
                return (_, _) => false;
            }

            var mainType = types.t1;
            if (mainType == typeof(T))
                return comparisonFunctionsLazy.Value.equalityFunc;
            var equalityComparerType = typeof(IdentityEqualityComparer<,>).MakeGenericType(
                mainType,
                GetTargetTypeOfPocoType(mainType)
            );
            var method = equalityComparerType.GetMethod(
                nameof(EqualsImpl),
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { mainType, mainType },
                null
            );
            var instance = equalityComparerType.GetField(nameof(Instance))!.GetValue(null);
            var prm1 = Expression.Parameter(typeof(T));
            var prm2 = Expression.Parameter(typeof(T));
            return Expression
                .Lambda<Func<T, T, bool>>(
                    Expression.Call(
                        Expression.Constant(instance),
                        method!,
                        Expression.Convert(prm1, mainType),
                        Expression.Convert(prm2, mainType)
                    ),
                    prm1,
                    prm2
                )
                .Compile();
        }

        public static Type GetTargetTypeOfPocoType(Type type)
        {
            var attribute = type.GetSingleCustomAttribute<TargetTypeAttribute>();

            return attribute?.Type ?? type;
        }

        public bool Equals(T? x, T? y)
        {
            if (x == null)
                return y == null;
            if (y == null)
                return false;

            return EqualsImpl(x, y);
        }

        private bool EqualsImpl(T x, T y)
        {
            var type1 = x!.GetType();
            var type2 = y!.GetType();

            return subtypeEquality.GetInstance((type1, type2))(x, y);
        }

        public int GetHashCode(T? obj)
        {
            // ReSharper disable once ConditionIsAlwaysTrueOrFalse
            if (obj == null) // This is still done on purpose, we might come with null to here and nulls must produce the same hash codes
                // ReSharper disable once HeuristicUnreachableCode
                return 17;
            return GetHashCodeImpl(obj);
        }

        private int GetHashCodeImpl(T obj)
        {
            return subtypeHashCode.GetInstance(obj!.GetType())(obj);
        }
    }

    public static class IdentityEqualityComparerHelper
    {
        public static (Func<T, T, bool>, Func<T, int>) GetHashAndEqualityForType<T>(
            PropertyInfo[] identityProps
        )
        {
            Func<T, T, bool> equalityFunc;
            Func<T, int> hashCodeFunc;
            if (identityProps.Length == 0)
            {
                equalityFunc = (x, y) => (x == null && y == null) || x != null && x.Equals(y);
                hashCodeFunc = x => x?.GetHashCode() ?? 0;
                return (equalityFunc, hashCodeFunc);
            }

            var prm1 = Expression.Parameter(typeof(T));
            var prm2 = Expression.Parameter(typeof(T));
            var expr = identityProps
                .Select(p => GetPropertyEqualityAndHash(p, prm1, prm2))
                .ToArray();
            var equalityExpr = expr.Select(x => x.equality).Aggregate(Expression.AndAlso);
            equalityFunc = Expression.Lambda<Func<T, T, bool>>(equalityExpr, prm1, prm2).Compile();

            Expression typeExpression;
            if (typeof(T).IsInterface)
                typeExpression = Expression.Constant(17);
            else
            {
                var callGetType = Expression.Call(prm1, nameof(GetType), Type.EmptyTypes);
                typeExpression = Expression.Call(callGetType, nameof(GetHashCode), Type.EmptyTypes);
            }

            var hashCodeExpr = Expression.ExclusiveOr(
                expr.Select(x => x.hash).Aggregate(Expression.ExclusiveOr),
                typeExpression
            );
            hashCodeFunc = Expression.Lambda<Func<T, int>>(hashCodeExpr, prm1).Compile();

            return (equalityFunc, hashCodeFunc);
        }

        private static (Expression equality, Expression hash) GetPropertyEqualityAndHash(
            PropertyInfo propertyInfo,
            ParameterExpression prm1,
            ParameterExpression prm2
        )
        {
            var eq = Expression.Equal(
                Expression.Property(prm1, propertyInfo),
                Expression.Property(prm2, propertyInfo)
            );
            var hash =
                propertyInfo.PropertyType.IsNullableGeneric()
                || !propertyInfo.PropertyType.IsValueType
                    ? (Expression)
                        Expression.Condition(
                            Expression.Equal(
                                Expression.Property(prm1, propertyInfo),
                                Expression.Constant(null, propertyInfo.PropertyType)
                            ),
                            Expression.Constant(17),
                            Expression.Call(
                                Expression.Property(prm1, propertyInfo),
                                GetHashCodeMethod
                            )
                        )
                    : Expression.Call(Expression.Property(prm1, propertyInfo), GetHashCodeMethod);

            return (eq, hash);
        }

        [SuppressMessage("ReSharper", "ReturnValueOfPureMethodIsNotUsed")]
        public static readonly MethodInfo GetHashCodeMethod = ReflectionHelper.GetMethod<object>(
            x => x.GetHashCode()
        );
    }
}
