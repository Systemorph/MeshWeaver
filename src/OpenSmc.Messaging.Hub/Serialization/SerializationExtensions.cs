using System.Collections.Immutable;
using OpenSmc.Serialization;

namespace OpenSmc.Messaging.Serialization;

public static class SerializationExtensions
{
    public static MessageHubConfiguration WithSerialization(this MessageHubConfiguration hubConf,
        Func<SerializationConfiguration, SerializationConfiguration> configure)
    {
        var conf = hubConf.Get<SerializationConfiguration>();
        conf = configure(conf);
        return hubConf.Set(conf);
    }
}

