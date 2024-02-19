using System.Reflection;
using System.Runtime.CompilerServices;

namespace OpenSmc.Serialization;
public interface ISerializationContext
{
    IServiceProvider ServiceProvider { get; }
    object OriginalValue { get; }
    PropertyInfo ParentProperty { get; }
    object Parent { get; }
    int Depth { get; }
    void SetResult(object value);
    object TraverseProperty(object propertyValue, object parent, PropertyInfo propertyInfo);
    object TraverseValue(object value);
    object SetProperty(string propName, object propValue);
    void DeleteProperty(string propName);
}