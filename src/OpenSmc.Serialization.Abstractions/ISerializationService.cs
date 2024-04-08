using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;

[assembly: InternalsVisibleTo("OpenSmc.Messaging.Hub")]

namespace OpenSmc.Serialization;

public interface ISerializationService
{
    string SerializeToString(object obj);
    RawJson Serialize(object obj) => new(SerializeToString(obj));
    object Deserialize(string content);
    object Deserialize(RawJson rawJson) => Deserialize(rawJson.Content);

}



