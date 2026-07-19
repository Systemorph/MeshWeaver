using MeshWeaver.Data.Serialization;
using MeshWeaver.Messaging;

namespace MeshWeaver.Data;

/// <summary>
/// A live, reducible view over a piece of workspace state. Carries identity (owner, stream id,
/// client id, reference) and supports reducing to narrower references and stashing per-stream
/// key/value state. The generic <see cref="ISynchronizationStream{TStream}"/> adds typed read/write.
/// </summary>
public interface ISynchronizationStream : IDisposable
{
    /// <summary>Address of the hub that owns the underlying state.</summary>
    Address Owner { get; }
    /// <summary>The workspace reference this stream represents.</summary>
    object Reference { get; }
    /// <summary>Stable identifier of this stream.</summary>
    string StreamId { get; }
    /// <summary>Identifier of the client/subscriber this stream instance serves.</summary>
    string ClientId { get; }

    /// <summary>
    /// The identity (mesh node) that owns this stream.
    /// For user-facing streams, this is the user ID.
    /// For hub-to-hub streams, this is the hub address.
    /// </summary>
    string? Identity { get; }

    /// <summary>Combined owner address and partition that identifies this stream.</summary>
    StreamIdentity StreamIdentity { get; }
    internal IMessageDelivery DeliverMessage(IMessageDelivery delivery);
    /// <summary>Registers a resource to be disposed together with this stream.</summary>
    /// <param name="disposable">Resource to dispose with the stream.</param>
    void RegisterForDisposal(IDisposable disposable);

    /// <summary>Reduces this stream to the narrower view described by <paramref name="reference"/>.</summary>
    /// <param name="reference">Reference describing the reduced view.</param>
    /// <returns>A stream over the reduced state.</returns>
    ISynchronizationStream Reduce(
        WorkspaceReference reference) => Reduce((dynamic)reference);
    /// <summary>Reduces this stream to a typed view described by <paramref name="reference"/>.</summary>
    /// <typeparam name="TReduced">The reduced state type.</typeparam>
    /// <param name="reference">Reference describing the reduced view.</param>
    /// <returns>A typed reduced stream, or null if the reference cannot be reduced.</returns>
    ISynchronizationStream<TReduced>? Reduce<TReduced>(
        WorkspaceReference<TReduced> reference);

    /// <summary>Reduces this stream to a typed view, applying optional stream configuration.</summary>
    /// <typeparam name="TReduced">The reduced state type.</typeparam>
    /// <param name="reference">Reference describing the reduced view.</param>
    /// <param name="config">Optional configuration for the reduced stream.</param>
    /// <returns>A typed reduced stream, or null if the reference cannot be reduced.</returns>
    ISynchronizationStream<TReduced>? Reduce<TReduced>(
        WorkspaceReference<TReduced> reference,
        Func<StreamConfiguration<TReduced>, StreamConfiguration<TReduced>>? config
    );

    /// <summary>The message hub associated with this stream.</summary>
    IMessageHub Hub { get; }
    /// <summary>The hub that hosts the underlying data source backing this stream.</summary>
    IMessageHub Host { get; }
    /// <summary>Reads a per-stream value previously stashed under <paramref name="key"/>.</summary>
    /// <typeparam name="T">Expected value type.</typeparam>
    /// <param name="key">Key the value was stored under.</param>
    /// <returns>The stored value, or default if none.</returns>
    T? Get<T>(string key);
    /// <summary>Reads a per-stream value stashed under the type name of <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">Type whose name is the key and the expected value type.</typeparam>
    /// <returns>The stored value, or default if none.</returns>
    T? Get<T>();
    /// <summary>Stashes a per-stream value under <paramref name="key"/>.</summary>
    /// <typeparam name="T">Value type.</typeparam>
    /// <param name="key">Key to store under.</param>
    /// <param name="value">Value to store.</param>
    void Set<T>(string key, T? value);
    /// <summary>Stashes a per-stream value under the type name of <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">Type whose name is the key.</typeparam>
    /// <param name="value">Value to store.</param>
    void Set<T>(T? value);


}

/// <summary>
/// A typed synchronization stream that is both an observable and an observer of
/// <see cref="ChangeItem{TStream}"/>, exposing value- and change-based writes and the snapshot of
/// its current state.
/// </summary>
/// <typeparam name="TStream">The state type carried by the stream.</typeparam>
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
    /// <summary>Change-based write whose failures are ignored; see the overload taking an exception callback.</summary>
    /// <param name="update">Builds the change item from the current value, or returns null for a no-op.</param>
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
    /// <summary>Value-based write whose failures are ignored; see the overload taking an exception callback.</summary>
    /// <param name="valueUpdate">Pure transform of the current value; a no-op (same value or null) is dropped.</param>
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
    /// <summary>Full-replace write whose failures are ignored; see the overload taking an exception callback.</summary>
    /// <param name="valueUpdate">Pure transform producing the complete authoritative state.</param>
    void SetFull(Func<TStream?, TStream?> valueUpdate) => SetFull(valueUpdate, _ => { });
    /// <summary>The reduce manager used to reduce this stream to narrower references.</summary>
    ReduceManager<TStream> ReduceManager { get; }

}


/// <summary>Controls when a synchronization stream performs its initial load.</summary>
public enum InitializationMode
{
    /// <summary>The stream initializes itself automatically on subscription.</summary>
    Automatic,
    /// <summary>Initialization is triggered explicitly by the caller.</summary>
    Manual
}
