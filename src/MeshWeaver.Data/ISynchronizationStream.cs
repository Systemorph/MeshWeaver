using MeshWeaver.Data.Serialization;
using MeshWeaver.Messaging;

namespace MeshWeaver.Data;

public interface ISynchronizationStream : IDisposable
{
    Address Owner { get; }
    object Reference { get; }
    string StreamId { get; }
    string ClientId { get; }

    /// <summary>
    /// The identity (mesh node) that owns this stream.
    /// For user-facing streams, this is the user ID.
    /// For hub-to-hub streams, this is the hub address.
    /// </summary>
    string? Identity { get; }

    StreamIdentity StreamIdentity { get; }
    internal IMessageDelivery DeliverMessage(IMessageDelivery delivery);
    void RegisterForDisposal(IDisposable disposable);

    ISynchronizationStream Reduce(
        WorkspaceReference reference) => Reduce((dynamic)reference);
    ISynchronizationStream<TReduced>? Reduce<TReduced>(
        WorkspaceReference<TReduced> reference);

    ISynchronizationStream<TReduced>? Reduce<TReduced>(
        WorkspaceReference<TReduced> reference,
        Func<StreamConfiguration<TReduced>, StreamConfiguration<TReduced>>? config
    );

    IMessageHub Hub { get; }
    IMessageHub Host { get; }
    T? Get<T>(string key);
    T? Get<T>();
    void Set<T>(string key, T? value);
    void Set<T>(T? value);


}

public interface ISynchronizationStream<TStream>
    : ISynchronizationStream,
        IObservable<ChangeItem<TStream>>,
        IObserver<ChangeItem<TStream>>
{
    /// <summary>
    /// Snapshot of the most recent ChangeItem this stream has observed.
    /// <para>
    /// <b>Anti-pattern for application code.</b> Sync reads on cold workspaces / remote
    /// streams that have not completed <c>SubscribeRequest</c> return <c>null</c> and
    /// silently ship wrong answers. Subscribe to the stream reactively or use
    /// <c>workspace.GetMeshNodeStream(...)</c> / <c>workspace.GetRemoteStream(...)</c>.
    /// See <c>Doc/Architecture/AsynchronousCalls.md</c>.
    /// </para>
    /// <para>
    /// TODO: target = internal once Blazor sync-render callsites (DataGridView columns,
    /// editor views, MeshNodePickerView) and <c>LayoutAreaHost.GetControl</c> have been
    /// migrated to maintain their own cached snapshot via long-lived subscription
    /// (the <c>CollaborativeMarkdownView</c> pattern).
    /// </para>
    /// </summary>
    ChangeItem<TStream>? Current { get; }
    void Update(Func<TStream?, ChangeItem<TStream>?> update, Func<Exception, Task> exceptionCallback);
    void Update(Func<TStream?, ChangeItem<TStream>?> update) => Update(update, _ => Task.CompletedTask);
    void Update(Func<TStream?, CancellationToken, Task<ChangeItem<TStream>?>> update, Func<Exception, Task> exceptionCallback);
    ReduceManager<TStream> ReduceManager { get; }

}


public enum InitializationMode
{
    Automatic,
    Manual
}
