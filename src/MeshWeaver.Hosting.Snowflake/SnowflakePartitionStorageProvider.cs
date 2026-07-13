using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Data;
using System.Data.Common;
using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Hosting.Embeddings;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Snowflake;

/// <summary>
/// Snowflake-backed <see cref="IPartitionStorageProvider"/> — the Snowflake port of
/// <c>PostgreSqlPartitionStorageProvider</c>. Owns the single shared
/// <see cref="SnowflakeConnectionSource"/>; the actual storage adapter
/// (<see cref="SnowflakePathRoutingAdapter"/>) routes per-path to a
/// per-schema <see cref="SnowflakeStorageAdapter"/>.
///
/// <para><b>No partition discovery, no existence probe, no lazy create.</b> The router maps
/// a path's first segment to a schema <i>synchronously</i> (<c>seg.ToLowerInvariant()</c>)
/// — no <c>information_schema</c> probe, no async cache. Schema creation is eager and gated to
/// partition-owning creates (<c>OwnsPartitionProvisioningValidator</c> →
/// <see cref="EnsurePartitionProvisioned"/>); the router itself NEVER creates a schema. A write
/// to an unprovisioned partition faults with the driver's "does not exist or not authorized"
/// error ("no partition, no write"); reads tolerate an absent schema (the per-schema adapter
/// catches that error → empty). The <c>_</c>-prefix global-satellite namespaces (whose schema
/// name differs from the namespace) are resolved from the registered-partition map seeded at
/// boot by the static-partition providers.</para>
///
/// <para><b>One connection source for every schema.</b> Unlike PG (which builds per-schema
/// <c>NpgsqlDataSource</c>s with a <c>search_path</c> for bespoke satellite DDL), Snowflake
/// needs no per-schema data sources: every statement is fully schema-qualified via
/// <see cref="SnowflakeIdentifiers.Qualify"/>, so all adapters share this provider's single
/// <see cref="SnowflakeConnectionSource"/> and differ only by
/// <see cref="PartitionDefinition.Schema"/>.</para>
///
/// <para>See <c>Doc/Architecture/PartitionStorageHubs.md</c>.</para>
/// </summary>
public sealed class SnowflakePartitionStorageProvider : IPartitionStorageProvider, IDisposable
{
    private readonly SnowflakeConnectionSource _connectionSource;
    private readonly SnowflakeStorageOptions _options;
    private readonly IEmbeddingProvider? _embeddingProvider;
    private readonly ILogger<SnowflakePartitionStorageProvider>? _logger;
    private readonly SnowflakeCapabilityHolder? _capabilities;
    // Registered partition definitions, keyed by namespace (first segment),
    // case-insensitive. Seeded by the boot-time static-partition provider, by
    // EnsureSchemaAsync, and by explicit RegisterPartition calls. The router
    // consults this ONLY to resolve `_`-prefix global-satellite namespaces
    // (whose schema name differs from the lowercased namespace) and to reuse a
    // richer PartitionDefinition when one was registered; ordinary partitions
    // resolve synchronously to `seg.ToLowerInvariant()` without it. Instance
    // field (never static) so its lifetime is the mesh's.
    private readonly ConcurrentDictionary<string, PartitionDefinition> _registeredPartitions =
        new(StringComparer.OrdinalIgnoreCase);
    // One READ pool (sf-read:{adapter}) shared by every adapter this provider creates — they all
    // share the single driver session pool, so the read bound must be shared too. Bounds read
    // fan-out below the session-pool size (leaves write headroom). This IS the one sanctioned
    // IIoPool primitive — no standalone SemaphoreSlim anywhere.
    private readonly IIoPool _readPool;

