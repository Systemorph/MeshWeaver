using Azure.Identity;
using Azure.Storage.Blobs;
using Memex.Portal.ServiceDefaults;
using Memex.Portal.Shared;
using MeshWeaver.Hosting.Embeddings;
using Microsoft.AspNetCore.DataProtection;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.ContentCollections;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Orleans;
using MeshWeaver.Hosting.PostgreSql;
using MeshWeaver.Messaging;
using MeshWeaver.NuGet;
using MeshWeaver.NuGet.AzureBlob;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Orleans.Configuration;
using Orleans.Hosting;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.Services.AddServerSideBlazor().AddCircuitOptions(o => o.DetailedErrors = true);
// Give Orleans time to drain grain activations during a rolling update.
// ACA termination grace period is set to 120 s in Memex.AppHost; this
// keeps the .NET host alive for 90 s (leaves 30 s headroom before SIGKILL).
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(90));

// Log levels controlled via appsettings.Development.json

// Deployment backend switch. Default "Azure" preserves the current ACA/Marketplace
// behaviour exactly (no regression). "Filesystem" is the Azure-free self-host path:
// object storage, the NodeType compile cache, the NuGet package cache, and
// DataProtection keys move to a (local or shared) volume. Mesh data still lives in
// Postgres in BOTH modes — the Postgres auth path below already auto-detects
// Azure-managed-identity vs basic auth from the connection string.
var deploymentBackend = builder.Configuration["Deployment:Backend"] ?? "Azure";
var useAzureBackend = !string.Equals(deploymentBackend, "Filesystem", StringComparison.OrdinalIgnoreCase);

if (useAzureBackend)
{
    // Register Aspire-injected clients
    builder.AddKeyedAzureTableServiceClient("orleans-clustering");
    builder.AddKeyedAzureBlobServiceClient("storage");
    builder.AddKeyedAzureBlobServiceClient("orleans-grain-state");
    // Shared NodeType compile cache — versioned assemblies live here, replacing the
    // per-replica in-memory compile cache with a durable cross-replica lookup.
    builder.AddKeyedAzureBlobServiceClient("nodetype-cache");

    // Persistent NuGet package cache backed by the content-storage account. Each resolved
    // package is stored as a .zip blob under container "nuget-cache" keyed by {id}/{version}.
    // On a new replica the resolver hydrates from blob instead of re-downloading from nuget.org.
    builder.Services.Replace(ServiceDescriptor.Singleton<INuGetPackageCache>(sp =>
        new BlobNuGetPackageCache(
            sp.GetRequiredKeyedService<BlobServiceClient>("storage"),
            containerName: "nuget-cache",
            logger: sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<BlobNuGetPackageCache>>(),
            // Mesh-scoped Blob pool caps blob concurrency; absent it falls back to IoPool.Unbounded.
            ioPoolRegistry: sp.GetService<MeshWeaver.Mesh.Threading.IoPoolRegistry>())));

    // Data protection: persist keys to Azure Blob Storage (shared across replicas)
    var dpConfig = builder.Configuration.GetSection("DataProtection");
    var containerName = dpConfig["ContainerName"] ?? "dataprotection";
    var blobName = dpConfig["BlobName"] ?? "keys.xml";

    builder.Services.AddDataProtection()
        .SetApplicationName("MemexPortal")
        .PersistKeysToAzureBlobStorage(sp =>
        {
            var blobServiceClient = sp.GetRequiredKeyedService<BlobServiceClient>("storage");
            var containerClient = blobServiceClient.GetBlobContainerClient(containerName);
            // Exists() probe before Create() avoids the Azure SDK's per-response
            // "409 ContainerAlreadyExists" warning that CreateIfNotExists() emits
            // on every startup against a pre-existing container.
            if (!containerClient.Exists())
                containerClient.Create();
            return containerClient.GetBlobClient(blobName);
        });
}
else
{
    // ---- Self-host filesystem backend (Azure-free) ----
    // Single-node: a local volume. HA: a shared volume (NFS/CIFS) so every replica
    // sees the same compile cache / package cache / DataProtection keys.
    var dataRoot = builder.Configuration["Deployment:DataRoot"]
        ?? Path.Combine(AppContext.BaseDirectory, "data");

    // NodeType compile cache → filesystem. Registered BEFORE ConfigureMemexMesh's
    // AddBlobAssemblyStore() runs; both use TryAddSingleton<IAssemblyStore>, so this
    // first registration wins and the blob factory (which needs a keyed BlobServiceClient
    // we deliberately don't register here) is never constructed.
    var assemblyCache = Path.Combine(dataRoot, "assembly-cache");
    builder.Services.AddFileSystemAssemblyStore(assemblyCache);

    // 🚨 ONE POD BAKES. The compile cache is shared but the decision to rebuild is per-process, so
    // without a lease every replica on a new image starts the SAME sweep over the SAME NodeTypes
    // into this SAME directory — concurrent cold compiles of one type, which is the storm the
    // sequential sweep exists to prevent. Any rollout with maxSurge hits this by default.
    // The lease lives beside the assemblies it guards, so it is shared exactly when they are.
    builder.Services.AddSingleton(new BakeCoordination(assemblyCache));

    // 🚨 …and ONE WHOLE GENERATION of that cache is written per deploy, because the store keys every
    // file by the MeshWeaver.Graph MVID and a CI build stamps a fresh InformationalVersion into
    // every assembly. That is deliberate ABI safety; what was missing is anything that ever removes
    // an old generation. Measured on memex 2026-08-12: 7817 DLLs across 93 generations, 3.2 GB — of
    // which 83 files (1%) were loadable by the running image — on the SAME 16 GiB share that holds
    // the DataProtection key ring below, so filling it takes auth-adjacent state down with it.
    //
    // This claims the generation this pod runs (the only thing that proves one is still referenced)
    // and sweeps the ones nothing runs. Deletion is OFF unless AssemblyCache__Retention__Delete is
    // explicitly true — until then it reports exactly what it would remove. See
    // AssemblyCacheGenerations for why the claim, not an age or a count, is the proof.
    builder.Services.AddAssemblyCacheRetention(builder.Configuration);

    // NuGet package cache → filesystem (zip-per-version, shared-volume safe).
    builder.Services.Replace(ServiceDescriptor.Singleton<INuGetPackageCache>(sp =>
        new FileSystemNuGetPackageCache(
            Path.Combine(dataRoot, "nuget-cache"),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<FileSystemNuGetPackageCache>>())));

    // DataProtection keys → filesystem (shared volume across replicas in HA).
    var keysDir = Path.Combine(dataRoot, "dataprotection-keys");
    Directory.CreateDirectory(keysDir);
    builder.Services.AddDataProtection()
        .SetApplicationName("MemexPortal")
        .PersistKeysToFileSystem(new DirectoryInfo(keysDir));
}

