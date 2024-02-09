using OpenSmc.Messaging;

namespace OpenSmc.Data;


public record MessageHubDataSourceBuilder<T>(object Address)
{
    internal object InitializeMessage { get; init; } = new GetManyRequest<T>();
    internal Func<IReadOnlyCollection<T>, object> SaveMessage { get; init; } = entities => new UpdatePersistenceRequest<T>(entities);
    public MessageHubDataSourceBuilder<T> WithSaveMessage(Func<IReadOnlyCollection<T>, object> saveMessage) => this with { SaveMessage = saveMessage, };
    internal Func<IReadOnlyCollection<T>, object> DeleteMessage { get; init; } = entities => new DeleteBatchRequest<T>(entities);
    public MessageHubDataSourceBuilder<T> WithDeleteMessage(Func<IReadOnlyCollection<T>, object> deleteMessage) => this with { DeleteMessage = deleteMessage, };
}