    /// <summary>Per-silo memo of "CREATE SCHEMA already ran this session".</summary>
    private readonly ConcurrentDictionary<string, byte> _schemasInitialized =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Promise-cache of in-flight / completed partition provisioning, keyed by schema.
    /// The CREATE SCHEMA round-trip runs at most once per (silo, schema): <see cref="IoPoolExtensions.Run"/>
    /// is eager + <see cref="System.Reactive.Subjects.ReplaySubject{T}"/>-backed, so the first caller
    /// kicks the DDL off on the per-adapter pool and every later subscriber replays the cached
    /// completion. Instance field (never static) so its lifetime is the mesh's.
    /// </summary>
    private readonly ConcurrentDictionary<string, IObservable<Unit>> _provisioned =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Per-adapter I/O pool (<c>sf:{adapter}</c>), capped at one in-flight op so the gate mirrors
    /// a single logical Snowflake connection — the gate IS the connection, the same contract the
    /// <c>pg:{adapter}</c> pool holds for Npgsql. The async DB edge is sealed inside
    /// <see cref="IIoPool"/>; there is NO <c>Observable.FromAsync</c> at any call site
    /// (forbidden — see ControlledIoPooling.md).
    /// </summary>
    private readonly IIoPool _ioPool;

    /// <summary>The single shared connection source every per-schema adapter opens against.</summary>
    internal SnowflakeConnectionSource ConnectionSource => _connectionSource;

    /// <summary>
    /// The CACHED per-schema adapter (shared live <c>Changes</c> feed) for a path's first
    /// segment, or null when not routable. Lets the Snowflake query layer own scoped query
    /// serving by delegating to a per-schema query provider.
    /// </summary>
    internal SnowflakeStorageAdapter? GetSchemaAdapter(string path) => _adapter.GetSchemaAdapter(path);

    /// <summary>Embedding provider for vector scoring, shared with the per-schema delegate.</summary>
    internal IEmbeddingProvider? EmbeddingProvider => _embeddingProvider;

    /// <summary>Shared per-adapter READ pool (<c>sf-read:{adapter}</c>) — bounds read fan-out below the session-pool size.</summary>
    internal IIoPool ReadPool => _readPool;

    /// <summary>
    /// Shared per-adapter WRITE/DDL pool (<c>sf:{adapter}</c>, cap 1) — handed to every
    /// per-schema adapter so all writes serialise through the one logical connection the
    /// gate models (there are no per-schema data sources to bound them instead).
    /// </summary>
    internal IIoPool WritePool => _ioPool;

    /// <summary>Probed endpoint capabilities shared with every adapter this provider creates.</summary>
    internal SnowflakeCapabilityHolder? Capabilities => _capabilities;

    /// <summary>Storage options shared with every adapter this provider creates.</summary>
    internal SnowflakeStorageOptions Options => _options;

    /// <summary>
    /// Synchronous lookup of a registered <see cref="PartitionDefinition"/> by
    /// namespace (first segment). Used by <see cref="SnowflakePathRoutingAdapter"/>
    /// to resolve <c>_</c>-prefix global-satellite namespaces to their real schema
    /// (e.g. <c>_Access</c> → <c>system_access</c>) and to reuse a registered def
    /// for an ordinary partition. No DB round-trip.
    /// </summary>
    internal bool TryGetRegisteredPartition(string @namespace, out PartitionDefinition def)
        => _registeredPartitions.TryGetValue(@namespace, out def!);

    /// <inheritdoc/>
    public string Name => "Snowflake";

    /// <inheritdoc/>
    public bool IsReadOnly => false;

    /// <inheritdoc/>
    /// <remarks>Durable backend - claims ahead of the in-memory wildcard catch-all.</remarks>
    public int Priority => 100;

    /// <inheritdoc/>
    public ImmutableHashSet<string> Contexts { get; }