// Register Aspire-injected PostgreSQL data source (with pgvector support)
// Single shared pool for all partition queries (schema-qualified SQL).
// Pool size must handle parallel fan-out across all schemas.
var connectionString = builder.Configuration.GetConnectionString("memex") ?? "";
if (connectionString.Contains("database.azure.com"))
    builder.AddAzureNpgsqlDataSource("memex",
        configureDataSourceBuilder: dsb =>
        {
            dsb.UseVector();
            dsb.ConnectionStringBuilder.MaxPoolSize = 50;
            dsb.ConnectionStringBuilder.ConnectionIdleLifetime = 30;
        });
else
    builder.AddNpgsqlDataSource("memex",
        configureDataSourceBuilder: dsb =>
        {
            dsb.UseVector();
            dsb.ConnectionStringBuilder.MaxPoolSize = 50;
            dsb.ConnectionStringBuilder.ConnectionIdleLifetime = 30;
        });

// Disable dev login in the distributed deployment by default (prod-safety): a real
// OAuth/OIDC provider is expected. A self-host / local deployment may opt in explicitly
// with Authentication__EnableDevLogin=true (anything else still forces it off).
if (builder.Configuration["Authentication:EnableDevLogin"] != "true")
    builder.Configuration["Authentication:EnableDevLogin"] = "false";

// Add web portal services
builder.ConfigureMemexServices();

// Register embedding provider if configured. Provider="AzureFoundry" (default) = Cohere
// embed-v4 via Azure Foundry; Provider="Ollama" = local on-host /v1/embeddings (bge-m3 etc.).
// No endpoint ⇒ no provider ⇒ search falls back to ILIKE.
var embeddingOptions = builder.Configuration.GetSection("Embedding").Get<EmbeddingOptions>() ?? new EmbeddingOptions();
builder.Services.AddEmbeddings(embeddingOptions);

