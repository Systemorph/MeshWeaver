using System.Reflection;

namespace OpenSmc.Serialization;

public interface IDataBindingRegistry
{
    IDataBindingRegistry RegisterTransformation<T>(Func<T, IDataBindingTransformationContext, object> transformation);
    IDataBindingRegistry RegisterMutation<T>(Action<T, IDataBindingMutationContext> mutation);
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
