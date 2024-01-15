using OpenSmc.TypeRelevance;

namespace OpenSmc.Serialization;

public class CustomSerializationRegistry : ICustomSerializationRegistry
{
    public readonly SortByTypeRelevanceRegistry<SerializationTransform> Transformations = new();
    public readonly SortByTypeRelevanceRegistry<SerializationMutate> Mutations = new();


    public ICustomSerializationRegistry RegisterTransformation<T>(SerializationTransform transformation)
    {
        Transformations.Register(typeof(T), (obj, c) => transformation((T)obj, c));
        return this;
    }

    public ICustomSerializationRegistry RegisterMutation<T>(SerializationMutate mutation)
    {
        Mutations.Register(typeof(T), (obj, context) => mutation((T)obj, context));
        return this;
    }
}