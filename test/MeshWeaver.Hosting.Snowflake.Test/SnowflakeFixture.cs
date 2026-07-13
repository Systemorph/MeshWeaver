using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using MeshWeaver.Hosting.Snowflake;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Threading;
using Xunit;

namespace MeshWeaver.Hosting.Snowflake.Test;

/// <summary>
/// Shared fixture over a Snowflake endpoint — the Snowflake port of
/// <c>PostgreSqlFixture</c> (same public surface, renamed types) so PG test classes port
/// mechanically. Unlike the PG fixture, the endpoint is OPTIONAL and gated:
/// <list type="bullet">
///   <item><c>SNOWFLAKE_CONNECTION</c> (a full Snowflake.Data connection string) set → run
///     against that endpoint directly (real-account nightly path); no container.</item>
///   <item>otherwise <c>LOCALSTACK_AUTH_TOKEN</c> set → start the LocalStack Snowflake
///     emulator via Testcontainers.</item>
///   <item>otherwise (or when container start / first connection fails) →
///     <see cref="Available"/> stays <c>false</c> and every test green-skips via
///     <see cref="SkipUnlessAvailable"/> — CI without Docker/token never goes red here.</item>
/// </list>
/// </summary>
public class SnowflakeFixture : IAsyncLifetime
{
    /// <summary>The LocalStack edge port (the emulator's Snowflake API listens here).</summary>
    private const ushort EmulatorPort = 4566;

    private IContainer? _container;
    private SnowflakeConnectionSource? _connectionSource;
    private string? _connectionString;
    private SnowflakeStorageAdapter? _storageAdapter;
    private SnowflakeAccessControl? _accessControl;

    /// <summary>Whether a Snowflake endpoint (emulator or real account) is up and initialized.</summary>
    public bool Available { get; private set; }

    /// <summary>Why <see cref="Available"/> is false — the skip reason surfaced to xunit.</summary>
    public string? UnavailableReason { get; private set; }

    /// <summary>The shared connection source (the Snowflake twin of the PG fixture's <c>DataSource</c>).</summary>
    public SnowflakeConnectionSource ConnectionSource => _connectionSource ?? throw Unavailable();

    /// <summary>The endpoint's Snowflake.Data connection string.</summary>
    public string ConnectionString => _connectionString ?? throw Unavailable();

    /// <summary>The default (central-schema) storage adapter.</summary>
    public SnowflakeStorageAdapter StorageAdapter => _storageAdapter ?? throw Unavailable();

    /// <summary>The default access-control helper over the central schema.</summary>
    public SnowflakeAccessControl AccessControl => _accessControl ?? throw Unavailable();

    /// <summary>Storage options in effect (central/events schema names, vector dimensions/enablement).</summary>
    public SnowflakeStorageOptions Options { get; private set; } = new();

    /// <summary>The probe result — what the connected endpoint actually supports.</summary>
    public SnowflakeCapabilities Capabilities { get; private set; } = SnowflakeCapabilities.AllOn;

    /// <summary>The capability holder handed to every adapter/access-control this fixture constructs.</summary>
    public SnowflakeCapabilityHolder CapabilityHolder { get; } = new();

    // Per-schema connection sources created via CreateSchemaAdapterAsync — tracked so
    // CleanDataAsync (called between tests) can dispose them and clear the driver's
    // session pools, mirroring the PG fixture's tracked per-schema NpgsqlDataSources.
    private readonly System.Collections.Concurrent.ConcurrentBag<SnowflakeConnectionSource>
        _trackedSchemaDataSources = new();

    private InvalidOperationException Unavailable()
        => new(UnavailableReason
               ?? "Snowflake endpoint unavailable — tests must call SkipUnlessAvailable() first.");

    /// <summary>Dynamic xunit-v3 skip when no endpoint is available (token/connection absent, Docker missing…).</summary>
    public void SkipUnlessAvailable()
        => Assert.SkipWhen(!Available, UnavailableReason ?? "Snowflake endpoint unavailable");