    /// <summary>
    /// Initializes the partition storage provider, which maps each top-level path segment to its own Snowflake schema.
    /// </summary>
    /// <param name="connectionSource">The single shared connection source used for provisioning AND by every per-schema adapter (Snowflake needs no per-schema data sources — statements are schema-qualified).</param>
    /// <param name="options">Storage options (vector dimensions, satellite mapping, etc.).</param>
    /// <param name="partitions">Optional pre-known partition definitions registered at boot so the router can resolve their real schema names.</param>
    /// <param name="embeddingProvider">Optional embedding provider passed to each per-(def,table) adapter for vector search.</param>
    /// <param name="contexts">Optional partition contexts this provider participates in; defaults to Search, Create, Autocomplete, and Browse.</param>
    /// <param name="logger">Optional logger for provisioning and routing diagnostics.</param>
    /// <param name="ioPoolRegistry">Optional I/O pool registry providing the per-adapter write and read pools.</param>
    /// <param name="capabilities">Probed endpoint capabilities; when null, the real-Snowflake all-on profile is assumed.</param>
    public SnowflakePartitionStorageProvider(
        SnowflakeConnectionSource connectionSource,
        SnowflakeStorageOptions options,
        IEnumerable<PartitionDefinition>? partitions = null,
        IEmbeddingProvider? embeddingProvider = null,
        IEnumerable<string>? contexts = null,
        ILogger<SnowflakePartitionStorageProvider>? logger = null,
        IoPoolRegistry? ioPoolRegistry = null,
        SnowflakeCapabilityHolder? capabilities = null)
    {
        _connectionSource = connectionSource;
        _options = options;
        _embeddingProvider = embeddingProvider;
        _capabilities = capabilities;
        // Per-adapter WRITE pool (cap 1 — one logical connection) and READ pool (cap =
        // IoPoolOptions.SnowflakeRead, below the driver session-pool size so a read fan-out can't
        // starve writes). Both fall back to the unbounded pool only when constructed outside DI
        // (tests) — still off the hub scheduler, never FromAsync.
        _ioPool = ioPoolRegistry?.Get($"{IoPoolNames.SnowflakeAdapterPrefix}{Name}") ?? IoPool.Unbounded;
        _readPool = ioPoolRegistry?.Get($"{IoPoolNames.SnowflakeReadAdapterPrefix}{Name}") ?? IoPool.Unbounded;
        _logger = logger;

        Contexts = contexts != null
            ? ImmutableHashSet.CreateRange(StringComparer.OrdinalIgnoreCase, contexts)
            : ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase,
                PartitionContexts.Search,
                PartitionContexts.Create,
                PartitionContexts.Autocomplete,
                PartitionContexts.Browse);

        _adapter = new SnowflakePathRoutingAdapter(this);

