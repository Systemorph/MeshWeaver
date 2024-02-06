using System.Collections.Immutable;

namespace OpenSmc.Serialization;

public record SerializationConfiguration
{
    internal ImmutableList<SerializationTypeRule> Rules = ImmutableList<SerializationTypeRule>.Empty;
    internal ImmutableDictionary<Type, Func<IServiceProvider, object>> TypeFactories = ImmutableDictionary<Type, Func<IServiceProvider, object>>.Empty;

    public SerializationConfiguration ForType<T>(Func<SerializationTypeRule<T>, SerializationTypeRule<T>> configure)
    {
        var rule = new SerializationTypeRule<T>();
        rule = configure(rule);
        return this with { Rules = Rules.Add(rule) };
    }

    internal void Apply(ICustomSerializationRegistry registry)
    {
        foreach (var rule in Rules)
        {
            rule.Apply(registry);
        }
    }

    public SerializationConfiguration WithTypeFactory(Type type, Func<IServiceProvider, object> factory)
    {
        return this with { TypeFactories = TypeFactories.SetItem(type, factory) };
    }
}

public abstract record SerializationTypeRule
{
    internal abstract void Apply(ICustomSerializationRegistry registry);
}

public record SerializationTypeRule<T> : SerializationTypeRule
{
    internal SerializationTransform<T> Transformation { get; init; }
    internal SerializationMutate<T> Mutation { get; init; }

    public SerializationTypeRule<T> WithTransformation(SerializationTransform<T> transform) =>
        this with { Transformation = transform };

    public SerializationTypeRule<T> WithMutation(SerializationMutate<T> mutation) =>
        this with { Mutation = mutation };

    internal override void Apply(ICustomSerializationRegistry registry)
    {
        if (Transformation != null)
            registry.RegisterTransformation(Transformation);
        
        if (Mutation != null)
            registry.RegisterMutation(Mutation);
    }
}