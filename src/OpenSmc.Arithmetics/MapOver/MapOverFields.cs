using System.Collections;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using OpenSmc.Conventions;
using OpenSmc.Reflection;

namespace OpenSmc.Arithmetics.MapOver
{
    /// <summary>
    /// The <see cref="MapOverFields"/> class provides the <see cref="MapOver{T}(ArithmeticOperation,double,T)"/> method,
    /// which is a generalized way to
    /// - scale all double values by a factor
    /// - add a constant to all double values
    /// - raise all double values to a power
    /// that works on a large variety of
    /// types (primitives, arrays, lists, dictionaries, custom classes) and calculates the result for all properties in a "best applicable way". For details see
    /// <conceptualLink target="3b557ccf-d392-496f-933b-08672b0e2d02#mapOverFields" />
    /// </summary>
    /// <conceptualLink target="3b557ccf-d392-496f-933b-08672b0e2d02#mapOverFields" />
    /// <conceptualLink target="799cb1b4-2638-49fb-827a-43131d364f06" />
    /// <seealso cref="NoArithmeticsAttribute"/>
    /// <seealso cref="DoNotCopyAttribute"/>
    public static class MapOverFields
    {
        private static readonly ConcurrentDictionary<Type, IMapOverFunctionProvider> MapOverDelegateProviders = new();



        //private static readonly IDictionary<ArithmeticOperation, Func<double, double, double>> Methods = new Dictionary<ArithmeticOperation, Func<double, double, double>>
        //                                                                                        {
        //                                                                                            { ArithmeticOperation.Plus, (factor, x) => factor + x },
        //                                                                                            { ArithmeticOperation.Power, (factor, x) => Math.Pow(x, factor) },
        //                                                                                            { ArithmeticOperation.Scale, (factor, x) => factor * x }
        //                                                                                        };


        private static readonly ConcurrentDictionary<ArithmeticOperation, ConcurrentDictionary<Type, Delegate>> MapOverDelegateStore = new();

        private static readonly MethodInfo MapOverEnumerableMethod = typeof(MapOverFields).GetMethod(nameof(MapOverEnumerable), BindingFlags.Static | BindingFlags.NonPublic);


        private static readonly MethodInfo MapOverArrayMethod = typeof(MapOverFields).GetMethod(nameof(MapOverArray), BindingFlags.Static | BindingFlags.NonPublic);

        private static readonly MethodInfo MapOverListMethod = typeof(MapOverFields).GetMethod(nameof(MapOverList), BindingFlags.Static | BindingFlags.NonPublic);

        //private static readonly MethodInfo MapOnNullableDoubleDelegate = typeof(MapOverFields).GetMethod(nameof(MapOnNullableDouble), BindingFlags.Static | BindingFlags.NonPublic);


        private static readonly MethodInfo MapOverDictionaryMethod = typeof(MapOverFields).GetMethod(nameof(MapOverDictionary), BindingFlags.Static | BindingFlags.NonPublic);

        /// <summary>
        /// Applies one of the supported methods to all applicable properties of the type <typeparamref name="T"/>
        /// </summary>
        /// <typeparam name="T">Type parameter</typeparam>
        /// <param name="method">String representation of one the supported methods</param>
        /// <param name="factor">The factor for scaling, constant for plus or exponent for power</param>
        /// <param name="x">The object which is "mapped over"</param>
        /// <returns>A new instance of <typeparamref name="T"/>, or same, unmodified input <paramref name="x"/> </returns>
        public static T MapOver<T>(string method, double factor, T x)
        {
            return MapOver((ArithmeticOperation)Enum.Parse(typeof(ArithmeticOperation), method), factor, x);
        }

        
        public static T MapOver<T>(ArithmeticOperation method, double factor, T x)
        {
            var func = (Func<double, T, T>)GetMapOverDelegate(typeof(T), method);
            return func(factor, x);
        }

        public static void RegisterMapOverProvider(IMapOverFunctionProvider provider, Action<MapOverDelegateConventionService, Type> conventionAction)
        {
            var type = provider.GetType();
            conventionAction(MapOverDelegateConventionService.Instance, type);
            MapOverDelegateProviders[provider.GetType()] = provider;
        }


        /// <summary>
        /// Gets the delegate, that performs the MapOver function
        /// </summary>
        /// <typeparam name="T">Type parameter</typeparam>
        /// <param name="method">One of the supported operations</param>
        /// <returns>MapOver Delegate</returns>
        public static Func<double, T, T> GetMapOver<T>(ArithmeticOperation method)
        {
            return (Func<double, T, T>)GetMapOverDelegate(typeof(T), method);
        }

