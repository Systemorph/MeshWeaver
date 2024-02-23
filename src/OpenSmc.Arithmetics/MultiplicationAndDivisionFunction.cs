using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;

namespace OpenSmc.Arithmetics
{
    public class MultiplicationAndDivisionFunction
    {
        public enum Operation { Multiply, Divide }
        public static T Divide<T>(T x, T y)
        {
            var func = (Func<T, T, T>)GetFunction(typeof(T), Operation.Divide);
            return func(x, y);
        }
        public static T Multiply<T>(T x, T y)
        {
            var func = (Func<T, T, T>)GetFunction(typeof(T), Operation.Multiply);
            return func(x, y);
        }

        public static T Multiply<T>(T x, double y)
        {
            var func = (Func<T, double, T>)GetFunction(typeof(T), Operation.Multiply);
            return func(x, y);
        }

        public static void Register<T>(Operation operation, Func<T, T, T> del)
        {
            DelegateStore.GetOrAdd(operation, _ => new ConcurrentDictionary<Type, Delegate>()).TryAdd(typeof(T), del);
        }


        private static readonly ConcurrentDictionary<Operation, ConcurrentDictionary<Type, Delegate>> DelegateStore = new ConcurrentDictionary<Operation, ConcurrentDictionary<Type, Delegate>>();
        public static Delegate GetFunction(Type type, Operation operation)
        {
            var inner = DelegateStore.GetOrAdd(operation, x => new ConcurrentDictionary<Type, Delegate>());
            return inner.GetOrAdd(type, t => CreateDelegate(type, operation));
        }

        private static Delegate CreateDelegate(Type type, Operation operation)
        {
            var arg1 = Expression.Parameter(type);
            var arg2 = Expression.Parameter(type);
            switch (operation)
            {
                case Operation.Divide:
                    if (type.IsValueType || type.GetMethod("op_Division", new[] { type, type }) != null)
                        return Expression.Lambda(Expression.Divide(arg1, arg2), arg1, arg2).Compile();
                    break;
                case Operation.Multiply:
                    if (type.IsValueType || type.GetMethod("op_Multiply", new[] { type, type }) != null)
                        return Expression.Lambda(Expression.Multiply(arg1, arg2), arg1, arg2).Compile();
                    break;

            }

            if (type.IsDictionary())
            {
                return CreateDelegateForDictionary(type, operation);
            }
            if (type.IsClass)
            {
                return TreatClass(type, operation);
            }
            throw new NotSupportedException();
        }



        private static Delegate TreatClass(Type type, Operation operation)
        {
            var props = GetTreatableProperties(type);
            var arg1 = Expression.Parameter(type);
            var arg2 = Expression.Parameter(type);
            var ret = Expression.Variable(type);
            var mainOp = new List<Expression> { Expression.Assign(ret, Expression.New(type)) };
            mainOp.AddRange(props.Select(p => Expression.Assign(Expression.Property((Expression)ret, (PropertyInfo)p), Expression.Invoke(Expression.Constant(GetFunction(p.PropertyType, operation)), Expression.Property((Expression)arg1, (PropertyInfo)p), Expression.Property((Expression)arg2, (PropertyInfo)p)))).ToList());
            var trivialCases = Expression.IfThenElse(Expression.OrElse(Expression.Equal(arg1, Expression.Constant(null, type)), Expression.Equal(arg2, Expression.Constant(null, type))), Expression.Assign(ret, Expression.Constant(null, type)), Expression.Block(mainOp));
            return Expression.Lambda(Expression.Block(new[] { ret }, trivialCases, ret), arg1, arg2).Compile();
        }

        private static IEnumerable<PropertyInfo> GetTreatableProperties(Type type)
        {
            foreach (var propertyInfo in type.GetProperties())
            {
                if (propertyInfo.CanWrite && (propertyInfo.PropertyType == typeof(double) || propertyInfo.PropertyType.IsDictionary() || (propertyInfo.PropertyType.IsClass && propertyInfo.PropertyType != typeof(string))))
                    yield return propertyInfo;
            }
        }


        private static Delegate CreateDelegateForDictionary(Type type, Operation operation)
        {
            var tKey = type.GenericTypeArguments[0];
            var tVal = type.GenericTypeArguments[1];
            var method = DivideDictionaryMethod.MakeGenericMethod(tKey, tVal);
            var numerator = Expression.Parameter(type);
            var denominator = Expression.Parameter(type);
            var divideFunc = GetFunction(tVal, operation);
            var call = Expression.Call(method, numerator, denominator, Expression.Constant(divideFunc));
            return Expression.Lambda(call, numerator, denominator).Compile();
        }

        private static readonly MethodInfo DivideDictionaryMethod = typeof(MultiplicationAndDivisionFunction).GetMethod(nameof(DivideDictionary), BindingFlags.Static | BindingFlags.NonPublic);

        private static Dictionary<TKey, TValue> DivideDictionary<TKey, TValue>(IDictionary<TKey, TValue> numerator, IDictionary<TKey, TValue> denominator, Func<TValue, TValue, TValue> DivideFunc)
            where TValue : new()
        {
            return numerator.Keys.Where(denominator.ContainsKey).Select(x => new { key = x, top = numerator[x], bottom = denominator[x] })
                            .ToDictionary(x => x.key, x => DivideFunc(x.top, x.bottom));
        }
    }
}