// Configure Orleans clustering (co-hosted silo + web).
//  - "AzureTables" (default): Aspire injects Azure Table clustering via config — no
//    explicit provider here, exactly as before (no regression for ACA/Marketplace).
//  - "Localhost": single-silo in-process membership for single-node self-host (compose
//    without an Aspire orchestrator to inject clustering config).
//  - "AdoNet" (Postgres): HA self-host — wired in Track A / compose-ha.
// Clustering provider is a deploy-time feature flag (Features:Orleans:Clustering); the
// legacy Deployment:Orleans:Clustering key is still honoured for back-compat.
var orleansClustering = builder.Configuration["Features:Orleans:Clustering"]
    ?? builder.Configuration["Deployment:Orleans:Clustering"]
    ?? "AzureTables";
var address = AddressExtensions.CreateMeshAddress();
builder.UseOrleansMeshServer(address, silo =>
    {
        silo.Configure<ClusterOptions>(opts =>
        {
            opts.ClusterId = MemexDistributedConstants.ClusterId;
            opts.ServiceId = MemexDistributedConstants.ServiceId;
        });
        // Membership-probe tolerance. EVERY portal crash on memex-cloud over 2026-07-15..22 was the
        // same self-inflicted death: a silo starved by load (boot import/compile storm, GC pauses
        // from the content-render leak) missed probes, got voted dead by its peers, and
        // Environment.FailFast'd ("I have been told I am dead") — on 2026-07-20 BOTH silos voted
        // each other dead within a second (full outage). Defaults (10s probe, 3 misses) declare a
        // busy-but-alive silo dead after ~30s of unresponsiveness. Widen to ~75s and probe
        // indirectly before condemning: a transiently starved silo recovers instead of dying; a
        // TRULY wedged pod is still restarted by the k8s liveness probe (/alive, 6×15s), so real
        // failures don't linger — we only stop killing pods that were about to recover.
        silo.Configure<ClusterMembershipOptions>(opts =>
        {
            opts.ProbeTimeout = TimeSpan.FromSeconds(15);
            opts.NumMissedProbesLimit = 5;
            opts.EnableIndirectProbes = true;
        });
        if (string.Equals(orleansClustering, "Localhost", StringComparison.OrdinalIgnoreCase))
        {
            silo.UseLocalhostClustering();
        }
        else if (string.Equals(orleansClustering, "AdoNet", StringComparison.OrdinalIgnoreCase))
        {
            // Real, Postgres-backed cluster membership (self-host / HA). The `orleans`
            // database and its connection string are declared in the Aspire AppHost and
            // injected as ConnectionStrings:orleans; the db-migration creates the Orleans
            // membership tables. (AzureTables — the ACA path — is configured by the Aspire
            // Orleans integration via WithReference(orleans), so it needs no explicit call.)
            var orleansConnectionString = builder.Configuration.GetConnectionString("orleans")
                ?? throw new InvalidOperationException(
                    "Features:Orleans:Clustering=AdoNet but ConnectionStrings:orleans is not set. " +
                    "The Aspire AppHost must add an 'orleans' database and WithReference it on the portal.");
            if (!System.Data.Common.DbProviderFactories.GetProviderInvariantNames().Contains("Npgsql"))
                System.Data.Common.DbProviderFactories.RegisterFactory("Npgsql", Npgsql.NpgsqlFactory.Instance);
            silo.UseAdoNetClustering(o =>
            {
                o.Invariant = "Npgsql";
                o.ConnectionString = orleansConnectionString;
            });
        }
        return silo;
    }
    )
    .ConfigureServices(services => services
        .AddPartitionedPostgreSqlPersistence(
            configureDataSource: connectionString.Contains("database.azure.com")
                ? dsb =>
                {
                    // In an AKS pod only the SERVER-SIDE credentials exist (Environment,
                    // Workload Identity, Managed Identity). Excluding the dev-machine
                    // credentials stops DefaultAzureCredential from probing — and dumping a
                    // ~30-line CredentialUnavailableException stack trace for — Azure CLI /
                    // PowerShell / azd / Visual Studio on every token acquisition (the
                    // credential-chain log noise on the portal pods). The credential that
                    // actually succeeds (Workload/Managed Identity) is unchanged.
                    var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
                    {
                        ExcludeAzureCliCredential = true,
                        ExcludeAzurePowerShellCredential = true,
                        ExcludeAzureDeveloperCliCredential = true,
                        ExcludeVisualStudioCredential = true,
                        ExcludeInteractiveBrowserCredential = true,
                    });
                    dsb.UsePeriodicPasswordProvider(async (_, ct) =>
                    {
                        var token = await credential.GetTokenAsync(
                            new Azure.Core.TokenRequestContext(["https://ossrdbms-aad.database.windows.net/.default"]), ct);
                        return token.Token;
                    }, TimeSpan.FromMinutes(4), TimeSpan.FromSeconds(10));
                }
                : null))
    .ConfigureMemexMesh(builder.Configuration, builder.Environment.IsDevelopment())
    .ConfigureMemexPortal()
    // 🚨 Register the "storage" SOURCE collection at mesh level — the backing store that every
    // per-node MapContentCollection("x", "storage", …) mapping resolves against
    // (ContentService.ResolveMappedConfig looks the source up on the parent content service and
    // returns NULL when it is absent, so the mapped collection silently reports
    // "collection 'x' not found"). The Monolith has always registered this from the same
    // `Storage` config section (Memex.Portal.Monolith/Program.cs); the Distributed portal never
    // did — so on memex EVERY mapped per-node collection was dead: ReinsuranceDemo/Setup's
    // packs zip (the demo could not be imported at all), Reinsurance/Cedent + Underwriting/
    // Submission `content`, and Claims/Claim `files`. The AKS values already supply
    // Storage__SourceType=FileSystem + Storage__BasePath=/mnt/content, so this reads config that
    // is deployed today. Same shape as the Monolith: a read-only static backing store, hidden
    // from children (IsEditable / ExposeInChildren stay false — the per-node MAPPING is the
    // writable view).
    .ConfigureHub(hub =>
    {
        var storageConfig = builder.Configuration.GetSection("Storage").Get<ContentCollectionConfig>();
        return storageConfig is null
            ? hub
            // Force the well-known SOURCE name "storage" — NOT storageConfig.Name. The `Storage`
            // section's Name is already spoken for: ConfigureMemexMesh uses this same section as
            // the per-node content-collection root, and on AKS it is set to "content"
            // (values.aks.yaml Storage__Name), which is what makes @{node}/content/… work. The
            // mapped-collection SOURCE that every plugin references is a different, mesh-level
            // thing and is always called "storage" (the Monolith's appsettings names it so).
            // Registering it under the configured name would both shadow the per-node collection
            // and still leave MapContentCollection(…, "storage", …) unresolved.
            //
            // 🚨 IsStatic stays FALSE (issue #587): this mesh-level store holds EVERY partition's
            // content, and publishing it by URL was the reported hole. The per-node collections
            // mapped over it are what get published — each owned by a node, gated on Read of it.
            : hub.AddContentCollection(_ => storageConfig with { Name = "storage" });
    });