        /// <summary>
        /// Non-generic Version of <see cref="GetMapOver{T}"/>. Signature of the delegate: <see cref="Func{Double,T,T}"/>
        /// </summary>
        /// <param name="type">The type of the class to map over</param>
        /// <param name="method">One of the supported operations</param>
        /// <returns>MapOver Delegate</returns>
        public static Delegate GetMapOverDelegate(Type type, ArithmeticOperation method)
        {
            var dic = MapOverDelegateStore.GetOrAdd(method, _ => new ConcurrentDictionary<Type, Delegate>());
            return dic.GetOrAdd(type, x => CreateMapOverDelegate(x, method));
        }

        /// <summary>
        /// Mystery method. Is this old, legacy FormulaFramework? Can it be deleted? Strange Architecture. 
        /// </summary>
        public static void Register<T>(ArithmeticOperation method, Func<double, T, T> func)
        {
            MapOverDelegateStore.GetOrAdd(method, _ => new ConcurrentDictionary<Type, Delegate>()).TryAdd(typeof(T), func);
        }

        private static Expression GetTrivialCases(ArithmeticOperation method, Expression factor)
        {
            switch (method)
            {
                case ArithmeticOperation.Plus:
                    return Expression.Equal(factor, Expression.Constant(0d));
                case ArithmeticOperation.Scale:
                case ArithmeticOperation.Power:
                    return Expression.Equal(factor, Expression.Constant(1d));
                default:
                    return Expression.Constant(false);
            }
        }

        private static Delegate GetMapOverDelegate(PropertyInfo prop, ArithmeticOperation method)
        {
            var type = prop.PropertyType;
            var noArithmeticsAttribute = prop.GetCustomAttribute<NoArithmeticsAttribute>();

            var isTrivial = noArithmeticsAttribute != null && (noArithmeticsAttribute.Operations & method) == method || prop.PropertyType.IsIntegerType();
            return isTrivial ? CreateTrivialMapOverDelegate(type) : GetMapOverDelegate(type, method);
        }

        private static Delegate CreateMapOverDelegate(Type type, ArithmeticOperation method)
        {
            var mapOverDelegateProvider = MapOverDelegateConventionService.Instance.Reorder(MapOverDelegateProviders.Values, type).FirstOrDefault();

            if (mapOverDelegateProvider != null)
                return mapOverDelegateProvider.GetDelegate(type, method);

            return CreateTrivialMapOverDelegate(type);
        }

        internal static Delegate CreateMapOverDelegateForEnumerable(Type type, ArithmeticOperation method)
        {
            var factor = Expression.Parameter(typeof(double));
            var obj = Expression.Parameter(type);
            var elementType = type.GetEnumerableElementType();
            var mapOverArray = MapOverEnumerableMethod.MakeGenericMethod(elementType);
            return Expression.Lambda(Expression.Call(mapOverArray, factor, obj, Expression.Constant(method)), factor, obj).Compile();
        }

        private static IEnumerable<T> MapOverEnumerable<T>(double factor, IEnumerable<T> value, ArithmeticOperation method)
        {
            if (value == null)
                return null;
            return value.Select(x => MapOver(method, factor, x));
        }

        private static T[] MapOverArray<T>(double factor, T[] value, ArithmeticOperation method)
        {
            if (value == null)
                return null;
            var res = new T[value.Length];
            for (var i = 0; i < value.Length; i++)
                res[i] = MapOver(method, factor, value[i]);

            return res;
        }

        internal static Delegate CreateMapOverDelegateForArray(Type type, ArithmeticOperation method)
        {
            var factor = Expression.Parameter(typeof(double));
            var obj = Expression.Parameter(type);
            var elementType = type.GetElementType();
            var mapOverArray = MapOverArrayMethod.MakeGenericMethod(elementType);
            return Expression.Lambda(Expression.Call(mapOverArray, factor, obj, Expression.Constant(method)), factor, obj).Compile();
        }

        internal static Delegate CreateMapOverDelegateForList(Type type, ArithmeticOperation method)
        {
            var factor = Expression.Parameter(typeof(double));
            var obj = Expression.Parameter(type);
            var typeList = type.GetGenericArguments().First();
            var mapOverList = MapOverListMethod.MakeGenericMethod(typeList);
            return Expression.Lambda(Expression.Call(mapOverList, factor, obj, Expression.Constant(method)), factor, obj).Compile();
        }

