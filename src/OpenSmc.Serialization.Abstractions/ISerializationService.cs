using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
[assembly: InternalsVisibleTo("OpenSmc.Messaging.Hub")]

namespace OpenSmc.Serialization;

public interface ISerializationService
{
    string SerializeToString(object obj);
    RawJson Serialize(object obj) => new(SerializeToString(obj));
    string SerializePropertyToString(object value, object obj, PropertyInfo property);
    RawJson SerializeProperty(object value, object obj, PropertyInfo property) => new (SerializePropertyToString(value, obj, property));
    object Deserialize(string content);
    object Deserialize(RawJson rawJson) => Deserialize(rawJson.Content);

    SerializationConfiguration Configuration { get; }
}

public record SerializationConfiguration
{
    internal ImmutableList<TypeFactory> TypeFactories { get; init; } = ImmutableList<TypeFactory>.Empty;

    public ImmutableList<Func<ISerializationContext, object, object>> Transformations { get; init; } =
        ImmutableList<Func<ISerializationContext, object, object>>.Empty;

    public ImmutableList<Action<ISerializationContext, object>> Mutations { get; init; } =
        ImmutableList<Action<ISerializationContext, object>>.Empty;

    public SerializationConfiguration WithTypeFactory(Func<Type, object> factory, Func<Type, bool> filter) =>
        this with { TypeFactories = TypeFactories.Add(new(factory, filter)) };

    public SerializationConfiguration WithTransformation<T>(Func<ISerializationContext, T, object> transformation)
        => this with
        {
            Transformations = Transformations.Add((context, instance) =>
                instance is T t ? transformation.Invoke(context, t) : instance)
        };

    public SerializationConfiguration WithMutation(Action<ISerializationContext, object> action)
        => this with
        {
            Mutations = Mutations.Add((context, instance) =>
            {
                action.Invoke(context, action);
            })
        };

    public SerializationConfiguration WithMutation<T>(Action<ISerializationContext, T> action)
        => this with
        {
            Mutations = Mutations.Add((context, instance) =>
            {
                if (instance is T t)
                    action.Invoke(context, t);
            })
        };
}

public record TypeFactory(Func<Type, object> Factory, Func<Type, bool> Filter);

