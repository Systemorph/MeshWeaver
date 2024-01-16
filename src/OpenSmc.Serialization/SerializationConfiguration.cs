using System.Collections.Immutable;

namespace OpenSmc.Serialization;

public record SerializationConfiguration
{
    internal ImmutableList<SerializationTypeRule> Rules = ImmutableList<SerializationTypeRule>.Empty;

    public SerializationConfiguration ForType<T>(Func<SerializationTypeRule, SerializationTypeRule> configure)
    {
        SerializationTypeRule rule = new SerializationTypeRule<T>();
        rule = configure(rule);
        return this with { Rules = Rules.Add(rule) };
    }
}

public abstract record SerializationTypeRule
{
    internal SerializationTransform Transformation { get; init; }
    internal SerializationMutate Mutation { get; init; }

    public SerializationTypeRule WithTransformation(SerializationTransform transform) =>
        this with { Transformation = transform };

    public SerializationTypeRule WithMutation(SerializationMutate mutation) =>
        this with { Mutation = mutation };

    public abstract void Apply(ICustomSerializationRegistry registry);
}

internal record SerializationTypeRule<T> : SerializationTypeRule
{
    public override void Apply(ICustomSerializationRegistry registry)
    {
        if (Transformation != null)
            registry.RegisterTransformation<T>(Transformation);
        
        if (Mutation != null)
            registry.RegisterMutation<T>(Mutation);
    }
}