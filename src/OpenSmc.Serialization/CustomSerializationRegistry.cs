using OpenSmc.TypeRelevance;

namespace OpenSmc.Serialization;

public class CustomSerializationRegistry : ICustomSerializationRegistry
{
    public readonly SortByTypeRelevanceRegistry<SerializationTransform<object>> Transformations = new();
    public readonly SortByTypeRelevanceRegistry<SerializationMutate<object>> Mutations = new();


    public ICustomSerializationRegistry RegisterTransformation<T>(SerializationTransform<T> transformation)
    {
        Transformations.Register(typeof(T), (obj, c) => transformation((T)obj, c));
        return this;
    }

    public ICustomSerializationRegistry RegisterMutation<T>(SerializationMutate<T> mutation)
    {
        Mutations.Register(typeof(T), (obj, context) => mutation((T)obj, context));
        return this;
    }
}