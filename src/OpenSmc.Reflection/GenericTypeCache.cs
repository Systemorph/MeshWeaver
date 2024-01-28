using System.Collections.Concurrent;

namespace OpenSmc.Reflection
{
    internal sealed class GenericTypeCache : IGenericTypeCache
    {
        private readonly ConcurrentDictionary<TypeArrayKey, Type> dic = new();

        internal GenericTypeCache(Type genericType)
        {
            if (genericType == null)
                throw new ArgumentNullException(nameof(genericType));
            if (!genericType.IsGenericTypeDefinition)
                throw new ArgumentException("Generic type definition was expected", nameof(genericType));

            GenericDefinition = genericType;
        }

        public Type GenericDefinition { get; }

        public Type MakeGenericType(params Type[] types)
        {
            if (types == null)
                throw new ArgumentNullException(nameof(types));
            if (types.Length == 0)
                throw new ArgumentException("Can't make generic type as no type parameters were specified", nameof(types));

            return dic.GetOrAdd(types.GetKey(), _ => GenericDefinition.MakeGenericType(types));
        }

        public override string ToString()
        {
            return string.Format("{0} for '{1}'", nameof(GenericTypeCache), GenericDefinition);
        }
    }
}