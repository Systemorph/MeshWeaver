using System.Reflection;

namespace OpenSmc.Serialization;

public interface ISerializationService
{
    Task<string> SerializeToStringAsync(object obj);
    async Task<RawJson> SerializeAsync(object obj) => new(await SerializeToStringAsync(obj));
    Task<string> SerializePropertyToStringAsync(object value, object obj, PropertyInfo property);
    async Task<RawJson> SerializePropertyAsync(object value, object obj, PropertyInfo property) => new (await SerializePropertyToStringAsync(value, obj, property));
    object Deserialize(string content);
    object Deserialize(RawJson rawJson) => Deserialize(rawJson.Content);
}
