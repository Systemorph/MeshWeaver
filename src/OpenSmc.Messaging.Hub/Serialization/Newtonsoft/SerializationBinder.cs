using Newtonsoft.Json.Serialization;
using OpenSmc.Serialization;

namespace OpenSmc.Messaging.Serialization.Newtonsoft;

public class SerializationBinder(ITypeRegistry typeRegistry) : DefaultSerializationBinder
{
    public override Type BindToType(string assemblyName, string typeName)
    {
        return typeRegistry.TryGetType(typeName, out var type)
                   ? type
                   : base.BindToType(assemblyName, typeName);
    }

    public override void BindToName(Type serializedType, out string assemblyName, out string typeName)
    {
        assemblyName = null;
        if (!typeRegistry.TryGetTypeName(serializedType, out typeName))
            base.BindToName(serializedType, out assemblyName, out typeName);
    }
}