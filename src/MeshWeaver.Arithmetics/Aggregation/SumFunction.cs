#nullable enable
using System.Collections.Concurrent;
using System.Collections.Immutable;
using MeshWeaver.Arithmetics.Aggregation.Implementation;

namespace MeshWeaver.Arithmetics.Aggregation
{
    /// <summary>
    /// The <see cref="SumFunction"/> class provides the <see cref="Sum{T}"/> method, which is a generalized + operator that works on a large variety of
    /// types (primitives, arrays, lists, dictionaries, custom classes) and calculates the sum for all properties in a "best applicable way". For details see
    /// <conceptualLink target="3b557ccf-d392-496f-933b-08672b0e2d02#sum" /> 
    /// </summary>
    /// <conceptualLink target="3b557ccf-d392-496f-933b-08672b0e2d02#sum" />
    public static class SumFunction
    {
        private static readonly ConcurrentDictionary<Type, Delegate> SumDelegateStore = new();

        private static ImmutableList<(Func<Type, bool> Filter, ISumFunctionProvider Provider)> sumFunctionProviders =
            ImmutableList<(Func<Type, bool> Filter, ISumFunctionProvider Provider)>.Empty
                .Add((_ => true, new GenericSumFunctionProvider()));

        /// <summary>
        /// Calculates the sum of <paramref name="x"/> and <paramref name="y"/>, where the sum is applied to all properties where applicable.
        /// For details see <conceptualLink target="3b557ccf-d392-496f-933b-08672b0e2d02#sum" />
        /// </summary>
        /// <typeparam name="T">Type parameter</typeparam>
        /// <param name="x">First operand</param>
        /// <param name="y">Second operand</param>
        /// <returns>A new instance of <typeparamref name="T"/> containing the sum of <paramref name="x"/> and <paramref name="y"/></returns>
        /// <exception cref="ArgumentException">Thrown, when the type <typeparamref name="T"/> does not have a default constructor</exception>
        /// <conceptualLink target="3b557ccf-d392-496f-933b-08672b0e2d02#sum" />
        public static T Sum<T>(T x, T y)
        {
            var func = GetSumFunc<T>();
            return func(x, y);
        }

        /// <summary>
        /// Gets the delegate of the Sum method
        /// </summary>
        /// <typeparam name="T">Type parameter</typeparam>
        /// <returns>The delegate of the Sum method</returns>
        /// <conceptualLink target="3b557ccf-d392-496f-933b-08672b0e2d02#sum" />
        public static Func<T, T, T> GetSumFunc<T>()
        {
            return (Func<T, T, T>)GetSumFunc(typeof(T));
        }


        /// <summary>
        /// Gets the delegate of the Sum method. Signature is <see cref="Func{T,T,T}"/>
        /// </summary>
        /// <param name="type">The type of the objects to sum up</param>
        /// <returns>The delegate of the Sum method</returns>
        /// <conceptualLink target="3b557ccf-d392-496f-933b-08672b0e2d02#sum" />
        public static Delegate GetSumFunc(Type type)
        {
            return SumDelegateStore.GetOrAdd(type, CreateSumDelegate);
        }

        private static Delegate CreateSumDelegate(Type type)
        {
            return SumDelegateStore.GetOrAdd(type, t => CreateDelegate(t, p => p.CreateSumDelegate(t)));
        }

        public static void RegisterSumProviderBefore<T>(ISumFunctionProvider provider, Func<Type, bool> filter)
        {
            var insert = sumFunctionProviders.FindIndex(x => x.Provider is T);
            sumFunctionProviders = sumFunctionProviders.Insert(insert, (filter, provider));
        }
        public static void RegisterSumProviderAfter<T>(ISumFunctionProvider provider, Func<Type, bool> filter)
        {
            var insert = sumFunctionProviders.FindIndex(x => x.Provider is T);
            sumFunctionProviders = sumFunctionProviders.Insert(insert + 1, (filter, provider));
        }

        /// <summary>
        /// Sums the <paramref name="value"/> into the <paramref name="target"/>.
        /// For details see <conceptualLink target="3b557ccf-d392-496f-933b-08672b0e2d02#sum" />
        /// </summary>
        /// <typeparam name="T">Type parameter</typeparam>
        /// <param name="target">Instance to sum into</param>
        /// <param name="value">The value to add into target</param>
        /// <returns>Reference to the updated <paramref name="target"/></returns>
        /// <exception cref="ArgumentException">Thrown, when the type <typeparamref name="T"/> does not have a default constructor</exception>
        /// <conceptualLink target="3b557ccf-d392-496f-933b-08672b0e2d02#sum" />
        public static T SumInto<T>(T target, T value)
            where T : class
        {
            var func = GetSumFunctionWithResult<T>();
            return func(target, value);
        }

        /// <summary>
        /// Gets a delegate which sums the second parameter into the instance of the first parameter and returns a reference to the updated first parameter.
        /// This is useful e.g. in loops, where to add multiple addends into one common result (aggregation).
        /// This method has the flavor of the += operator.
        /// </summary>
        /// <typeparam name="T">The type of the objects to sum up</typeparam>
        /// <returns>Delegate of the sum function</returns>
        /// <conceptualLink target="3b557ccf-d392-496f-933b-08672b0e2d02#sum" />
        public static Func<T, T, T> GetSumFunctionWithResult<T>()
            where T : class
        {
            return (Func<T, T, T>)GetSumFunctionWithResult(typeof(T));
        }

        private static readonly ConcurrentDictionary<Type, Delegate> SumFunctionWithResultDelegateStore = new();

        /// <summary>
        /// Non generic version of <see cref="GetSumFunctionWithResult{T}"/>
        /// </summary>
        /// <param name="type">The type of the objects to sum up</param>
        /// <returns>Delegate of the sum function</returns>
        /// <conceptualLink target="3b557ccf-d392-496f-933b-08672b0e2d02#sum" />
        public static Delegate GetSumFunctionWithResult(Type type)
        {
            return SumFunctionWithResultDelegateStore.GetOrAdd(type, t => CreateDelegate(t, p => p.CreateSumFunctionWithResult(t)));
        }

        private static Delegate CreateDelegate(Type type, Func<ISumFunctionProvider, Delegate> creationFunc)
        {
            var sumFunctionProvider = sumFunctionProviders.FirstOrDefault(x => x.Filter(type)).Provider;

            if (sumFunctionProvider != null)
                return creationFunc(sumFunctionProvider);

            throw new NotSupportedException();
        }
    }
}
