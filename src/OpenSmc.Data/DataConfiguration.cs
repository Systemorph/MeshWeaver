using OpenSmc.DataSource.Abstractions;
using OpenSmc.Messaging;
using System.Collections.Immutable;
using OpenSmc.Collections;

namespace OpenSmc.Data;

public record DataConfiguration
{
    internal ImmutableDictionary<Type, TypeConfiguration> TypeConfigurations { get; init; } = ImmutableDictionary<Type, TypeConfiguration>.Empty;

    public DataConfiguration WithType<T>(
        Func<TypeConfiguration<T>, TypeConfiguration<T>> typeConfigurator)
        => this with { TypeConfigurations = TypeConfigurations.SetItem(typeof(T), typeConfigurator.Invoke(new TypeConfiguration<T>())) };


    public DataConfiguration WithType<T>(Func<T, object> key, IDataSource dataSource) =>
        WithType<T>(o => o.WithKey(key)
            .WithInitialization(async () => await dataSource.Query<T>().ToArrayAsync())
            .WithSave(async entities => await dataSource.UpdateAsync(entities))
            .WithDelete(async ids => await dataSource.DeleteAsync(ids))
        );


}