using System.Collections.Concurrent;
using System.Collections.Immutable;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Npgsql;

namespace MeshWeaver.Hosting.PostgreSql;

/// <summary>
/// <see cref="IPartitionStorageProvider"/> implementation backing Postgres
/// partitions. Replaces the legacy <see cref="PostgreSqlPartitionedStoreFactory"/>
/// shared-pool model with per-(schema, table) adapters that own a tiny
/// <see cref="NpgsqlDataSource"/> with <c>MaxPoolSize=1</c>.
///
/// <para>The partition dictionary is seeded with known
/// <see cref="PartitionDefinition"/>s at construction (e.g. from the mesh
/// builder's <c>WithMeshNodes</c> static seed). A live <c>ObserveQuery</c>
/// subscription can be wired in later to react to <c>Admin/Partition/*</c>
/// node changes at runtime.</para>
///
/// <para>See <c>Doc/Architecture/PartitionStorageHubs.md</c>.</para>
/// </summary>
public sealed class PostgreSqlPartitionStorageProvider : IPartitionStorageProvider
{
    private readonly NpgsqlDataSource _baseDataSource;
    private readonly string _baseConnectionString;
    private readonly PostgreSqlStorageOptions _options;
    private readonly IEmbeddingProvider? _embeddingProvider;
    private readonly Action<NpgsqlDataSourceBuilder>? _configureDataSource;
    private readonly ConcurrentDictionary<string, PartitionDefinition> _partitions =
        new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public string Name => "Postgres";

    /// <inheritdoc/>
    public ImmutableHashSet<string> Contexts { get; }

    /// <summary>
    /// Constructs a provider over <paramref name="baseDataSource"/> /
    /// <paramref name="baseConnectionString"/>. <paramref name="partitions"/>
    /// seeds the per-namespace dictionary at boot. The standard mesh-builder
    /// extension also wires an <c>ObserveQuery</c> subscription so partitions
    /// added at runtime (e.g. organization creation) become routable without
    /// a restart.
    /// </summary>
    public PostgreSqlPartitionStorageProvider(
        NpgsqlDataSource baseDataSource,
        string baseConnectionString,
        PostgreSqlStorageOptions options,
        IEnumerable<PartitionDefinition>? partitions = null,
        IEmbeddingProvider? embeddingProvider = null,
        Action<NpgsqlDataSourceBuilder>? configureDataSource = null,
        IEnumerable<string>? contexts = null)
    {
        _baseDataSource = baseDataSource;
        _baseConnectionString = baseConnectionString;
        _options = options;
        _embeddingProvider = embeddingProvider;
        _configureDataSource = configureDataSource;

        Contexts = contexts != null
            ? ImmutableHashSet.CreateRange(StringComparer.OrdinalIgnoreCase, contexts)
            : ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase,
                PartitionContexts.Search,
                PartitionContexts.Create,
                PartitionContexts.Autocomplete,
                PartitionContexts.Browse);

        if (partitions != null)
            foreach (var def in partitions)
                RegisterPartition(def);
    }

    /// <summary>
    /// Adds or updates a partition definition. Idempotent — registering a
    /// namespace twice with different definitions replaces the cached
    /// entry. Called at boot by the mesh-builder seed and at runtime by the
    /// <c>Admin/Partition</c> live-query subscription.
    /// </summary>
    public void RegisterPartition(PartitionDefinition def)
    {
        if (string.IsNullOrEmpty(def.Namespace)) return;
        _partitions[def.Namespace] = def;
    }

    /// <summary>
    /// Removes a partition definition (e.g. organization deletion). Existing
    /// partition hubs will idle-expire from the router's 5-minute cache.
    /// </summary>
    public void RemovePartition(string @namespace)
    {
        if (string.IsNullOrEmpty(@namespace)) return;
        _partitions.TryRemove(@namespace, out _);
    }

    /// <inheritdoc/>
    public bool Matches(string fullPath)
    {
        var firstSegment = GetFirstSegment(fullPath);
        return firstSegment != null && _partitions.ContainsKey(firstSegment);
    }

    /// <inheritdoc/>
    public PartitionDefinition? ResolveDefinition(string fullPath)
    {
        var firstSegment = GetFirstSegment(fullPath);
        if (firstSegment == null) return null;
        _partitions.TryGetValue(firstSegment, out var def);
        return def;
    }

    /// <inheritdoc/>
    public IStorageAdapter CreateAdapterForTable(PartitionDefinition def, string table)
    {
        var schema = !string.IsNullOrEmpty(def.Schema) ? def.Schema : def.Namespace;

        // Per-(schema, table) NpgsqlDataSource: SearchPath scopes to this
        // partition's schema, MaxPoolSize=1 because the hub's actor scheduler
        // serialises every query — one open connection is sufficient.
        var connBuilder = new NpgsqlConnectionStringBuilder(_baseConnectionString)
        {
            SearchPath = $"{schema},public",
            MaxPoolSize = 1
        };
        var dsBuilder = new NpgsqlDataSourceBuilder(connBuilder.ConnectionString);
        dsBuilder.UseVector();
        _configureDataSource?.Invoke(dsBuilder);
        var ds = dsBuilder.Build();

        // The inner PostgreSqlStorageAdapter is given a constrained
        // PartitionDefinition where every ResolveTable(...) call returns the
        // single table this hub owns. This collapses the adapter's internal
        // table-routing logic into a no-op while still reusing the SQL builders.
        var tableScopedDef = def with
        {
            Table = table,
            TableMappings = null
        };

        return new PostgreSqlStorageAdapter(ds, _embeddingProvider, tableScopedDef);
    }

    // Inherited default: PartitionDefinition? PartitionDefinition => null;
    // Wildcard provider — partition definitions come from the dict, not a
    // single static property. (Cosmos / SQL discovery uses the same shape.)

    /// <inheritdoc/>
    public IStorageAdapter Adapter => throw new InvalidOperationException(
        "PostgreSqlPartitionStorageProvider has no single shared adapter — use "
        + "CreateAdapterForTable(def, table). The Adapter property is retained on "
        + "IPartitionStorageProvider for legacy static-provider compatibility only.");

    private static string? GetFirstSegment(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var normalized = path.Trim('/');
        if (normalized.Length == 0) return null;
        var slash = normalized.IndexOf('/');
        return slash < 0 ? normalized : normalized[..slash];
    }
}
