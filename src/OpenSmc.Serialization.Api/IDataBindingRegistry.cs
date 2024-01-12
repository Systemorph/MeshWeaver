using System.Reflection;

namespace OpenSmc.Serialization;

public interface IDataBindingRegistry
{
    IDataBindingRegistry RegisterTransformation<T>(Func<T, IDataBindingTransformationContext, Task<object>> transformation);
    IDataBindingRegistry RegisterTransformation<T>(Func<T, IDataBindingTransformationContext, object> transformation)
        => RegisterTransformation<T>((obj, context) => Task.FromResult(transformation(obj, context)));

    IDataBindingRegistry RegisterMutation<T>(Func<T, IDataBindingMutationContext, Task> mutation);

    IDataBindingRegistry RegisterMutation<T>(Action<T, IDataBindingMutationContext> mutation) => RegisterMutation<T>((obj, context) =>
                                                                                                                     {
                                                                                                                         mutation(obj, context);
                                                                                                                         return Task.CompletedTask;
                                                                                                                     });
}

public interface IDataBindingTransformationContext
{
    Task<object> TraverseValueAsync(object value);
    Task<object> TraversePropertyAsync(object propertyValue, object parent, PropertyInfo propertyInfo);
}

public interface IDataBindingMutationContext
{
    void SetProperty(string propName, object propValue);
    void DeleteProperty(string propName);
}
