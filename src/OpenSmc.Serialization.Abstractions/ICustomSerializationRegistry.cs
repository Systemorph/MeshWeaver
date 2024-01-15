using System.Reflection;

namespace OpenSmc.Serialization;

public delegate object SerializationTransform(object value, IDataBindingTransformationContext context);
public delegate void SerializationMutate(object value, IDataBindingTransformationContext context);

public interface ICustomSerializationRegistry
{
    ICustomSerializationRegistry RegisterTransformation<T>(SerializationTransform transform);
    ICustomSerializationRegistry RegisterMutation<T>(SerializationMutate mutate);
}

public interface IDataBindingTransformationContext
{
    object TraverseValue(object value);
    object TraverseProperty(object propertyValue, object parent, PropertyInfo propertyInfo);
}

public interface IDataBindingMutationContext
{
    void SetProperty(string propName, object propValue);
    void DeleteProperty(string propName);
}
