using System.Collections.Immutable;

namespace OpenSmc.DataPlugin;

public record DataPluginConfiguration
{
    internal ImmutableList<TypeConfiguration> TypeConfigurations { get; private set; }

    public DataPluginConfiguration WithType<T>(
        Func<Task<IReadOnlyCollection<T>>> initialize,
        Func<IReadOnlyCollection<T>, Task> save,
        Func<IReadOnlyCollection<object>, Task> delete)
        => this with { TypeConfigurations = TypeConfigurations.Add(new TypeConfiguration<T>(initialize, save, delete)) };
}