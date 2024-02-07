using Newtonsoft.Json.Serialization;

namespace OpenSmc.Serialization;

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
        if (typeRegistry.TryGetTypeName(serializedType, out typeName))
        {
            assemblyName = null;
            return;
        }

        base.BindToName(serializedType, out assemblyName, out typeName);
    }
}