        // Boot-time seed: ANY pre-known partition definition (e.g. one passed
        // by the mesh-builder for system schemas) is registered so the router
        // can resolve its real schema (notably the `_`-prefix globals whose
        // schema ≠ lowercased namespace). No enumeration — only what the caller
        // hands us.
        if (partitions != null)
            foreach (var def in partitions)
                if (!string.IsNullOrEmpty(def.Namespace))
                    _registeredPartitions[def.Namespace] = def;
    }

    /// <summary>
    /// Whether embedding columns are emitted when provisioning DDL runs — capability-dependent
    /// and therefore computed AT CALL TIME, never captured at construction:
    /// <see cref="SnowflakeCapabilityHolder.Current"/> starts at the all-on profile and is
    /// overwritten by the schema-initialization probe, so reading it lazily means provisioning
    /// that runs after the probe sees the real answer (the LocalStack emulator may lack
    /// <c>VECTOR</c>). <see cref="SnowflakeStorageOptions.EnableVectorType"/> <c>= false</c>
    /// force-disables; <c>null</c>/<c>true</c> defer to the probed capability.
    /// </summary>
    private bool VectorEnabled =>
        (_capabilities?.Current ?? SnowflakeCapabilities.AllOn).SupportsVector
        && _options.EnableVectorType != false;

    /// <summary>
    /// Idempotent CREATE SCHEMA + standard-tables init for one partition. Hot
    /// path: in-process memo (<see cref="_schemasInitialized"/>) returns
    /// immediately after the first call per (silo, schema).
    /// </summary>
    internal Task EnsureSchemaForPartitionAsync(PartitionDefinition def, CancellationToken ct)
        => EnsureSchemaAsync(def, ct);

    /// <summary>
    /// Runs idempotent provisioning DDL with a bounded retry on TRANSIENT concurrent-DDL
    /// errors — the Snowflake twin of the PG wrapper (which retries <c>XX000 "tuple
    /// concurrently updated"</c> / deadlocks). Two sessions provisioning partitions at the same
    /// time can race the catalog: the loser of a <c>CREATE … IF NOT EXISTS</c> race may still
    /// error "already exists", and concurrent DDL on the same objects can hit a lock-wait
    /// timeout or deadlock — NOT real failures: on retry the statement observes the winner's
    /// committed object (and the <c>IF NOT EXISTS</c> no-ops) and succeeds. Without this the
    /// loser's provisioning would be swallowed upstream and the partition stay unprovisioned →
    /// downstream 500s. The race fires on every multi-replica / rolling-deploy start, not just
    /// under tests. Bounded ×6, 50ms·attempt backoff — mirrored from PG.
    /// </summary>
    private static async Task ExecuteDdlWithRetryAsync(
        Func<CancellationToken, Task> ddl, CancellationToken ct, ILogger? logger = null)
    {
        const int maxAttempts = 6;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await ddl(ct).ConfigureAwait(false);
                return;
            }
            catch (DbException ex) when (IsTransientDdlRace(ex) && attempt < maxAttempts)
            {
                logger?.LogDebug(ex,
                    "SnowflakePartitionStorageProvider: transient concurrent-DDL error ({Message}) on attempt {Attempt}/{Max}; retrying",
                    ex.Message, attempt, maxAttempts);
                await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt), ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Snowflake errors meaning "another session is concurrently running the same idempotent
    /// DDL" — transient and safe to retry (the retry observes the committed result). The driver
    /// surfaces no stable SQLSTATE for these, so the match is on message text: a lost
    /// <c>CREATE … IF NOT EXISTS</c> race ("already exists"), a DDL lock-wait timeout, or a
    /// deadlock.
    /// </summary>
    private static bool IsTransientDdlRace(DbException ex) =>
        ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("deadlock", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("waiting for lock", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("lock wait timeout", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The single source of truth for per-partition provisioning on this backend:
    /// <see cref="SnowflakeSchemaInitializer.EnsurePartitionSchemaAsync"/> idempotently creates
    /// the schema + <c>{schema}.mesh_nodes</c> + every standard satellite / support table
    /// (Snowflake has no stored procedures in this port — PG routes the same DDL through
    /// <c>public.ensure_partition_schema</c>). Bespoke <see cref="PartitionDefinition.Table"/> /
    /// <see cref="PartitionDefinition.TableMappings"/> entries outside the standard set are
    /// created afterwards on the SAME shared connection source (no per-schema data source —
    /// every statement is schema-qualified). Finishes by memoizing the schema and registering
    /// the definition for the router.
    /// </summary>
    private async Task<PartitionDefinition> EnsureSchemaAsync(
        PartitionDefinition def, CancellationToken ct)
    {
        var schema = !string.IsNullOrEmpty(def.Schema) ? def.Schema : def.Namespace;
        if (string.IsNullOrEmpty(schema)) return def;

        if (_schemasInitialized.ContainsKey(schema)) return def;

        // Capability-dependent: resolved NOW (inside the pooled call), after the boot-time
        // probe has overwritten SnowflakeCapabilityHolder.Current — see VectorEnabled.
        var vectorEnabled = VectorEnabled;

        await ExecuteDdlWithRetryAsync(
            attemptCt => SnowflakeSchemaInitializer.EnsurePartitionSchemaAsync(
                _connectionSource, schema, _options.VectorDimensions, vectorEnabled, _logger, attemptCt),
            ct, _logger).ConfigureAwait(false);

        // Non-standard satellite tables: EnsurePartitionSchemaAsync only provisions the standard
        // set (SatelliteTableMapping.Defaults). If this def carries a bespoke Table or
        // TableMappings entry outside that set, create it too so routing to it doesn't hit
        // "does not exist". The common partition-root case (standard mappings +
        // Table="mesh_nodes") finds nothing extra here and skips the second round-trip.
        var standard = new HashSet<string>(
            PartitionDefinition.DefaultSegmentTableMappings().Values, StringComparer.Ordinal);
        var extraTables = new HashSet<string>(StringComparer.Ordinal);
        if (def.TableMappings is { Count: > 0 })
            foreach (var t in def.TableMappings.Values)
                if (!string.IsNullOrEmpty(t) && !standard.Contains(t)) extraTables.Add(t);
        if (!string.IsNullOrEmpty(def.Table) &&
            !string.Equals(def.Table, "mesh_nodes", StringComparison.Ordinal) &&
            !standard.Contains(def.Table))
            extraTables.Add(def.Table);
        if (extraTables.Count > 0)
        {
            await ExecuteDdlWithRetryAsync(async attemptCt =>
            {
                await using var connection = await _connectionSource.OpenAsync(attemptCt).ConfigureAwait(false);
                foreach (var table in extraTables)
                foreach (var sql in SnowflakeSchemaInitializer.GetSatelliteTableStatements(
                             schema, table, _options.VectorDimensions, vectorEnabled))
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = sql;
                    await command.ExecuteNonQueryAsync(attemptCt).ConfigureAwait(false);
                }
            }, ct, _logger).ConfigureAwait(false);
        }

        _schemasInitialized.TryAdd(schema, 0);
        if (!string.IsNullOrEmpty(def.Namespace))
            _registeredPartitions[def.Namespace] = def;
        _logger?.LogInformation(
            "SnowflakePartitionStorageProvider: schema {Schema} ready for partition {Namespace}",
            schema, def.Namespace);
        return def;
    }

    /// <summary>
    /// The configured segment→table satellite map (from <c>SnowflakeStorageOptions.SatelliteTables</c>,
    /// host-overridable). Stamped onto every partition this provider provisions/routes so a custom
    /// satellite layout actually takes effect — not the hardcoded default.
    /// </summary>
    internal Dictionary<string, string> SatelliteSegmentMappings()
        => SatelliteTableMapping.ToSegmentTableMap(_options.SatelliteTables);

    /// <summary>The configured nodeType→table satellite map (from <c>SnowflakeStorageOptions.SatelliteTables</c>).</summary>
    internal Dictionary<string, string> SatelliteNodeTypeMappings()
        => SatelliteTableMapping.ToNodeTypeTableMap(_options.SatelliteTables);

    /// <summary>
    /// The reactive provisioning entry point — the ONE path that creates a partition's
    /// schema + tables, driven by <c>OwnsPartitionProvisioningValidator</c> when a
    /// top-level <c>User</c>/<c>Space</c> is created. Routed through
    /// <see cref="SnowflakeSchemaInitializer.EnsurePartitionSchemaAsync"/>. Idempotent; the
    /// per-silo memo short-circuits repeat calls for the same schema. Emits once and completes.
    ///
    /// <para>The storage router never lazily creates schemas on arbitrary writes, so a
    /// write whose partition was never provisioned here fails loudly ("does not exist or
    /// not authorized") instead of conjuring a ghost schema. See
    /// <c>Doc/Architecture/PartitionStorageRouting.md</c>.</para>
    /// </summary>
    public IObservable<Unit> EnsurePartitionProvisioned(string @namespace)
    {
        if (string.IsNullOrEmpty(@namespace))
            return Observable.Return(Unit.Default);
        var def = new PartitionDefinition
        {
            Namespace = @namespace,
            DataSource = Name,
            Schema = @namespace.ToLowerInvariant(),
            Table = "mesh_nodes",
            TableMappings = SatelliteSegmentMappings(),
            NodeTypeTableMappings = SatelliteNodeTypeMappings(),
            Versioned = true,
        };
        // Promise-cache: provision each schema at most once. IIoPool.Run pushes the
        // CREATE SCHEMA onto the per-adapter pool (cap 1 = one logical connection) and replays
        // the single completion to every subscriber. NO Observable.FromAsync here — the only
        // async edge lives sealed inside IIoPool (forbidden everywhere else; see
        // ControlledIoPooling.md).
        return _provisioned.GetOrAdd(def.Schema!, _ =>
            _ioPool.Run(ct => EnsureSchemaAsync(def, ct)).Select(_ => Unit.Default));
    }

    /// <summary>
    /// Read-only existence probe (see <see cref="IPartitionStorageProvider.PartitionExists"/>).
    /// NEVER creates a schema. Tri-state contract:
    /// <list type="bullet">
    ///   <item><c>true</c> — the schema exists (registered/initialised this session,
    ///     or found in <c>INFORMATION_SCHEMA.SCHEMATA</c>).</item>
    ///   <item><c>false</c> — the probe ran and no matching schema exists; the
    ///     partition genuinely does not exist (this is what lets the
    ///     <c>PartitionWriteGuardValidator</c> reject implicit space creation).</item>
    ///   <item><c>null</c> — the probe itself failed; indeterminate, so the guard
    ///     must NOT reject on it.</item>
    /// </list>
    /// Fast positive: a namespace we've already registered or CREATE'd this session
    /// answers <c>true</c> with no round-trip. Otherwise a single read-only
    /// <c>INFORMATION_SCHEMA.SCHEMATA</c> query at the reactive leaf, sealed inside
    /// <see cref="IIoPool"/> — this is a genuine existence query, NOT partition routing.
    /// The schema resolves by lowercase match, exact for user/space partitions
    /// (schema == lower(id)); the namespace≠schema <c>_</c>-prefix globals are exempted by
    /// the guard before it ever calls this, and the named system schemas
    /// (admin/auth/portal/kernel/doc) resolve correctly by lower-case match.
    /// </summary>
    public IObservable<bool?> PartitionExists(string @namespace)
    {
        if (string.IsNullOrEmpty(@namespace)) return Observable.Return<bool?>(null);

        var schema = _registeredPartitions.TryGetValue(@namespace, out var def)
                     && !string.IsNullOrEmpty(def.Schema)
            ? def.Schema!
            : @namespace.ToLowerInvariant();

        if (_registeredPartitions.ContainsKey(@namespace) || _schemasInitialized.ContainsKey(schema))
            return Observable.Return<bool?>(true);

        return _ioPool.Invoke<bool?>(async ct =>
        {
            await using var connection = await _connectionSource.OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            // 🚨 Deliberate exception to the always-quote-identifiers rule: this backend
            // double-quotes every schema/table identifier (Snowflake uppercases unquoted
            // names, and the router emits lowercase schemas). INFORMATION_SCHEMA's view and
            // column identifiers, however, are UPPERCASE objects — quoting them lowercase
            // ("information_schema"."schemata"/"schema_name") would reference objects that
            // do not exist. Emitting them UNQUOTED lets Snowflake's default uppercase fold
            // resolve them correctly; the lowercase COMPARISON happens in the predicate
            // (LOWER(schema_name) = :schema, bound lowercased), mirroring PG's
            // lower(schema_name) = lower($1).
            command.CommandText =
                "SELECT 1 FROM information_schema.schemata WHERE LOWER(schema_name) = :schema LIMIT 1";
            SnowflakeConnectionSource.AddParam(command, "schema", schema.ToLowerInvariant(), DbType.String);
            var scalar = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return scalar is not null && scalar is not DBNull;
        })
        .Catch<bool?, Exception>(ex =>
        {
            _logger?.LogDebug(ex,
                "PartitionExists: schema probe for '{Namespace}' (schema '{Schema}') failed; emitting null (indeterminate).",
                @namespace, schema);
            return Observable.Return<bool?>(null);
        });
    }

    /// <summary>
    /// Drops the partition's backing schema — the inverse of
    /// <see cref="EnsurePartitionProvisioned"/>. <c>DROP SCHEMA IF EXISTS … CASCADE</c>
    /// removes the partition's <c>mesh_nodes</c> and every satellite table in one atomic
    /// DDL statement, then evicts the provisioning caches (<see cref="_schemasInitialized"/>,
    /// <see cref="_provisioned"/>, <see cref="_registeredPartitions"/>) AND the router's cached
    /// per-schema adapter (its merged-feed subscription included) so a later re-create of the
    /// same partition provisions from scratch instead of replaying the cached "already
    /// provisioned" promise — or reusing an adapter bound to a schema that no longer exists.
    /// Idempotent — dropping an absent schema is a no-op. The async DB edge is sealed inside
    /// <see cref="IIoPool"/> (never <c>Observable.FromAsync</c>).
    /// </summary>
    public IObservable<Unit> DeletePartition(string @namespace)
    {
        if (string.IsNullOrEmpty(@namespace))
            return Observable.Return(Unit.Default);

        var schema = _registeredPartitions.TryGetValue(@namespace, out var def)
                     && !string.IsNullOrEmpty(def.Schema)
            ? def.Schema!
            : @namespace.ToLowerInvariant();

        return _ioPool.Invoke(async ct =>
        {
            await ExecuteDdlWithRetryAsync(async attemptCt =>
            {
                // Schema name is identifier-quoted — it derives from a node id, not raw SQL.
                await using var connection = await _connectionSource.OpenAsync(attemptCt).ConfigureAwait(false);
                await using var command = connection.CreateCommand();
                command.CommandText = $"DROP SCHEMA IF EXISTS {SnowflakeIdentifiers.Quote(schema)} CASCADE";
                await command.ExecuteNonQueryAsync(attemptCt).ConfigureAwait(false);
            }, ct, _logger).ConfigureAwait(false);

            _schemasInitialized.TryRemove(schema, out _);
            _provisioned.TryRemove(schema, out _);
            _registeredPartitions.TryRemove(@namespace, out _);
            _adapter.EvictSchemaAdapter(schema);
            _logger?.LogInformation(
                "SnowflakePartitionStorageProvider: dropped schema {Schema} for deleted partition {Namespace}",
                schema, @namespace);
            return Unit.Default;
        });
    }

    /// <summary>
    /// Tests / boot-time callers can pre-register a known
    /// <see cref="PartitionDefinition"/> so the router resolves its real schema
    /// (notably the <c>_</c>-prefix globals whose schema ≠ lowercased namespace)
    /// and <see cref="PartitionExists"/> answers <c>true</c> without a probe.
    /// No-op when the namespace is empty.
    /// </summary>
    public void RegisterPartition(PartitionDefinition def)
    {
        if (string.IsNullOrEmpty(def.Namespace)) return;
        _registeredPartitions[def.Namespace] = def;
    }

    /// <summary>
    /// Drops the registered definition for <paramref name="namespace"/>. Used by
    /// tests that simulate partition deletion. Does NOT drop the
    /// <c>_schemasInitialized</c> memo or the underlying schema — it only forgets
    /// the registered def so the router falls back to the synchronous
    /// <c>seg.ToLowerInvariant()</c> mapping (and `_`-prefix globals become
    /// unroutable again).
    /// </summary>
    public void RemovePartition(string @namespace)
    {
        if (string.IsNullOrEmpty(@namespace)) return;
        _registeredPartitions.TryRemove(@namespace, out _);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        // The pools are owned by IoPoolRegistry (mesh-scoped singleton) and the connection
        // source by DI — both die with the mesh; the provider must NOT dispose them here
        // (they may be shared / outlive this provider's Dispose).
    }

    /// <inheritdoc/>
    public IStorageAdapter CreateAdapterForTable(PartitionDefinition def, string table)
    {
        // No lazy CREATE SCHEMA here: per-(schema,table) hubs only spin up for partitions
        // that were already provisioned (OwnsPartitionProvisioningValidator). An adapter for
        // an absent schema tolerates "does not exist or not authorized" on read (→ empty)
        // and faults loudly on write — it never conjures a ghost schema.
        //
        // Reuse the single shared connection source. SnowflakeStorageAdapter
        // schema-qualifies every statement ("schema"."table") from the def
        // (SnowflakeIdentifiers.Qualify), so a per-(schema,table) session pool /
        // schema context is unnecessary — the PG per-table pool leak class
        // (one abandoned NpgsqlDataSource per hub) cannot occur here by construction.
        var tableScopedDef = def with
        {
            Table = table,
            TableMappings = null
        };

        return new SnowflakeStorageAdapter(
            _connectionSource,
            _embeddingProvider,
            tableScopedDef,
            logger: null,
            readPool: _readPool,
            ioPool: _ioPool,
            capabilities: _capabilities,
            options: _options);
    }

    /// <inheritdoc/>
    public IStorageAdapter Adapter => _adapter;
    private readonly SnowflakePathRoutingAdapter _adapter;

    /// <summary>
    /// The first segment of a normalized mesh path (leading/trailing slashes trimmed),
    /// or <c>null</c> when the path is empty. This IS the partition namespace the
    /// router lowercases into a schema name.
    /// </summary>
    internal static string? GetFirstSegment(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var normalized = path.Trim('/');
        if (normalized.Length == 0) return null;
        var slash = normalized.IndexOf('/');
        return slash < 0 ? normalized : normalized[..slash];
    }
}
