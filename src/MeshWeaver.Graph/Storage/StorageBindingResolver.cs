using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Storage;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Storage;

/// <summary>
/// The mesh's <see cref="IStorageBindingResolver"/>: reads the binding nodes of the partition (and,
/// for an instance-wide purpose, of <c>Admin</c>) LIVE, as the infrastructure identity — storage
/// routing is plumbing, never a user-facing read, and the answer must not depend on who happens to
/// trigger it — and applies the precedence rule (<see cref="Pick"/>).
///
/// <para>Mesh-scoped singleton: the instance's pre-configured stores come from DI
/// (<see cref="IInstanceStore"/>), the bindings from the mesh.</para>
/// </summary>
internal sealed class StorageBindingResolver : IStorageBindingResolver
{
    private readonly IMessageHub hub;

    /// <summary>Creates the resolver.</summary>
    /// <param name="hub">The mesh hub.</param>
    public StorageBindingResolver(IMessageHub hub) => this.hub = hub;

    /// <summary>The partition catalog, live, as System: <c>null</c> while it cannot be read, which
    /// <see cref="Pick"/> treats fail-closed. Per resolution, cold — its subscription is the
    /// resolution's own, so nothing outlives the consumer that asked.</summary>
    private IObservable<ImmutableHashSet<string>?> Partitions()
        => Live($"namespace:{PartitionNodeType.Namespace} nodeType:{PartitionNodeType.NodeType}")
            .Select(nodes => (ImmutableHashSet<string>?)nodes.Select(n => n.Id).ToImmutableHashSet(StringComparer.OrdinalIgnoreCase))
            .Catch((Exception _) => Observable.Return<ImmutableHashSet<string>?>(null));

    private IReadOnlyList<IInstanceStore> Stores => hub.ServiceProvider.GetServices<IInstanceStore>().ToList();

    /// <inheritdoc />
    public IObservable<ResolvedStorage> Resolve(string purpose, string partition, string? collection = null)
    {
        var stores = Stores;
        var own = Bindings(partition);
        var admin = StoragePurpose.FallsBackToAdmin(purpose) && !StorageBindingPaths.IsAdmin(partition)
            ? Bindings(StorageBindingPaths.AdminPartition)
            : Observable.Return(ImmutableList<MeshNode>.Empty);

        return own.CombineLatest(admin, Partitions(),
                (o, a, known) => Pick(hub, stores, purpose, partition, collection, o, a, known))
            .DistinctUntilChanged();
    }

    /// <summary>The binding nodes of <paramref name="partition"/>, live, as System. A failed read
    /// answers "no bindings" (the default) and logs — storage never waits on a broken query.</summary>
    private IObservable<ImmutableList<MeshNode>> Bindings(string partition)
        => Live(StorageBindingPaths.QueryFor(partition))
            .Catch((Exception ex) =>
            {
                hub.ServiceProvider.GetService<ILogger<StorageBindingResolver>>()?.LogWarning(ex,
                    "Storage bindings of {Partition} could not be read — resolving to the instance default", partition);
                return Observable.Return(ImmutableList<MeshNode>.Empty);
            });

    /// <summary>One query's rows, live, as System.</summary>
    private IObservable<ImmutableList<MeshNode>> Live(string query)
    {
        var mesh = hub.ServiceProvider.GetRequiredService<IMeshService>();
        return mesh.Query<MeshNode>(MeshQueryRequest
                .FromQuery(query)
                .AsSystem()
                .Complete())
            .Scan(ImmutableDictionary<string, MeshNode>.Empty, (rows, change) =>
            {
                if (change.ChangeType is QueryChangeType.Initial or QueryChangeType.Reset)
                    rows = ImmutableDictionary<string, MeshNode>.Empty;
                foreach (var item in change.Items)
                    rows = change.ChangeType == QueryChangeType.Removed ? rows.Remove(item.Path) : rows.SetItem(item.Path, item);
                return rows;
            })
            // No seeded empty frame: the FIRST answer must already be the bindings' answer, or a
            // consumer that takes one value at open would always read the default.
            .Select(rows => rows.Values.OrderBy(n => n.Path, StringComparer.Ordinal).ToImmutableList());
    }