    /// <summary>
    /// Dynamic xunit-v3 skip for capability-gated constructs (e.g. <c>VECTOR</c> on the emulator):
    /// skips when unavailable OR when the probed <see cref="Capabilities"/> fail <paramref name="predicate"/>.
    /// </summary>
    public void SkipUnless(Func<SnowflakeCapabilities, bool> predicate, string reason)
    {
        SkipUnlessAvailable();
        Assert.SkipUnless(predicate(Capabilities), reason);
    }

    public async ValueTask InitializeAsync()
    {
        // Real-account nightly path: a full connection string bypasses the container entirely.
        // Failures here are deliberately LOUD (no green-skip): a misconfigured nightly account
        // should turn the run red, not silently skip every Snowflake test.
        var external = Environment.GetEnvironmentVariable("SNOWFLAKE_CONNECTION");
        if (!string.IsNullOrWhiteSpace(external))
        {
            _connectionString = external;
            _connectionSource = new SnowflakeConnectionSource(external);
            await InitializeEndpointAsync();
            Available = true;
            return;
        }

        var token = Environment.GetEnvironmentVariable("LOCALSTACK_AUTH_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
        {
            UnavailableReason =
                "LOCALSTACK_AUTH_TOKEN not set — LocalStack Snowflake emulator unavailable";
            return; // Available stays false; no container is started.
        }

        try
        {
            _container = new ContainerBuilder("localstack/snowflake:latest") // pin a digest/tag when stabilized
                .WithEnvironment("LOCALSTACK_AUTH_TOKEN", token)
                .WithPortBinding(EmulatorPort, assignRandomHostPort: true)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(
                    request => request.ForPort(EmulatorPort).ForPath("/_localstack/health")))
                .Build();
            await _container.StartAsync();

            // snowflake.localhost.localstack.cloud publicly resolves to 127.0.0.1 — the driver
            // needs a *.localstack.cloud hostname so the emulator routes the request; insecuremode
            // skips OCSP validation of the emulator's self-signed certificate. If CI DNS cannot
            // resolve the public name, add a hosts entry / container WithExtraHost for it.
            _connectionString =
                "account=test;user=test;password=test;db=test;schema=public;warehouse=test;" +
                $"host=snowflake.localhost.localstack.cloud;port={_container.GetMappedPublicPort(EmulatorPort)};" +
                "scheme=https;insecuremode=true";
            _connectionSource = new SnowflakeConnectionSource(_connectionString);
            await InitializeEndpointAsync();
            Available = true;
        }
        catch (Exception ex)
        {
            // Do NOT fail the fixture: CI without Docker (or with a broken emulator image) must
            // stay green-skipped. The exception message becomes the skip reason.
            Available = false;
            UnavailableReason = ex.Message;
            _connectionSource?.Dispose();
            _connectionSource = null;
            if (_container is not null)
            {
                try { await _container.DisposeAsync(); } catch { /* tearing down a failed start */ }
                _container = null;
            }
        }
    }

    /// <summary>
    /// Endpoint init sequence: central + events objects, capability probe (stored on
    /// <see cref="Capabilities"/> and pushed into <see cref="CapabilityHolder"/>), the central
    /// schema's partition tables plus the framework schemas the PG migration creates eagerly
    /// (<c>auth</c>, <c>system_access</c>) — mirroring what <c>PostgreSqlFixture</c> initializes —
    /// then the default adapter + access control.
    /// </summary>
    private async Task InitializeEndpointAsync()
    {
        var ct = CancellationToken.None;
        Options = new SnowflakeStorageOptions { ConnectionString = _connectionString };

        // Central (partition_access + searchable_schemas) + events (event_log + action_cursor).
        // Also the first connection — an unreachable endpoint fails here.
        await SnowflakeSchemaInitializer.InitializeAsync(_connectionSource!, Options, logger: null, ct);

        // Probe what the endpoint actually supports; every component reads through the holder.
        Capabilities = await SnowflakeCapabilityProbe.ProbeAsync(_connectionSource!, logger: null, ct);
        CapabilityHolder.Current = Capabilities;
        Options.EnableVectorType = Capabilities.SupportsVector;

        // Unlike PG's InitializeAsync, the Snowflake central initializer does NOT provision the
        // central schema's own partition tables — EnsurePartitionSchemaAsync (the twin of PG's
        // ensure_partition_schema) does. auth / system_access mirror the framework schemas the
        // PG fixture creates up front so full-mesh tests behave like prod.
        foreach (var schema in new[] { Options.Schema, "auth", "system_access" })
            await SnowflakeSchemaInitializer.EnsurePartitionSchemaAsync(
                _connectionSource!, schema, Options.VectorDimensions, Capabilities.SupportsVector,
                logger: null, ct);

        _storageAdapter = new SnowflakeStorageAdapter(
            _connectionSource!, capabilities: CapabilityHolder, options: Options);
        _accessControl = new SnowflakeAccessControl(
            _connectionSource!, centralSchema: Options.Schema, capabilities: CapabilityHolder);
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeTrackedSchemaDataSourcesAsync();
        if (_storageAdapter is not null)
            await _storageAdapter.DisposeAsync();
        _connectionSource?.Dispose();
        if (_container is not null)
            await _container.DisposeAsync();
    }

