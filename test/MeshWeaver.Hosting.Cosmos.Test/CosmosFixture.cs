using System.Threading;
using System.Threading.Tasks;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Azure.Cosmos;
using Xunit;

namespace MeshWeaver.Hosting.Cosmos.Test;

/// <summary>
/// Shared fixture over a Cosmos DB endpoint — the Cosmos port of <c>SnowflakeFixture</c>
/// (same public surface, renamed types). The endpoint is OPTIONAL and gated:
/// <list type="bullet">
///   <item><c>COSMOS_CONNECTION</c> (a full Cosmos connection string) set → run against that
///     endpoint directly (real-account nightly path); no container.</item>
///   <item>otherwise → start the Cosmos emulator via Testcontainers.</item>
///   <item>on container start / first-connection failure → <see cref="Available"/> stays
///     <c>false</c> and every test green-skips via <see cref="SkipUnlessAvailable"/>, so CI
///     without a working Docker never goes red here.</item>
/// </list>
///
/// <para>
/// 🚨 The image is the <c>vnext-preview</c> emulator, NOT the classic
/// <c>azure-cosmos-emulator:latest</c>, and that choice is load-bearing:
/// </para>
/// <list type="bullet">
///   <item><b>Architecture</b> — the classic image publishes a linux/amd64 manifest ONLY, so it
///     cannot run natively on an arm64 developer machine. vnext ships amd64 + arm64.</item>
///   <item><b>Protocol</b> — vnext serves the gateway over plain HTTP
///     (<c>/status</c> reports <c>"protocol": "http"</c>), so there is no self-signed-certificate
///     import and no TLS bypass to configure. The classic emulator's HTTPS-only endpoint is what
///     made it "heavy to set up on a runner".</item>
///   <item><b>Weight</b> — vnext is ~0.68 GB and reports ready in ~10 s, against the classic
///     emulator's multi-GB image and minutes-long startup. That fits the shard's per-project
///     <c>timeout 8m</c> with room to spare.</item>
/// </list>
///
/// <para>
/// This is why the fixture drives a generic <see cref="ContainerBuilder"/> rather than the
/// <c>Testcontainers.CosmosDb</c> module: that module wraps the CLASSIC emulator (amd64-only,
/// HTTPS, its own wait strategy), and every one of its defaults would have to be overridden.
/// The generic builder also matches the in-repo precedent set by <c>SnowflakeFixture</c>.
/// </para>
/// </summary>
public class CosmosFixture : IAsyncLifetime
{
    /// <summary>The emulator's Cosmos gateway port (the SDK talks to this one).</summary>
    private const ushort GatewayPort = 8081;

    /// <summary>
    /// The emulator's dedicated health-probe port (<c>/alive</c>, <c>/ready</c>, <c>/status</c>).
    ///
    /// <para>
    /// 🚨 Published for diagnostics, but deliberately NOT used as the wait strategy: these probes
    /// LIE about readiness. Measured on this image, <c>/ready</c> returns 200 and <c>/status</c>
    /// reports <c>{"postgres":"healthy","gateway":"healthy","ready":true}</c> at ~2.1 s, while the
    /// data plane still rejects writes with <c>503 — "pgcosmos extension is still starting"</c>.
    /// They probe HTTP/TCP reachability, not extension load. Waiting on them produced exactly one
    /// failure mode: the fixture caught the 503 and green-SKIPPED every endpoint test, i.e. the
    /// silent no-coverage pass this whole change exists to eliminate.
    /// </para>
    /// </summary>
    private const ushort HealthPort = 8080;

    /// <summary>
    /// The emulator's own authoritative startup line, and the actual wait strategy. It names the
    /// pgcosmos extension specifically — the component the health probes miss — and lands ~1 s
    /// after <c>/ready</c> flips (~3.1 s from container start, measured).
    /// </summary>
    private const string PgCosmosReadyMarker = "PostgreSQL and pgcosmos extension are ready";

    /// <summary>
    /// The emulator's well-known fixed account key — published by Microsoft and identical on every
    /// emulator instance. NOT a credential: it grants access to a throwaway local container only.
    /// </summary>
    private const string EmulatorAccountKey =
        "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";

    private const string DefaultImage =
        "mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-preview";

    /// <summary>Partition key path of the nodes container (mirrors the production layout).</summary>
    public const string NodesContainerName = "nodes";
    public const string PartitionsContainerName = "partitions";
    public const string LeasesContainerName = "leases";

    private IContainer? _container;
    private CosmosClient? _client;
    private Database? _database;
    private string? _connectionString;

    /// <summary>Whether a Cosmos endpoint (emulator or real account) is up and initialized.</summary>
    public bool Available { get; private set; }

    /// <summary>Why <see cref="Available"/> is false — the skip reason surfaced to xunit.</summary>
    public string? UnavailableReason { get; private set; }

    /// <summary>The shared SDK client against the endpoint.</summary>
    public CosmosClient Client => _client ?? throw Unavailable();

    /// <summary>The per-run test database (dropped on dispose).</summary>
    public Database Database => _database ?? throw Unavailable();

    /// <summary>The endpoint's Cosmos connection string.</summary>
    public string ConnectionString => _connectionString ?? throw Unavailable();

    /// <summary>The nodes container, partitioned by <c>/namespace</c>.</summary>
    public Container Nodes => Database.GetContainer(NodesContainerName);

