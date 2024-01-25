using OpenSmc.Collections;
using OpenSmc.Domain.Abstractions.Attributes;
using OpenSmc.Reflection;

namespace OpenSmc.Equality
{
    public static class TypeWithInterfaceExtensions
    {
        private static readonly CreatableObjectStore<Type, Type> GetEntityInterfaceCache = new(key =>
                                                                                               {
                                                                                                   var attribute = key.GetSingleCustomAttribute<TypeWithInterfaceAttribute>();
                                                                                                   return attribute?.InterfaceType;
                                                                                               });

        public static Type GetTypeInterface(this Type type)
        {
            return GetEntityInterfaceCache.GetInstance(type);
        }
    }
}