    /// <summary>
    /// Creates a per-schema connection source and adapter for a named schema — the Snowflake
    /// twin of the PG fixture's per-schema <c>SearchPath</c> data source (here the session's
    /// default schema is switched via the connection string's <c>schema=</c> key; the adapter
    /// additionally schema-qualifies every table via the <see cref="PartitionDefinition"/>,
    /// because Snowflake has no <c>search_path</c>).
    /// </summary>
    public async Task<(SnowflakeConnectionSource SchemaSource, SnowflakeStorageAdapter Adapter)>
        CreateSchemaAdapterAsync(string schemaName, PartitionDefinition? partitionDef = null, CancellationToken ct = default)
    {
        // Schema + the full standard partition/satellite table set — the Snowflake counterpart
        // of PG's `SELECT public.ensure_partition_schema(name)`. Idempotent (IF NOT EXISTS).
        await SnowflakeSchemaInitializer.EnsurePartitionSchemaAsync(
            ConnectionSource, schemaName, Options.VectorDimensions, Capabilities.SupportsVector,
            logger: null, ct);

        // Provision any EXTRA satellite table a custom mapping introduces (the standard ones
        // already exist; re-running their IF NOT EXISTS statements is free).
        if (partitionDef?.TableMappings is { Count: > 0 })
        {
            await using var connection = await ConnectionSource.OpenAsync(ct);
            foreach (var table in partitionDef.TableMappings.Values.Distinct(StringComparer.Ordinal))
            foreach (var sql in SnowflakeSchemaInitializer.GetSatelliteTableStatements(
                         schemaName, table, Options.VectorDimensions, Capabilities.SupportsVector))
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                await command.ExecuteNonQueryAsync(ct);
            }
        }

        // Per-schema source: same endpoint, session default schema switched. The driver pools
        // per connection string, so these are tracked and disposed between tests exactly like
        // the PG fixture's per-schema MaxPoolSize=1 data sources.
        var csb = new DbConnectionStringBuilder { ConnectionString = ConnectionString };
        csb["schema"] = schemaName;
        var schemaSource = new SnowflakeConnectionSource(csb.ConnectionString);

        // The adapter scopes table references via PartitionDefinition.Schema — default a
        // definition when the caller didn't pass one (or left its Schema null).
        var definition = partitionDef ?? new PartitionDefinition { Namespace = schemaName };
        if (string.IsNullOrEmpty(definition.Schema))
            definition = definition with { Schema = schemaName };

