using Newtonsoft.Json.Serialization;
using OpenSmc.Serialization;

namespace OpenSmc.Messaging.Serialization;

public class SerializationBinder : DefaultSerializationBinder
{
    private readonly ITypeRegistry typeRegistry;

    public SerializationBinder(ITypeRegistry typeRegistry)
    {
        this.typeRegistry = typeRegistry;
    }

    public override Type BindToType(string assemblyName, string typeName)
    {
        return typeRegistry.TryGetType(typeName, out var type) 
                   ? type 
                   : base.BindToType(assemblyName, typeName) ?? typeof(RawJson);
    }

    public override void BindToName(Type serializedType, out string assemblyName, out string typeName)
    {
        assemblyName = null;
        typeName = typeRegistry.GetTypeName(serializedType);
    }
}