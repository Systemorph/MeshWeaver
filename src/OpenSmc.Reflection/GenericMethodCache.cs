using System.Collections.Concurrent;
using System.Reflection;

namespace OpenSmc.Reflection
{
    internal sealed class GenericMethodCache : IGenericMethodCache
    {
        private readonly ConcurrentDictionary<TypeArrayKey, MethodInfo> dic = new();

        internal GenericMethodCache(MethodInfo genericMethod)
        {
            if(!genericMethod.IsGenericMethodDefinition)
                throw new ArgumentException("Generic method definition expected", nameof(genericMethod));

            GenericDefinition = genericMethod;
        }

        public MethodInfo GenericDefinition { get; }

        public MethodInfo MakeGenericMethod(params Type[] types)
        {
            if (types == null)
                throw new ArgumentNullException(nameof(types));
            if (types.Length == 0)
                throw new ArgumentException("Can't make generic method as no type parameters were specified", nameof(types));

            return dic.GetOrAdd(types.GetKey(), _ => GenericDefinition.MakeGenericMethod(types));
        }

        public override string ToString()
        {
            return string.Format("{0} for '{1}' from '{2}'", nameof(GenericMethodCache), GenericDefinition, GenericDefinition.DeclaringType);
        }
    }
}