        var adapter = new SnowflakeStorageAdapter(
            schemaSource, partitionDefinition: definition,
            capabilities: CapabilityHolder, options: Options);
        _trackedSchemaDataSources.Add(schemaSource);
        return (schemaSource, adapter);
    }

    /// <summary>
    /// <see cref="IObservable{T}"/> projection of <see cref="CreateSchemaAdapterAsync"/>
    /// so test bodies stay void + blocking-reactive (§2a). The low-level schema DDL stays
    /// async inside; this only bridges it via the unbounded <see cref="IoPool"/>.
    /// </summary>
    public IObservable<(SnowflakeConnectionSource SchemaSource, SnowflakeStorageAdapter Adapter)>
        CreateSchemaAdapter(string schemaName, PartitionDefinition? partitionDef = null, CancellationToken ct = default)
        => IoPool.Unbounded.Invoke(token => CreateSchemaAdapterAsync(schemaName, partitionDef, token));

    /// <summary>
    /// Disposes every per-schema <see cref="SnowflakeConnectionSource"/> ever returned by
    /// <see cref="CreateSchemaAdapterAsync"/>, clearing the driver's session pools. Call
    /// between tests so leaked schema adapters don't hold HTTPS sessions open.
    /// </summary>
    public void DisposeTrackedSchemaDataSources()
    {
        while (_trackedSchemaDataSources.TryTake(out var source))
        {
            try { source.Dispose(); } catch { /* tearing down */ }
        }
    }

    /// <summary>
    /// Async-named counterpart of <see cref="DisposeTrackedSchemaDataSources"/> so PG test
    /// classes port mechanically. <see cref="SnowflakeConnectionSource"/> disposal is
    /// synchronous (a driver pool clear) — there is no async work to await here.
    /// </summary>
    public Task DisposeTrackedSchemaDataSourcesAsync()
    {
        DisposeTrackedSchemaDataSources();
        return Task.CompletedTask;
    }

    /// <summary>
    /// <see cref="IObservable{T}"/> projection of <see cref="CleanDataAsync"/> so test bodies
    /// stay void + blocking-reactive (§2a). The DELETE statements (low-level driver ops) stay
    /// async inside.
    /// </summary>
    public IObservable<Unit> CleanData()
        => IoPool.Unbounded.Invoke(async _ => { await CleanDataAsync(); return Unit.Default; });

    // Data tables wiped between tests: the union of the PG fixture's central batch
    // (partition_objects, mesh_nodes, user_effective_permissions, access_control, group_members,
    // node_type_permissions — the _shadow table is deliberately not ported to Snowflake) and its
    // per-schema list (threads, activities, user_activities, access, annotations, notifications,
    // code), plus the Snowflake-only cross-process outbox (event_log, action_cursor) — leaked
    // events would replay into the next test's change-feed poller. Immutable constant lookup.
    private static readonly string[] DataTables =
    [
        "mesh_nodes", "threads", "activities", "user_activities", "access",
        "annotations", "notifications", "code", "partition_objects",
        "user_effective_permissions", "access_control", "group_members",
        "node_type_permissions", "event_log", "action_cursor",
    ];

    /// <summary>
    /// Cleans all data tables for test isolation — the PG fixture's shape: ONE
    /// <c>information_schema</c> catalog listing resolves every (schema, data-table) pair,
    /// then batched DELETEs. The Snowflake driver executes exactly one statement per command
    /// (no multi-statement batches), so the DELETEs run sequentially over ONE connection
    /// instead of PG's single multi-statement round-trip.
    /// </summary>
    public async Task CleanDataAsync()
    {
        // Release per-schema driver sessions first so the DELETEs don't compete with
        // leaked schema adapters — mirror of the PG fixture.
        await DisposeTrackedSchemaDataSourcesAsync();

        await using var connection = await ConnectionSource.OpenAsync(CancellationToken.None);

        // The IN list is a compile-time constant of safe lowercase identifiers — inlined, not
        // bound (Snowflake.Data has no array binding). LOWER() on both sides keeps the
        // comparison robust however the endpoint case-folds catalog rows; the fixture creates
        // every schema/table double-quoted lowercase.
        var inList = string.Join(", ", DataTables.Select(t => $"'{t}'"));
        var targets = new List<(string Schema, string Table)>();
        await using (var listTables = connection.CreateCommand())
        {
            listTables.CommandText =
                $"""
                 SELECT table_schema, table_name
                 FROM information_schema.tables
                 WHERE table_type = 'BASE TABLE'
                   AND LOWER(table_schema) <> 'information_schema'
                   AND LOWER(table_name) IN ({inList})
                 """;
            await using var reader = await listTables.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                targets.Add((reader.GetString(0), reader.GetString(1)));
        }

        foreach (var (schema, table) in targets)
        {
            await using var delete = connection.CreateCommand();
            delete.CommandText = $"DELETE FROM {SnowflakeIdentifiers.Qualify(schema, table)}";
            await delete.ExecuteNonQueryAsync();
        }
    }
}
