using OpenSmc.Collections;
using OpenSmc.DataSource.Abstractions;
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

    public DataPluginConfiguration WithType<T>(IDataSource dataSource) =>
        this.WithType<T>(async () => await dataSource.Query<T>().ToArrayAsync(), items => dataSource.UpdateAsync(items), ids => dataSource.DeleteAsync(ids));
}