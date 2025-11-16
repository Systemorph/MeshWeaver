using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Domain;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Data;

/// <summary>
/// A data source that computes its data from a virtual stream rather than storing it directly.
/// This is useful for derived data, aggregations, or transformations of existing data sources.
/// </summary>
public record VirtualDataSource(object Id, IWorkspace Workspace)
    : TypeSourceBasedUnpartitionedDataSource<VirtualDataSource, VirtualTypeSource>(Id, Workspace)
{
    public override VirtualDataSource WithType<T>(Func<ITypeSource, ITypeSource>? config)
    {
        throw new NotSupportedException("VirtualDataSource does not support WithType. Use WithVirtualType instead.");
    }

    /// <summary>
    /// Adds a virtual type to this data source with a stream provider function.
    /// </summary>
    /// <typeparam name="T">The type to add</typeparam>
    /// <param name="streamProvider">Function that receives the workspace and returns an observable stream of instances</param>
    /// <param name="collectionName">Optional collection name (defaults to type name)</param>
    /// <returns>Updated data source</returns>
    public VirtualDataSource WithVirtualType<T>(
        Func<IWorkspace, IObservable<IEnumerable<T>>> streamProvider,
        string? collectionName = null
    ) where T : class
    {
        var typeSource = new VirtualTypeSource<T>(
            Workspace,
            Id,
            streamProvider,
            collectionName
        );
        return WithTypeSource(typeof(T), typeSource);
    }
}

/// <summary>
/// Base class for virtual type sources
/// </summary>
public abstract record VirtualTypeSource : TypeSource<VirtualTypeSource>
{
    protected VirtualTypeSource(IWorkspace workspace, Type type) : base(workspace, type)
    {
    }

    public abstract IObservable<IEnumerable<object>> GetStreamUpdates();
}

/// <summary>
/// Type source for virtual data that is computed from a stream
/// </summary>
public record VirtualTypeSource<T>(
    IWorkspace Workspace,
    object DataSourceId,
    Func<IWorkspace, IObservable<IEnumerable<T>>> StreamProvider,
    string? CollectionName = null
) : VirtualTypeSource(Workspace, typeof(T)) where T : class
{
    private IObservable<IEnumerable<T>>? cachedStream;

    public IObservable<IEnumerable<T>> StreamUpdates()
    {
        return cachedStream ??= StreamProvider(Workspace)
            .DistinctUntilChanged()
            .Replay(1)
            .RefCount();
    }

    public override IObservable<IEnumerable<object>> GetStreamUpdates()
    {
        return StreamUpdates().Select(items => items.Cast<object>());
    }

    protected override async Task<InstanceCollection> InitializeAsync(
        WorkspaceReference<InstanceCollection> reference,
        CancellationToken cancellationToken
    )
    {
        var instances = await StreamUpdates()
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(30))
            .ToTask(cancellationToken);

        return new InstanceCollection(instances.Cast<object>(), TypeDefinition.GetKey);
    }
}
