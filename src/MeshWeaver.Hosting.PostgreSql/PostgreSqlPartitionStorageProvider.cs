using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace MeshWeaver.Hosting.PostgreSql;

/// <summary>
/// <see cref="IPartitionStorageProvider"/> implementation backing Postgres
/// partitions. Replaces the legacy <see cref="PostgreSqlPartitionedStoreFactory"/>
/// shared-pool model with per-(schema, table) adapters that own a tiny
/// <see cref="NpgsqlDataSource"/> with <c>MaxPoolSize=1</c>.
///
/// <para><b>Live partition routing.</b> Per first-segment we hold a
/// <see cref="ReplaySubject{T}"/> of <see cref="PartitionDefinition"/>?.
/// On first <see cref="Matches"/>/<see cref="ResolveDefinition"/> against
/// a previously-unseen first-segment we kick off a schema-existence probe
/// against <c>information_schema.schemata</c> on the thread pool and push
/// the result into the subject (<c>null</c> = no schema, hence no
/// partition; non-null = synthesised <see cref="PartitionDefinition"/>).
/// The <see cref="SubscribeToWorkspace"/> call later attaches an
/// <c>ObserveQuery</c> subscription that updates each subject when its
/// matching <c>Admin/Partition/*</c> row is added or removed — same
/// <see cref="ReplaySubject{T}"/>, just refreshed values, so any
/// subscriber (PersistenceService, PathRoutingAdapter) sees the new
/// state without needing to know who's emitting.</para>
///
/// <para>See <c>Doc/Architecture/PartitionStorageHubs.md</c>.</para>
/// </summary>
public sealed class PostgreSqlPartitionStorageProvider : IPartitionStorageProvider, IDisposable
{
    private readonly NpgsqlDataSource _baseDataSource;
    private readonly string _baseConnectionString;
    private readonly PostgreSqlStorageOptions _options;
    private readonly IEmbeddingProvider? _embeddingProvider;
    private readonly Action<NpgsqlDataSourceBuilder>? _configureDataSource;

    /// <summary>
    /// Per first-segment <see cref="ReplaySubject{T}"/>. The subject buffers
    /// the latest <see cref="PartitionDefinition"/>? for that namespace so
    /// every subscriber receives the current value on subscribe and every
    /// subsequent update. <c>null</c> means "no partition for this segment"
    /// — <see cref="Matches"/> maps the null/non-null projection to a bool
    /// observable.
    /// </summary>
    private readonly ConcurrentDictionary<string, ReplaySubject<PartitionDefinition?>> _partitionSubjects =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, byte> _schemasInitialized =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task> _schemaInitTasks =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<PostgreSqlPartitionStorageProvider>? _logger;
    private IDisposable? _partitionSubscription;

    /// <summary>
    /// Internal access to the shared base <see cref="NpgsqlDataSource"/> so
    /// the host service that drives schema discovery can issue an
    /// <c>information_schema.tables</c> query against the same Postgres
    /// instance without re-resolving DI.
    /// </summary>
    internal NpgsqlDataSource BaseDataSource => _baseDataSource;

    /// <inheritdoc/>
    public string Name => "Postgres";

    /// <inheritdoc/>
    public bool IsReadOnly => false;

    /// <inheritdoc/>
    public ImmutableHashSet<string> Contexts { get; }