// Hard gate: refuse to start if the DB isn't migrated. Aspire's
// WaitForCompletion(dbMigration) is a soft hint at deploy time — Container
// Apps schedule the portal independently, so a crashed migration silently
// lets the portal come up against a half-migrated DB. The startup gate
// trips IHostApplicationLifetime.StopApplication, which causes the host to
// exit and Container Apps to mark the revision as Failed — that's the
// signal tools/deploy.sh polls for to fail the pipeline.
builder.Services.AddHostedService<Memex.Portal.Distributed.DbVersionGate>();
// Live healthcheck for the same condition — surfaces drift after startup
// (e.g. someone manually rolled a partial migration via psql).
builder.Services.AddHealthChecks()
    .AddCheck<Memex.Portal.Distributed.DbVersionHealthCheck>("db_version");

// Front-load dynamic NodeType compiles at startup so a fresh pod (every image roll /
// self-update spins one up) doesn't make the first visitor of each type wait out a cold
// Roslyn compile. The sweep is sequential, in dependency order, and RESUMES from the shared
// assembly cache — types already baked for this framework are skipped, so a second replica
// (or a restart) inherits the first pod's work instead of repeating it.
builder.Services.AddDynamicTypePreWarming();

// 🚦 "Fail before prod, not in prod." Opt-in (PreWarm:GateReadiness) gate that holds /health
// RED until this pod's NodeTypes are built against ITS image. Combined with the deployment's
// startupProbe on /health and maxUnavailable:0, a NodeType that regressed on the new image
// stalls the ROLLOUT — the new pod never takes traffic and the previous image keeps serving —
// instead of surfacing as user-facing errors after the switch.
//
// Registered only when explicitly enabled: a gate that can withhold readiness should be an
// intentional deployment choice, not something a self-host inherits by accident. It also
// REQUIRES a startupProbe budget large enough for a full cold bake (see values.aks.yaml) —
// without that, Kubernetes kills the pod mid-bake and it never converges.
//
// 🚨 ONE flag decides BOTH the health-check registration and what the pre-warmer is allowed to
// claim. They were previously independent: the state registered unconditionally while the check
// registered on the flag, so a regression logged "REFUSING READINESS — the rollout will stall"
// on a pod with nothing gating it, which then went Ready and served traffic. Deriving both from
// this single parse is what keeps the log honest — see NodeTypeBakeGateState.GatesReadiness.
var gateBake = bool.TryParse(builder.Configuration[NodeTypeBakeGateExtensions.EnabledConfigKey],
    out var parsedGateBake) && parsedGateBake;
