using System.Reflection;

namespace OpenSmc.Serialization;

public delegate object SerializationTransform<T>(T value, ISerializationTransformContext context);
public delegate void SerializationMutate<T>(T value, IDataBindingMutateContext context);

public interface ICustomSerializationRegistry
{
    ICustomSerializationRegistry RegisterTransformation<T>(SerializationTransform<T> transform);
    ICustomSerializationRegistry RegisterMutation<T>(SerializationMutate<T> mutate);
}

public interface ISerializationTransformContext
{
    object TraverseValue(object value);
    object TraverseProperty(object propertyValue, object parent, PropertyInfo propertyInfo);
}

public interface IDataBindingMutateContext
{
    void SetProperty(string propName, object propValue);
    void DeleteProperty(string propName);
}
