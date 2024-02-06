namespace OpenSmc.Serialization
{
    public record TypeFactoryProvider(IReadOnlyDictionary<Type, Func<IServiceProvider, object>> TypeFactories);
}
