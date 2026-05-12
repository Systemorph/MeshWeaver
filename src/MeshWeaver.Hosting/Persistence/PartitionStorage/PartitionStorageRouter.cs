using System.Collections.Concurrent;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;

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
/// <para>Hubs spawn lazily on first request and dispose themselves after
/// ~5 minutes idle. The router holds no long-lived state per partition —
/// the dictionary entry is a queue+timer, nothing else. See
/// <c>Doc/Architecture/PartitionStorageHubs.md</c> for the full design.</para>
/// </summary>
public sealed class PartitionStorageRouter : IDisposable
{
    private readonly IMessageHub _mesh;
    private readonly IReadOnlyList<IPartitionStorageProvider> _providers;
    private readonly ConcurrentDictionary<(string Schema, string Table), HubEntry> _hubs =
        new(KeyComparer.Instance);
    private static readonly TimeSpan IdleTtl = TimeSpan.FromMinutes(5);
    private bool _disposed;

    /// <summary>
    /// Constructs a router over the registered <see cref="IPartitionStorageProvider"/>s,
    /// using <paramref name="mesh"/> as the parent hub for hosted partition hubs.
    /// </summary>
    public PartitionStorageRouter(
        IMessageHub mesh,
        IEnumerable<IPartitionStorageProvider> providers)
    {
        _mesh = mesh;
        _providers = providers.ToList();
    }

    /// <summary>
    /// Resolves <paramref name="path"/> to the partition-hub address that
    /// owns the table the node lives in. Spawns the hub lazily on first
    /// access and resets the idle timer on every call. Returns null when no
    /// provider claims the path.
    /// </summary>
    public Address? AddressFor(string path)
    {
        if (_disposed) return null;
        foreach (var provider in _providers)
        {
            if (!provider.Matches(path)) continue;
            var def = provider.ResolveDefinition(path);
            if (def == null) continue;
            var schema = !string.IsNullOrEmpty(def.Schema) ? def.Schema : def.Namespace;
            if (string.IsNullOrEmpty(schema)) continue;
            var table = def.ResolveTable(path);
            var key = (Schema: schema, Table: table);
            var entry = _hubs.GetOrAdd(key, k => SpawnHub(k, provider, def));
            entry.ResetIdleTimer();
            return entry.Hub.Address;
        }
        return null;
    }

    private HubEntry SpawnHub(
        (string Schema, string Table) key,
        IPartitionStorageProvider provider,
        PartitionDefinition def)
    {
        var adapter = provider.CreateAdapterForTable(def, key.Table);
        var address = new Address($"storage/{key.Schema}/{key.Table}");
        var hub = _mesh.GetHostedHub(
            address,
            config => config.AddPartitionStorageHandlers(adapter));
        return new HubEntry(this, key, hub);
    }

    private void OnIdle((string Schema, string Table) key)
    {
        if (_disposed) return;
        if (_hubs.TryRemove(key, out var entry))
            entry.DisposeOnIdle();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var entry in _hubs.Values) entry.DisposeOnShutdown();
        _hubs.Clear();
    }

    private sealed class HubEntry
    {
        private readonly PartitionStorageRouter _router;
        private readonly (string Schema, string Table) _key;
        public IMessageHub Hub { get; }
        private readonly Timer _idleTimer;

        public HubEntry(
            PartitionStorageRouter router,
            (string Schema, string Table) key,
            IMessageHub hub)
        {
            _router = router;
            _key = key;
            Hub = hub;
            _idleTimer = new Timer(_ => _router.OnIdle(_key), null, IdleTtl, Timeout.InfiniteTimeSpan);
        }

        public void ResetIdleTimer()
            => _idleTimer.Change(IdleTtl, Timeout.InfiniteTimeSpan);

        public void DisposeOnIdle()
        {
            _idleTimer.Dispose();
            Hub.Dispose();
        }

        public void DisposeOnShutdown()
        {
            _idleTimer.Dispose();
            Hub.Dispose();
        }
    }

    private sealed class KeyComparer : IEqualityComparer<(string Schema, string Table)>
    {
        public static readonly KeyComparer Instance = new();

        public bool Equals((string Schema, string Table) x, (string Schema, string Table) y)
            => string.Equals(x.Schema, y.Schema, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Table, y.Table, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Schema, string Table) obj)
            => HashCode.Combine(
                obj.Schema?.ToLowerInvariant(),
                obj.Table?.ToLowerInvariant());
    }
}