var bakeSweepEnabled =
    bool.TryParse(builder.Configuration[DynamicTypePreWarmerHostedService.EnabledConfigKey], out var s) && s;

// Operator escape hatch: serve even when the sweep ERRORED and therefore proved nothing
// (BakePhase.Faulted). Default off — an unproven bake is not a passed bake, which is the guard the
// retired pre-run bake Job used to enforce from outside as Bake:AllowEmpty. It never relaxes a real
// regression, and it never rewrites the recorded state; only the readiness verdict.
var allowUnprovenBake = bool.TryParse(
    builder.Configuration[NodeTypeBakeGateExtensions.AllowUnprovenBakeConfigKey],
    out var parsedAllowUnproven) && parsedAllowUnproven;

// Shared bake state the sweep writes and the readiness gate below reads. Always registered — the
// diagnostics are collected either way; only ENFORCEMENT is opt-in. Passing the flag is what lets
// the sweep report an UNARMED regression honestly instead of claiming a stall nothing enforces.
builder.Services.AddNodeTypeBakeGate(gateBake, allowUnprovenBake);

if (gateBake)
    builder.Services.AddHealthChecks()
        .AddCheck<Memex.Portal.Distributed.NodeTypeBakeHealthCheck>("nodetype_bake");

var app = builder.Build();

// 🚨 The two PreWarm keys are ONE setting. The gate reads bake state that only the SWEEP writes,
// and the health check reports Healthy while the bake is NotStarted — deliberately, so a config
// mistake can never black-hole a pod forever. The consequence is that GateReadiness=true with
// DynamicTypes=false yields a gate that is registered, permanently green, and protects nothing.
// That is precisely the failure this gate exists to prevent, so it must not be the quiet outcome:
// say it at Critical, where a deployment that believes it is protected will actually see it.
if (gateBake && !bakeSweepEnabled)
    app.Services.GetRequiredService<ILoggerFactory>()
        .CreateLogger("MeshWeaver.Hosting.NodeTypeBakeGate")
        .LogCritical(
            "NodeType bake gate is ENABLED ({GateKey}=true) but the pre-warm sweep is OFF "
            + "({SweepKey}!=true). The gate has nothing to measure, so it will report healthy on "
            + "every rollout and protect NOTHING. Enable the sweep, or turn the gate off so the "
            + "configuration stops claiming a protection that is not there.",
            NodeTypeBakeGateExtensions.EnabledConfigKey,
            DynamicTypePreWarmerHostedService.EnabledConfigKey);

// The escape hatch is a HOLE in an armed gate — small and deliberate, but a hole. Say so at boot,
// so "the gate is on" and "the gate still refuses an unproven bake" cannot drift apart silently in
// an operator's head. A flag that quietly weakens a safety gate is how the unarmed-gate incident
// happened in the first place.
if (gateBake && allowUnprovenBake)
    app.Services.GetRequiredService<ILoggerFactory>()
        .CreateLogger("MeshWeaver.Hosting.NodeTypeBakeGate")
        .LogWarning(
            "NodeType bake gate is ARMED ({GateKey}=true) but {AllowKey}=true, so a sweep that "
            + "ERRORS will still be served. Real regressions are unaffected and still stall the "
            + "rollout; what is waived is the refusal to certify a bake that never ran. Clear "
            + "{AllowKey} once whatever prevents the sweep from completing is fixed.",
            NodeTypeBakeGateExtensions.EnabledConfigKey,
            NodeTypeBakeGateExtensions.AllowUnprovenBakeConfigKey,
            NodeTypeBakeGateExtensions.AllowUnprovenBakeConfigKey);

app.MapDefaultEndpoints();
app.StartMemexApplication<Memex.Portal.Shared.App>();