    /// <summary>The partitions container, partitioned by <c>/partitionKey</c>.</summary>
    public Container Partitions => Database.GetContainer(PartitionsContainerName);

    /// <summary>The change-feed lease container, partitioned by <c>/id</c>.</summary>
    public Container Leases => Database.GetContainer(LeasesContainerName);

    private InvalidOperationException Unavailable()
        => new(UnavailableReason
               ?? "Cosmos endpoint unavailable — tests must call SkipUnlessAvailable() first.");

    /// <summary>Dynamic xunit-v3 skip when no endpoint is available (Docker missing, image pull failed…).</summary>
    public void SkipUnlessAvailable()
        => Assert.SkipWhen(!Available, UnavailableReason ?? "Cosmos endpoint unavailable");

    public async ValueTask InitializeAsync()
    {
        // Real-account nightly path: a full connection string bypasses the container entirely.
        // Failures here are deliberately LOUD (no green-skip): a misconfigured nightly account
        // should turn the run red, not silently skip every Cosmos test.
        var external = Environment.GetEnvironmentVariable("COSMOS_CONNECTION");
        if (!string.IsNullOrWhiteSpace(external))
        {
            _connectionString = external;
            _client = new CosmosClient(external, BuildClientOptions());
            await InitializeEndpointAsync();
            Available = true;
            return;
        }

        try
        {
            // Overridable so CI can pin a validated tag/digest for reproducible bisection.
            var image = Environment.GetEnvironmentVariable("COSMOS_EMULATOR_IMAGE") ?? DefaultImage;
            _container = new ContainerBuilder(image)
                .WithPortBinding(GatewayPort, assignRandomHostPort: true)
                .WithPortBinding(HealthPort, assignRandomHostPort: true)
                .WithWaitStrategy(Wait.ForUnixContainer()
                    .UntilMessageIsLogged(PgCosmosReadyMarker))
                .Build();
            await _container.StartAsync();

            // Plain HTTP — see the class remarks. LimitToEndpoint (below) keeps the SDK on this
            // mapped host port instead of following the address the gateway advertises for itself,
            // which is the container-internal one and unreachable from the test process.
            _connectionString =
                $"AccountEndpoint=http://{_container.Hostname}:{_container.GetMappedPublicPort(GatewayPort)}/;" +
                $"AccountKey={EmulatorAccountKey}";
            _client = new CosmosClient(_connectionString, BuildClientOptions());
            await InitializeEndpointAsync();
            Available = true;
        }
        catch (Exception ex)
        {
            // Do NOT fail the fixture: CI without Docker (or with a broken emulator image) must
            // stay green-skipped. The exception message becomes the skip reason.
            Available = false;
            UnavailableReason = ex.Message;
            _client?.Dispose();
            _client = null;
            if (_container is not null)
            {
                try { await _container.DisposeAsync(); } catch { /* tearing down a failed start */ }
                _container = null;
            }
        }
    }

    /// <summary>
    /// 🚨 Built from <see cref="CosmosSerialization.CreateClientOptions"/> — the SAME storage
    /// contract the production factory uses — so these tests exercise production's document shape
    /// rather than a fixture-local one. Constructing a bare <c>new CosmosClient(...)</c> here is
    /// what let the missing camelCase policy (400 "Document does not contain an id field") go
    /// unnoticed in the first place.
    ///
    /// <para>
    /// Gateway mode + <c>LimitToEndpoint</c> are emulator-specific and layered on top: the gateway
    /// advertises its container-internal address for direct mode, which the test process cannot
    /// route to.
    /// </para>
    /// </summary>
    private static CosmosClientOptions BuildClientOptions()
        => CosmosSerialization.CreateClientOptions(o =>
        {
            o.ConnectionMode = ConnectionMode.Gateway;
            o.LimitToEndpoint = true;
            o.RequestTimeout = TimeSpan.FromSeconds(30);
        });

    /// <summary>
    /// Endpoint init: a per-run database plus the standard container set. The database name is
    /// unique per fixture instance so a crashed previous run (whose container Testcontainers
    /// reaped, or whose real-account database was left behind) cannot leak state into this one.
    /// </summary>
    private async Task InitializeEndpointAsync()
    {
        var ct = CancellationToken.None;

        // Also the first real round-trip — an unreachable endpoint fails here, not at first use.
        var databaseName = $"MeshWeaverTest_{Guid.NewGuid():N}";
        _database = (await _client!.CreateDatabaseIfNotExistsAsync(databaseName, cancellationToken: ct)).Database;

        await _database.CreateContainerIfNotExistsAsync(
            new ContainerProperties(NodesContainerName, "/namespace"), cancellationToken: ct);
        await _database.CreateContainerIfNotExistsAsync(
            new ContainerProperties(PartitionsContainerName, "/partitionKey"), cancellationToken: ct);
        await _database.CreateContainerIfNotExistsAsync(
            new ContainerProperties(LeasesContainerName, "/id"), cancellationToken: ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_database is not null)
        {
            // Best-effort: on the container path the whole endpoint is about to be destroyed
            // anyway; this only matters for the real-account path, where a leaked per-run
            // database would accrue cost.
            try { await _database.DeleteAsync(); } catch { /* tearing down */ }
        }

        _client?.Dispose();
        if (_container is not null)
            await _container.DisposeAsync();
    }
}
