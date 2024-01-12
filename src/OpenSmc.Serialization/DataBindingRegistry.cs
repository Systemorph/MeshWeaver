using OpenSmc.TypeRelevance;

namespace OpenSmc.Serialization;

public class DataBindingRegistry : IDataBindingRegistry
{
    public readonly SortByTypeRelevanceRegistry<Func<object, IDataBindingTransformationContext, Task<object>>> Transformations = new();
    public readonly SortByTypeRelevanceRegistry<Func<object, IDataBindingMutationContext, Task>> Mutations = new();


    public IDataBindingRegistry RegisterTransformation<T>(Func<T, IDataBindingTransformationContext, Task<object>> transformation)
    {
        Transformations.Register(typeof(T), (obj, c) => transformation((T)obj, c));
        return this;
    }

    public IDataBindingRegistry RegisterMutation<T>(Func<T, IDataBindingMutationContext, Task> mutation)
    {
        Mutations.Register(typeof(T), (obj, context) => mutation((T)obj, context));
        return this;
    }
}