    /// <summary>
    /// Constructs a provider over <paramref name="baseDataSource"/> /
    /// <paramref name="baseConnectionString"/>. <paramref name="partitions"/>
    /// seeds the per-namespace subjects at boot. The standard mesh-builder
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
        IEnumerable<string>? contexts = null,
        ILogger<PostgreSqlPartitionStorageProvider>? logger = null)
    {
        _baseDataSource = baseDataSource;
        _baseConnectionString = baseConnectionString;
        _options = options;
        _embeddingProvider = embeddingProvider;
        _configureDataSource = configureDataSource;
        _logger = logger;

        Contexts = contexts != null
            ? ImmutableHashSet.CreateRange(StringComparer.OrdinalIgnoreCase, contexts)
            : ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase,
                PartitionContexts.Search,
                PartitionContexts.Create,
                PartitionContexts.Autocomplete,
                PartitionContexts.Browse);

        _adapter = new PostgreSqlPathRoutingAdapter(this);

        if (partitions != null)
            foreach (var def in partitions)
                RegisterPartition(def);
    }

    /// <summary>
    /// Subscribes to <c>Admin/Partition/*</c> nodes on the workspace and
    /// updates the per-first-segment subjects when partition rows are added,
    /// changed, or removed. Each emission also ensures the SQL schema +
    /// standard tables exist (idempotent CREATE SCHEMA IF NOT EXISTS).
    /// <para>Call once after the mesh hub is up. Returns an <see cref="IDisposable"/>
    /// that ends the subscription; the provider also disposes it on
    /// <see cref="Dispose"/>.</para>
    /// </summary>
    public IDisposable SubscribeToWorkspace(IMessageHub mesh)
    {
        var meshService = mesh.ServiceProvider.GetRequiredService<IMeshService>();
        _partitionSubscription = meshService
            .ObserveQuery<MeshNode>(new MeshQueryRequest
            {
                Query = "namespace:Admin/Partition nodeType:Partition",
                Skip = 0,
                Limit = 1000
            })
            .Select(c => c.Items
                .Select(n => n.Content)
                .OfType<PartitionDefinition>()
                .Where(d => !string.IsNullOrEmpty(d.Namespace))
                .ToImmutableList())
            .SelectMany(defs => defs.ToObservable()
                .SelectMany(def => Observable.FromAsync(ct => EnsureSchemaAsync(def, ct))
                    .Catch<PartitionDefinition, Exception>(ex =>
                    {
                        _logger?.LogWarning(ex,
                            "PostgreSqlPartitionStorageProvider: failed to ensure schema for {Namespace}; "
                            + "writes to this partition will not route until the next emission.",
                            def.Namespace);
                        return Observable.Empty<PartitionDefinition>();
                    })))
            .Subscribe(
                def => RegisterPartition(def),
                ex => _logger?.LogError(ex,
                    "PostgreSqlPartitionStorageProvider: Admin/Partition stream failed; "
                    + "no further partition updates will be observed."));
        return _partitionSubscription;
    }

    /// <summary>
    /// Internal entrypoint for callers that pre-register a partition before
    /// the workspace stream emits it (e.g.
    /// <see cref="PostgreSqlPartitionSubscriptionHostedService"/>'s static-provider
    /// seeding pass). Idempotent.
    /// </summary>
    internal Task EnsureSchemaForPartitionAsync(PartitionDefinition def, CancellationToken ct)
        => EnsureSchemaAsync(def, ct);

    private async Task<PartitionDefinition> EnsureSchemaAsync(
        PartitionDefinition def, CancellationToken ct)
    {
        var schema = !string.IsNullOrEmpty(def.Schema) ? def.Schema : def.Namespace;
        if (string.IsNullOrEmpty(schema)) return def;

        // Fast path — already initialized this silo session.
        if (_schemasInitialized.ContainsKey(schema)) return def;

        await using (var cmd = _baseDataSource.CreateCommand(
            $"CREATE SCHEMA IF NOT EXISTS \"{schema}\""))
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }

        var builder = new NpgsqlConnectionStringBuilder(_baseConnectionString)
        {
            SearchPath = $"{schema},public",
            MaxPoolSize = 2,
            ConnectionIdleLifetime = 10
        };
        var dsBuilder = new NpgsqlDataSourceBuilder(builder.ConnectionString);
        dsBuilder.UseVector();
        _configureDataSource?.Invoke(dsBuilder);
        await using var ddlDs = dsBuilder.Build();

        var schemaOptions = new PostgreSqlStorageOptions
        {
            ConnectionString = builder.ConnectionString,
            VectorDimensions = _options.VectorDimensions,
            Schema = schema
        };
        await PostgreSqlSchemaInitializer.InitializeAsync(ddlDs, schemaOptions, ct);

        var satelliteTables = new HashSet<string>(StringComparer.Ordinal);
        if (def.TableMappings is { Count: > 0 })
            foreach (var t in def.TableMappings.Values)
                if (!string.IsNullOrEmpty(t)) satelliteTables.Add(t);
        if (!string.IsNullOrEmpty(def.Table) &&
            !string.Equals(def.Table, "mesh_nodes", StringComparison.Ordinal))
            satelliteTables.Add(def.Table);
        if (satelliteTables.Count > 0)
        {
            await PostgreSqlSchemaInitializer.CreateSatelliteTablesAsync(
                ddlDs, schemaOptions, satelliteTables, ct);
        }

        _schemasInitialized.TryAdd(schema, 0);
        _logger?.LogInformation(
            "PostgreSqlPartitionStorageProvider: schema {Schema} ready for partition {Namespace}",
            schema, def.Namespace);
        return def;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _partitionSubscription?.Dispose();
        _partitionSubscription = null;
        foreach (var subj in _partitionSubjects.Values)
            subj.OnCompleted();
    }

    /// <summary>
    /// Pushes a partition into the per-namespace subject. Subsequent
    /// <see cref="Matches"/>/<see cref="ResolveDefinition"/> subscribers see
    /// the new value immediately (ReplaySubject buffer = 1). Idempotent —
    /// pushing the same definition twice is a no-op as far as downstream
    /// equality-comparing operators are concerned.
    /// </summary>
    public void RegisterPartition(PartitionDefinition def)
    {
        if (string.IsNullOrEmpty(def.Namespace)) return;
        GetOrCreateSubject(def.Namespace).OnNext(def);
    }

    /// <summary>
    /// Pushes <c>null</c> into the namespace's subject, marking it as no
    /// longer routable. Useful when an organization is decommissioned and
    /// we want the route to fail fast rather than wait out a TTL.
    /// </summary>
    public void RemovePartition(string @namespace)
    {
        if (string.IsNullOrEmpty(@namespace)) return;
        if (_partitionSubjects.TryGetValue(@namespace, out var subj))
            subj.OnNext(null);
    }

    private ReplaySubject<PartitionDefinition?> GetOrCreateSubject(string firstSegment)
    {
        return _partitionSubjects.GetOrAdd(firstSegment, seg =>
        {
            // Each fresh subject seeds itself via a thread-pool schema probe
            // so the value is available within milliseconds of first
            // subscribe even if SubscribeToWorkspace hasn't yet attached its
            // ObserveQuery feed. We escape any captured scheduler (Orleans
            // grain) by using Task.Run — the npgsql call below is fully async.
            var subj = new ReplaySubject<PartitionDefinition?>(1);
            _ = Task.Run(async () =>
            {
                try
                {
                    var def = await QueryPartitionFromSchemaAsync(seg).ConfigureAwait(false);
                    subj.OnNext(def);
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex,
                        "PostgreSqlPartitionStorageProvider: schema probe for '{Segment}' failed; "
                        + "treating as unroutable until next update.", seg);
                    subj.OnNext(null);
                }
            });
            return subj;
        });
    }

    /// <summary>
    /// Internal lookup used by <see cref="PostgreSqlPathRoutingAdapter"/> —
    /// emits the cached <see cref="PartitionDefinition"/> for the path's
    /// first segment (probing <c>information_schema.schemata</c> on first
    /// access). To be replaced by <c>PgPartitionCache</c> in Stage 5.
    /// </summary>
    internal IObservable<PartitionDefinition?> ResolvePartitionDefinition(string fullPath)
    {
        var seg = GetFirstSegment(fullPath);
        if (string.IsNullOrEmpty(seg)) return Observable.Return<PartitionDefinition?>(null);
        return GetOrCreateSubject(seg);
    }

    /// <summary>
    /// Asynchronous schema-existence probe against <c>information_schema.schemata</c>.
    /// If the schema exists, returns a default <see cref="PartitionDefinition"/>
    /// with standard table mappings; otherwise null. Hot path is the
    /// <see cref="_partitionSubjects"/> ReplaySubject; this is the cold-fill
    /// (one round-trip per unseen first-segment per silo).
    /// </summary>
    private async Task<PartitionDefinition?> QueryPartitionFromSchemaAsync(string firstSegment)
    {
        await using var conn = await _baseDataSource.OpenConnectionAsync().ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT schema_name FROM information_schema.schemata
            WHERE lower(schema_name) = lower($1)
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue(firstSegment);
        var scalar = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
        if (scalar is not string actualSchema || string.IsNullOrEmpty(actualSchema))
            return null;

        return new PartitionDefinition
        {
            Namespace = firstSegment,
            DataSource = "default",
            Schema = actualSchema,
            Table = "mesh_nodes",
            TableMappings = PartitionDefinition.StandardTableMappings,
            Versioned = true,
        };
    }

    /// <inheritdoc/>
    public IStorageAdapter CreateAdapterForTable(PartitionDefinition def, string table)
    {
        var schema = !string.IsNullOrEmpty(def.Schema) ? def.Schema : def.Namespace;

        EnsureSchemaForPartitionSync(def);

        var connBuilder = new NpgsqlConnectionStringBuilder(_baseConnectionString)
        {
            SearchPath = $"{schema},public",
            MaxPoolSize = 1
        };
        var dsBuilder = new NpgsqlDataSourceBuilder(connBuilder.ConnectionString);
        dsBuilder.UseVector();
        _configureDataSource?.Invoke(dsBuilder);
        var ds = dsBuilder.Build();

        var tableScopedDef = def with
        {
            Table = table,
            TableMappings = null
        };

        return new PostgreSqlStorageAdapter(ds, _embeddingProvider, tableScopedDef);
    }

    /// <inheritdoc/>
    public IStorageAdapter Adapter => _adapter;
    private readonly PostgreSqlPathRoutingAdapter _adapter;

    /// <summary>
    /// Per-schema adapter cache used by <see cref="Adapter"/>. Each entry
    /// reuses the shared base <see cref="NpgsqlDataSource"/> with a
    /// <see cref="PartitionDefinition"/> scoped to the schema; per-table
    /// routing happens inside <see cref="PostgreSqlStorageAdapter"/> via
    /// <see cref="PartitionDefinition.ResolveTable"/>.
    /// <para>Observable form — callers compose with <c>SelectMany</c>; the
    /// adapter is materialised once the partition definition is known.</para>
    /// </summary>
    internal IObservable<IStorageAdapter?> ResolveAdapterForSchema(string firstSegment)
    {
        return ResolvePartitionDefinition(firstSegment).Take(1).Select(def =>
        {
            // Stage 1: lazy-create when the cache reports the schema doesn't
            // exist. Mirrors the pre-refactor catch-all behaviour; PgPartitionCache
            // (Stage 5) will refine this to distinguish PendingCreate vs Absent.
            var effective = def ?? new PartitionDefinition
            {
                Namespace = firstSegment,
                DataSource = "default",
                Schema = firstSegment.ToLowerInvariant(),
                Table = "mesh_nodes",
                TableMappings = PartitionDefinition.StandardTableMappings,
                Versioned = true,
            };
            EnsureSchemaForPartitionSync(effective);
            return (IStorageAdapter?)new PostgreSqlStorageAdapter(_baseDataSource, _embeddingProvider, effective);
        });
    }

    /// <summary>
    /// Synchronously waits for the schema's first-touch init to complete.
    /// Idempotent — already-initialized schemas return immediately via the
    /// <see cref="_schemasInitialized"/> fast path.
    /// </summary>
    private void EnsureSchemaForPartitionSync(PartitionDefinition def)
    {
        var schema = !string.IsNullOrEmpty(def.Schema) ? def.Schema : def.Namespace;
        if (string.IsNullOrEmpty(schema)) return;
        if (_schemasInitialized.ContainsKey(schema)) return;

        var task = _schemaInitTasks.GetOrAdd(schema,
            _ => EnsureSchemaAsync(def, CancellationToken.None));
        task.GetAwaiter().GetResult();
    }

    private static string? GetFirstSegment(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var normalized = path.Trim('/');
        if (normalized.Length == 0) return null;
        var slash = normalized.IndexOf('/');
        return slash < 0 ? normalized : normalized[..slash];
    }
}
