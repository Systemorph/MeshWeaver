using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Caching.Memory;

namespace MeshWeaver.Hosting.Persistence.PartitionStorage;

/// <summary>
/// Singleton silo-wide registry that maps node paths to <c>(schema, table)</c>
/// partition-storage hubs. <b>Not a hub itself</b> — pure routing table.
///
/// <para>A caller hub resolves an <see cref="Address"/> via
/// <see cref="AddressFor(string)"/> and then posts directly to that address
/// via <c>hub.Observe(req, o =&gt; o.WithTarget(addr))</c>. No intermediate
/// routing hub is on the message path; the caller hub talks straight to the
/// partition hub that owns the I/O for the target table.</para>
///
/// <para><b>Observable resolution.</b> <see cref="AddressFor(string)"/>
/// returns <c>IObservable&lt;Address?&gt;</c> because partition existence is
/// observable: the per-provider <see cref="System.Reactive.Subjects.ReplaySubject{T}"/>
/// re-emits when the catalog changes (e.g. organization creation). Callers
/// compose via <c>SelectMany</c> — no blocking, no stale cached <c>null</c>.</para>
///
/// <para>Hubs live in <see cref="IMemoryCache"/> with a 5-minute sliding
/// expiration — they spawn on first request and dispose themselves when the
/// cache evicts them. The router holds no long-lived state per partition
/// beyond the cache entry. See <c>Doc/Architecture/PartitionStorageHubs.md</c>
/// for the full design.</para>
/// </summary>
public sealed class PartitionStorageRouter : IDisposable
{
    private readonly IMessageHub _mesh;
    private readonly IReadOnlyList<IPartitionStorageProvider> _providers;
    private readonly IMemoryCache _hubs;
    private static readonly TimeSpan IdleTtl = TimeSpan.FromMinutes(5);
    private bool _disposed;

    /// <summary>
    /// Constructs a router over the registered <see cref="IPartitionStorageProvider"/>s,
    /// using <paramref name="mesh"/> as the parent hub for hosted partition hubs.
    /// The <paramref name="cache"/> (typically a dedicated <see cref="IMemoryCache"/>
    /// for this router) holds spawned hubs with a 5-minute sliding expiration.
    /// </summary>
    public PartitionStorageRouter(
        IMessageHub mesh,
        IEnumerable<IPartitionStorageProvider> providers,
        IMemoryCache cache)
    {
        _mesh = mesh;
        _providers = providers.ToList();
        _hubs = cache;
    }

    /// <summary>
    /// Resolves <paramref name="path"/> to the partition-hub address that
    /// owns the table the node lives in. Spawns the hub lazily on first
    /// access and resets the sliding-expiration timer on every call.
    /// Emits null when no provider claims the path.
    /// <para>Routing iterates providers in registration order; the first
    /// whose adapter claims the path (rather than short-circuiting with
    /// null) wins. Per-provider <c>Take(1)</c> bounds each subscription so a
    /// silent subject can't strand the resolution.</para>
    /// </summary>
    public IObservable<Address?> AddressFor(string path)
    {
        if (_disposed) return Observable.Return<Address?>(null);
        if (string.IsNullOrEmpty(path)) return Observable.Return<Address?>(null);

        // Stage 1 stub: pick the first writable provider; build a synthesized
        // PartitionDefinition keyed on the path's first segment. PartitionStorageRouter
        // is currently dead code on the main message path (RoutingProxyAdapter is the
        // proxy and its partition-object surface is stubbed); will be revisited or
        // removed in a follow-up alongside the try-then-claim refactor.
        var firstSegment = GetFirstSegment(path);
        if (string.IsNullOrEmpty(firstSegment)) return Observable.Return<Address?>(null);

        foreach (var provider in _providers)
        {
            if (provider.IsReadOnly) continue;
            var def = provider.PartitionDefinition ?? new PartitionDefinition
            {
                Namespace = firstSegment,
                DataSource = provider.Name,
                Schema = firstSegment.ToLowerInvariant(),
                Table = "mesh_nodes",
                TableMappings = PartitionDefinition.DefaultSegmentTableMappings(), NodeTypeTableMappings = PartitionDefinition.DefaultNodeTypeTableMappings(),
                Versioned = false,
            };
            return Observable.Return<Address?>(SpawnOrReuse(path, provider, def));
        }
        return Observable.Return<Address?>(null);
    }

    private static string? GetFirstSegment(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var normalized = path.Trim('/');
        if (normalized.Length == 0) return null;
        var slash = normalized.IndexOf('/');
        return slash < 0 ? normalized : normalized[..slash];
    }

    private Address? SpawnOrReuse(string path, IPartitionStorageProvider provider, PartitionDefinition def)
    {
        var schema = !string.IsNullOrEmpty(def.Schema) ? def.Schema : def.Namespace;
        if (string.IsNullOrEmpty(schema)) return null;
        var table = def.ResolveTable(path);
        var cacheKey = $"storage/{schema}/{table}".ToLowerInvariant();

        var hub = _hubs.GetOrCreate(cacheKey, entry =>
        {
            entry.SlidingExpiration = IdleTtl;
            var spawned = SpawnHub(schema!, table, provider, def);
            entry.RegisterPostEvictionCallback(static (_, value, _, _) =>
            {
                if (value is IMessageHub h)
                {
                    try { h.Dispose(); }
                    catch { /* swallow — best-effort */ }
                }
            });
            return spawned;
        })!;

        return hub.Address;
    }

    private IMessageHub SpawnHub(
        string schema,
        string table,
        IPartitionStorageProvider provider,
        PartitionDefinition def)
    {
        var adapter = provider.CreateAdapterForTable(def, table);
        var address = new Address($"storage/{schema}/{table}");
        return _mesh.GetHostedHub(
            address,
            config => config.AddPartitionStorageHandlers(adapter));
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // IMemoryCache disposal cascades to PostEviction callbacks, which
        // dispose each live partition hub.
    }
}
