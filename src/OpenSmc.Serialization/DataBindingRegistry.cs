using OpenSmc.TypeRelevance;

namespace OpenSmc.Serialization;

public class DataBindingRegistry : IDataBindingRegistry
{
    public readonly SortByTypeRelevanceRegistry<Func<object, IDataBindingTransformationContext, object>> Transformations = new();
    public readonly SortByTypeRelevanceRegistry<Action<object, IDataBindingMutationContext>> Mutations = new();


    public IDataBindingRegistry RegisterTransformation<T>(Func<T, IDataBindingTransformationContext, object> transformation)
    {
        Transformations.Register(typeof(T), (obj, c) => transformation((T)obj, c));
        return this;
    }

    public IDataBindingRegistry RegisterMutation<T>(Action<T, IDataBindingMutationContext> mutation)
    {
        Mutations.Register(typeof(T), (obj, context) => mutation((T)obj, context));
        return this;
    }
}