    /// <summary>
    /// The precedence rule, pure over the bindings read: the partition's own usable binding (one
    /// naming <paramref name="collection"/> before its default one) → Admin's (only when given) →
    /// the built-in default. A binding is usable only when <see cref="StorageBinding.IsUsable"/> AND
    /// its container passes the FULL ownership rule for the partition that holds it — the prefix AND
    /// "not another partition's default container" against the live partition catalog
    /// (<paramref name="knownPartitions"/>; <c>null</c> = unreadable, fail closed). Re-applied on every
    /// answer, so neither a verdict written onto a node by hand nor a partition created after the
    /// binding was validated can route one partition's data into another's container.
    /// </summary>
    internal static ResolvedStorage Pick(
        IMessageHub hub, IReadOnlyList<IInstanceStore> stores, string purpose, string partition, string? collection,
        IReadOnlyList<MeshNode> own, IReadOnlyList<MeshNode> admin, IReadOnlySet<string>? knownPartitions)
    {
        if (Choose(hub, stores, purpose, partition, collection, own, knownPartitions) is { } local)
            return local;
        if (Choose(hub, stores, purpose, StorageBindingPaths.AdminPartition, collection, admin, knownPartitions) is { } global)
            return global with { Partition = partition };
        return Default(stores, purpose, partition);
    }

    private static ResolvedStorage? Choose(
        IMessageHub hub, IReadOnlyList<IInstanceStore> stores, string purpose, string holder, string? collection,
        IReadOnlyList<MeshNode> nodes, IReadOnlySet<string>? knownPartitions)
    {
        var usable = nodes
            .Select(n => (Node: n, Binding: n.ContentAs<StorageBinding>(hub.JsonSerializerOptions)))
            .Where(x => x.Binding is not null
                        && string.Equals(x.Binding.Purpose, purpose, StringComparison.Ordinal)
                        && x.Binding.IsUsable()
                        && !x.Binding.IsSeparateAccount())
            .Select(x => (x.Node, Binding: x.Binding!, Store: StoreFor(stores, x.Binding!)))
            .Where(x => x.Store is not null
                        && StorageContainerOwnership.MayUse(x.Store, holder, x.Binding.Container, knownPartitions))
            .ToList();

        var chosen = usable.FirstOrDefault(x => !string.IsNullOrEmpty(collection)
                         && string.Equals(x.Binding.Collection, collection, StringComparison.OrdinalIgnoreCase));
        if (chosen.Node is null)
            chosen = usable.FirstOrDefault(x => x.Binding.IsDefault && string.IsNullOrEmpty(x.Binding.Collection));
        if (chosen.Node is null)
            chosen = usable.FirstOrDefault(x => string.IsNullOrEmpty(x.Binding.Collection));
        if (chosen.Node is null)
            return null;
        return new ResolvedStorage(purpose, holder, chosen.Store!.Id, chosen.Store.Kind, chosen.Binding.Container!)
        {
            TablePrefix = string.IsNullOrWhiteSpace(chosen.Binding.TablePrefix) ? null : chosen.Binding.TablePrefix,
            BindingPath = chosen.Node.Path,
        };
    }

    /// <summary>The store a binding names, or the default store for its purpose when it names none.</summary>
    internal static IInstanceStore? StoreFor(IReadOnlyList<IInstanceStore> stores, StorageBinding binding)
        => string.IsNullOrWhiteSpace(binding.StoreId)
            ? DefaultStore(stores, binding.Purpose)
            : stores.FirstOrDefault(s => string.Equals(s.Id, binding.StoreId, StringComparison.OrdinalIgnoreCase));

    /// <summary>The store registered as the default for <paramref name="purpose"/>, if any (first by id).</summary>
    internal static IInstanceStore? DefaultStore(IReadOnlyList<IInstanceStore> stores, string purpose)
        => stores.Where(s => s.DefaultPurposes.Contains(purpose))
            .OrderBy(s => s.Id, StringComparer.Ordinal)
            .FirstOrDefault();

    /// <summary>The built-in default: what the instance does with no binding at all.</summary>
    internal static ResolvedStorage Default(IReadOnlyList<IInstanceStore> stores, string purpose, string partition)
        => DefaultStore(stores, purpose) is { } store
            ? new ResolvedStorage(purpose, partition, store.Id, store.Kind, store.DefaultContainerFor(partition))
            : new ResolvedStorage(purpose, partition, StorageStoreKind.Instance, StorageStoreKind.Instance,
                partition.ToLowerInvariant());
}
