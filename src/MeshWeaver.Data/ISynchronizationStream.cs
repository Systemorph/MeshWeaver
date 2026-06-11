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
    /// <summary>
    /// Updates the stream by composing the provided <paramref name="update"/> on top of
    /// the current value and posting the result. Failures (validation, persistence, hub
    /// disposal) flow through <paramref name="exceptionCallback"/>.
    ///
    /// <para><paramref name="exceptionCallback"/> is <see cref="Action{T}"/> rather than
    /// <see cref="Func{T, TResult}"/> with a Task return so callers can't await it. An
    /// awaited Task on a hub-touching error path deadlocks when the awaited thread is
    /// the one that would publish the result — see Doc/Architecture/AsynchronousCalls.md.
    /// Side effects only: log, push to a status subject, etc.</para>
    /// </summary>
    void Update(Func<TStream?, ChangeItem<TStream>?> update, Action<Exception> exceptionCallback);
    void Update(Func<TStream?, ChangeItem<TStream>?> update) => Update(update, _ => { });

    /// <summary>
    /// 🚨 Canonical VALUE-based write. The caller supplies only a pure value
    /// transform; the stream builds the <see cref="ChangeItem{TStream}"/> itself
    /// (per-entity Updates, ChangeType, and the OWNER-ONLY Version). This is the
    /// path callers should use — it removes the error-prone job of hand-building a
    /// ChangeItem (a malformed EntityUpdate silently fails the owner's write-back,
    /// and a non-owner stamping its own Version breaks ordering). A no-op transform
    /// (same value / null) is dropped.
    /// </summary>
    void Update(Func<TStream?, TStream?> valueUpdate, Action<Exception> exceptionCallback);
    void Update(Func<TStream?, TStream?> valueUpdate) => Update(valueUpdate, _ => { });

    /// <summary>
    /// 🚨 Full-replace write (OVERWRITE). Like <see cref="Update(Func{TStream,TStream},Action{Exception})"/>
    /// but emits the change as <see cref="ChangeType.Full"/> — the complete authoritative state —
    /// instead of a field-level Patch. A Full lands on every mirror UNCONDITIONALLY (the
    /// monotonicity guard lets Fulls through regardless of version) and persists via the owner's
    /// write-back. Use it to assert truth decoupled from the merge protocol (static-repo import,
    /// rollback), NOT for ordinary field edits — those stay <see cref="Update(Func{TStream,TStream},Action{Exception})"/>.
    /// </summary>
    void SetFull(Func<TStream?, TStream?> valueUpdate, Action<Exception> exceptionCallback);
    void SetFull(Func<TStream?, TStream?> valueUpdate) => SetFull(valueUpdate, _ => { });
    ReduceManager<TStream> ReduceManager { get; }

}


public enum InitializationMode
{
    Automatic,
    Manual
}