        private static List<T> MapOverList<T>(double factor, List<T> value, ArithmeticOperation method)
        {
            return value?.Select(x => MapOver(method, factor, x)).ToList();
        }

        internal static bool ImplementEnumerable(this Type type)
        {
            return typeof(IEnumerable).IsAssignableFrom(type);
        }
        internal static Delegate CreateMapOverDelegateForValueType(Type type, ArithmeticOperation method)
        {
            var factor = Expression.Parameter(typeof(double));
            var obj = Expression.Parameter(type);

            Expression operation;
            switch (method)
            {
                case ArithmeticOperation.Plus:
                    operation = MapOperationToDoubleAndBack(factor,obj,Expression.Add);
                    break;
                case ArithmeticOperation.Power:
                    operation = MapOperationToDoubleAndBack(factor, obj, (f,o) => Expression.Power(o,f));
                    break;
                case ArithmeticOperation.Scale:
                    operation = MapOperationToDoubleAndBack(factor, obj, Expression.Multiply);
                    break;
                default:
                    throw new ArgumentException();
            }

            return Expression.Lambda(operation, factor, obj).Compile();
        }

        private static Expression MapOperationToDoubleAndBack(ParameterExpression factor, ParameterExpression obj, Func<Expression,Expression, Expression> operation)
        {
            var type = obj.Type;
            var _ = type.GetGenericArgumentTypes(typeof(Nullable<>))?.First();

            Expression converted = Expression.Convert(obj, typeof(double));
            Expression ret = Expression.Convert(operation(factor, converted), obj.Type);
            ret = Expression.Condition(Expression.Equal(obj, Expression.Default(type)), Expression.Default(type), ret);
            return ret;
        }

        //private static double? MapOnNullableDouble(double factor, double? value, ArithmeticOperation method)
        //{
        //    return value.HasValue ? Methods[method](factor, value.Value) : (double?)null;
        //}

        internal static bool HasParameterlessConstructor(this Type type)
        {
            return type.GetConstructor(Type.EmptyTypes) != null;
        }

        private static Delegate CreateTrivialMapOverDelegate(Type type)
        {
            var prm = Expression.Parameter(type);
            var factor = Expression.Parameter(typeof(double));
            return Expression.Lambda(prm, factor, prm).Compile();
        }

        internal static Delegate CreateMapOverDelegateForClass(Type type, ArithmeticOperation method)
        {
            var props = GetMappableProperties(type);
            var prm = Expression.Parameter(type);
            var factor = Expression.Parameter(typeof(double));
            var ret = Expression.Variable(type);
            var expr = new List<Expression> { Expression.Assign(ret, Expression.New(type)) };
            expr.AddRange(props.Select(p => Expression.Assign(Expression.Property(ret, p), Expression.Invoke(Expression.Constant(GetMapOverDelegate(p, method)), factor, Expression.Property(prm, p)))).ToList());
            expr.Add(ret);
            var block = Expression.Block(new[] { ret }, expr);
            // handle trivial cases
            var cond = Expression.Condition(Expression.OrElse(Expression.Equal(prm, Expression.Constant(null, type)), GetTrivialCases(method, factor)), prm, block);
            return Expression.Lambda(cond, factor, prm).Compile();
        }

        private static IEnumerable<PropertyInfo> GetMappableProperties(Type type)
        {
            foreach (var propertyInfo in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
                if (propertyInfo.CanWrite && !Attribute.IsDefined(propertyInfo, typeof(DoNotCopyAttribute)))
                    yield return propertyInfo;
        }


        internal static Delegate CreateMapOverDelegateForDictionary(Type type, ArithmeticOperation method)
        {
            var tKey = type.GenericTypeArguments[0];
            var tVal = type.GenericTypeArguments[1];
            var globalMethod = MapOverDictionaryMethod.MakeGenericMethod(tKey, tVal);
            var factor = Expression.Parameter(typeof(double), "factor");
            var x = Expression.Parameter(type, "x");
            var mapOverFunc = GetMapOverDelegate(tVal, method);
            var call = Expression.Call(globalMethod, factor, x, Expression.Constant(mapOverFunc));
            return Expression.Lambda(call, factor, x).Compile();
        }

        private static Dictionary<TKey, TValue> MapOverDictionary<TKey, TValue>(double factor, IDictionary<TKey, TValue> dictionary, Func<double, TValue, TValue> mapOverFunc)
        {
            return dictionary?.ToDictionary(x => x.Key, x => mapOverFunc(factor, x.Value));
        }
    }
}
