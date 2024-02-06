using OpenSmc.Collections;
using OpenSmc.DataSource.Abstractions;
using OpenSmc.Messaging;
using System.Collections.Immutable;

namespace OpenSmc.DataPlugin;

public record DataPersistenceConfiguration(IMessageHub Hub)
{
    internal ImmutableList<TypeConfiguration> TypeConfigurations { get; private init; } = ImmutableList<TypeConfiguration>.Empty;

    public DataPersistenceConfiguration WithType<T>(
        Func<Task<IReadOnlyCollection<T>>> initialize,
        Func<IReadOnlyCollection<T>, Task> save,
        Func<IReadOnlyCollection<T>, Task> delete)
        => this with { TypeConfigurations = TypeConfigurations.Add(new TypeConfiguration<T>(initialize, save, delete)) };

    public DataPersistenceConfiguration WithType<T>(IDataSource dataSource) =>
        WithType<T>(async () => await dataSource.Query<T>().ToArrayAsync(), items => dataSource.UpdateAsync(items), ids => dataSource.DeleteAsync(ids));

    // Message variant of lambda
    // public DataPersistenceConfiguration WithType<T>(object address)
    //     => WithType(() => Hub.RegisterCallback())
}

public record MessageHubDataSourceBuilder<T>(object Address)
{
    internal object InitializeMessage { get; init; } = new GetManyRequest<T>();
    internal Func<IReadOnlyCollection<T>, object> SaveMessage { get; init; } = entities => new UpdatePersistenceRequest<T>(entities);
    public MessageHubDataSourceBuilder<T> WithSaveMessage(Func<IReadOnlyCollection<T>, object> saveMessage) => this with { SaveMessage = saveMessage, };
    internal Func<IReadOnlyCollection<T>, object> DeleteMessage { get; init; } = entities => new DeleteBatchRequest<T>(entities);
    public MessageHubDataSourceBuilder<T> WithDeleteMessage(Func<IReadOnlyCollection<T>, object> deleteMessage) => this with { DeleteMessage = deleteMessage, };
}