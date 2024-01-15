using System.Reflection;

namespace OpenSmc.Serialization;

public interface ISerializationService
{
    string SerializeToString(object obj);
    RawJson SerializeAsync(object obj) => new(SerializeToString(obj));
    string SerializePropertyToString(object value, object obj, PropertyInfo property);
    RawJson SerializePropertyAsync(object value, object obj, PropertyInfo property) => new (SerializePropertyToString(value, obj, property));
    object Deserialize(string content);
    object Deserialize(RawJson rawJson) => Deserialize(rawJson.Content);
}
