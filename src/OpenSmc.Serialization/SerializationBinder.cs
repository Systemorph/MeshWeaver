using Newtonsoft.Json.Serialization;

namespace OpenSmc.Serialization;

public class SerializationBinder : DefaultSerializationBinder
{
    private readonly IEventsRegistry eventRegistry;

    public SerializationBinder(IEventsRegistry eventRegistry)
    {
        this.eventRegistry = eventRegistry;
    }

    public override Type BindToType(string assemblyName, string typeName)
    {
        return eventRegistry.TryGetType(typeName, out var type) 
                   ? type 
                   : base.BindToType(assemblyName, typeName) ?? typeof(RawJson);
    }

    public override void BindToName(Type serializedType, out string assemblyName, out string typeName)
    {
        if (eventRegistry.TryGetTypeName(serializedType, out typeName))
        {
            assemblyName = null;
            return;
        }

        base.BindToName(serializedType, out assemblyName, out typeName);
    }
}