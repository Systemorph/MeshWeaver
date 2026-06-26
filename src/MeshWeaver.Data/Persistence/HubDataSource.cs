using System.Text.Json;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Messaging;

namespace MeshWeaver.Data.Persistence;


/// <summary>
/// An unpartitioned data source backed by a remote message hub: it mirrors the entity store of
/// the hub at <paramref name="Address"/> by opening a synchronization stream to it.
/// </summary>
/// <param name="Address">The address of the remote hub that owns the data.</param>
/// <param name="Workspace">The local workspace this data source belongs to.</param>
public record UnpartitionedHubDataSource(Address Address, IWorkspace Workspace) : UnpartitionedDataSource<UnpartitionedHubDataSource, ITypeSource>(Address, Workspace)
{
    /// <summary>
    /// The JSON serialization options of the owning hub, used to serialize entities exchanged with the remote hub.
    /// </summary>
    protected JsonSerializerOptions Options => Hub.JsonSerializerOptions;
    /// <summary>
    /// Registers type <typeparamref name="T"/> with this data source.
    /// </summary>
    /// <typeparam name="T">The entity type to register.</typeparam>
    /// <param name="typeSource">Optional configuration applied to the type source; the identity transform is used when null.</param>
    /// <returns>This data source with the type registered.</returns>
    public override UnpartitionedHubDataSource WithType<T>(Func<ITypeSource, ITypeSource>? typeSource) =>
        WithType<T>(x => (TypeSourceWithType<T>)(typeSource ?? (y => y)).Invoke(x));

    /// <summary>
    /// Registers type <typeparamref name="T"/> with this data source using a strongly typed type-source configuration.
    /// </summary>
    /// <typeparam name="T">The entity type to register.</typeparam>
    /// <param name="typeSource">Configuration applied to the <see cref="TypeSourceWithType{T}"/>.</param>
    /// <returns>This data source with the type registered.</returns>
    public UnpartitionedHubDataSource WithType<T>(
        Func<TypeSourceWithType<T>, TypeSourceWithType<T>> typeSource
    ) => WithTypeSource(typeof(T), typeSource.Invoke(new TypeSourceWithType<T>(Workspace, Id)));

    /// <summary>
    /// Creates the synchronization stream for the given identity by opening a remote stream to the hub.
    /// </summary>
    /// <param name="identity">The identity of the stream to create.</param>
    /// <returns>The synchronization stream bound to the remote hub.</returns>
    protected override ISynchronizationStream<EntityStore> CreateStream(StreamIdentity identity) =>
        CreateStream(identity, x => x);

    /// <summary>
    /// Creates the synchronization stream for the given identity by opening a remote stream to the hub.
    /// </summary>
    /// <param name="identity">The identity of the stream to create.</param>
    /// <param name="config">Stream configuration transform (unused for hub-backed streams, which delegate to the remote hub).</param>
    /// <returns>The synchronization stream bound to the remote hub.</returns>
    protected override ISynchronizationStream<EntityStore> CreateStream(StreamIdentity identity, Func<StreamConfiguration<EntityStore>, StreamConfiguration<EntityStore>> config) =>
        Workspace.GetRemoteStreamAsHub(Address, GetReference());

    /// <summary>
    /// Initializes the data source and eagerly opens the remote stream for its reference.
    /// </summary>
    public override void Initialize()
    {
        base.Initialize();
        GetStream(GetReference());
    }
}
