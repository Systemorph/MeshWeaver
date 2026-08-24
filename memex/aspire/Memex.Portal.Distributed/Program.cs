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
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.NuGet;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Orleans.Configuration;
using Orleans.Hosting;

using MeshWeaver.Compiler;
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
    // 🚨 The implementation RELOCATED to the MeshWeaver.Azure.Blob MODULE ("move all the Azure
    // stuff to modules", 2026-08-20). The app keeps the DECISION — this is the Azure branch — and
    // reaches the type by probe-and-delegate at first resolve, when module assemblies are
    // certainly loaded. throwOnError: an Azure-backend deployment without the AzureBlob module is
    // a misconfiguration that must fail NAMING the module, not quietly re-download from nuget.org.
    builder.Services.Replace(ServiceDescriptor.Singleton<INuGetPackageCache>(sp =>
    {
        var type = Type.GetType(
            "MeshWeaver.Azure.Blob.BlobNuGetPackageCache, MeshWeaver.Azure.Blob",
            throwOnError: false)
            ?? throw new InvalidOperationException(
                "Deployment:Backend is Azure but the MeshWeaver.Azure.Blob module is not landed — "
                + "the blob NuGet package cache lives there. Land the AzureBlob package.");
        return (INuGetPackageCache)Activator.CreateInstance(type,
            sp.GetRequiredKeyedService<BlobServiceClient>("storage"),
            "nuget-cache",
            sp.GetRequiredService(typeof(Microsoft.Extensions.Logging.ILogger<>).MakeGenericType(type)),
            // Mesh-scoped Blob pool caps blob concurrency; absent it falls back to IoPool.Unbounded.
            sp.GetService<MeshWeaver.Mesh.Threading.IoPoolRegistry>())!;
    }));

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

    // 🚨 ONE POD BAKES — coordinated by the build protocol (Doc/Architecture/BuildCoordination):
    // the Admin/Build claim decides who runs the sweep and every other pod completes on the
    // per-fingerprint GO subscription. Nothing to register here — the protocol is the pre-warmer's
    // default. (A file lease beside the assembly cache used to serialise this; it is deleted, its
    // one-builder and steal-on-stale properties carried by the claim arbiter.)

    // 🚨 …and ONE WHOLE GENERATION of that cache is written per DEPLOYED COMMIT, because the store
    // keys every file by the framework identity (the commit for CI builds — #1660 WS3; images of
    // the SAME commit now share a generation, and the CI bake pre-fills it). That is deliberate
    // ABI safety; what was missing is anything that ever removes an old generation. Measured on
    // memex 2026-08-12 (when every BUILD was its own generation): 7817 DLLs across 93 generations, 3.2 GB — of
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
// Select the Entra-ID token path ONLY for an Azure host with NO password. A fully-qualified Azure
// host reached with username+password must take the plain Npgsql path — AddAzureNpgsqlDataSource
// wires a token password provider, and Npgsql throws NotSupportedException on connect when a
// password is ALSO present, which SIGABRTs the portal via PostgreSqlChangeListener. (Was a
// substring match on the whole string, which both false-matches and ignores the password.)
if (AzurePostgres.UsesManagedIdentityAuth(connectionString))
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
            // Azure Flexible Server requires SSL; enforce it on the password path too so a config
            // string without an explicit SslMode still connects (28000: no pg_hba.conf entry …).
            if (AzurePostgres.IsAzureHost(connectionString))
                dsb.ConnectionStringBuilder.SslMode = SslMode.Require;
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

// 🔥 BAKE MODE (Deployment:Mode=Bake): this SAME binary — and therefore the SAME image, the same
// Graph MVID, the same framework fingerprint — runs as an ephemeral build master instead of a
// serving portal. It joins NO live cluster (own ServiceId + localhost clustering below), runs the
// protocol-coordinated sweep against the shared stores (Postgres, /data assembly cache, source
// replica), publishes the per-fingerprint GO on Admin/Build, and EXITS (BakeModeCompletion).
// Serving pods then find the share full and their GO already published.
//
// The retired 2025 bake image failed precisely because it was a DIFFERENT build with a foreign
// fingerprint (#1347); same-image-different-mode is what makes its bakes valid by construction.
// Exiting after GO also disposes every sync hub and collectible ALC the compiles minted — the
// bake's memory cost (measured +1.9 GB managed for a full sweep) dies with the process.
var bakeMode = string.Equals(
    builder.Configuration["Deployment:Mode"], "Bake", StringComparison.OrdinalIgnoreCase);
if (bakeMode)
{
    // The bake posture, forced regardless of the deployment's serving config: the sweep IS the
    // job, batch direct-compile (nothing to be gentle to), and no readiness gate — a Job has no
    // rotation to be held out of; its verdict is its EXIT CODE.
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
    {
        [DynamicTypePreWarmerHostedService.EnabledConfigKey] = "true",
        [DynamicTypePreWarmerHostedService.BatchBakeConfigKey] = "true",
        [NodeTypeBakeGateExtensions.EnabledConfigKey] = "false",
    });
}

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

// 🚨 AdoNet clustering is the MULTI-SILO shape, and multi-silo is precisely when the streaming
// pub-sub subscription registry may not be in-memory (issue #1729). Derived from the clustering
// provider rather than exposed as its own knob, so the two can never drift apart: a deployment
// that turned on real clustering has, by that single act, also made its reply path survivable.
//
// What goes wrong without it: every cross-silo delivery to a stream-routed hub — mesh/, portal/,
// client/, cache/, import/, and therefore every REPLY to one — is published to an Orleans memory
// stream. The pulling agent asks PubSubStore who is subscribed; with AddMemoryGrainStorage that
// answer lives in the RAM of whichever silo happened to host the PubSubRendezvousGrain, so when
// that silo departs (EVERY rolling deploy overlaps two silos and then drops one) the answer
// becomes "nobody" — permanently, for that stream. The consumer's handle stays valid and silent,
// the publish still reports success, and the message is discarded with nothing logged anywhere.
// On memex-cloud that surfaced as one replica serving /api/content in 6–57 ms while the other ran
// out its entire 60 s reply budget, deterministically, across several image rolls.
//
// Postgres — the same `orleans` database that already holds cluster membership — makes the
// registry outlive any single silo, so the surviving consumer's subscription is still there after
// the departure. The migration creates the Orleans persistence tables next to the membership ones.
var useAdoNetClustering = !bakeMode
    && string.Equals(orleansClustering, "AdoNet", StringComparison.OrdinalIgnoreCase);
string? orleansConnectionString = null;
if (useAdoNetClustering)
{
    // The `orleans` database and its connection string are declared in the Aspire AppHost and
    // injected as ConnectionStrings:orleans; the db-migration creates the Orleans membership AND
    // persistence tables. (AzureTables — the ACA path — is configured by the Aspire Orleans
    // integration via WithReference(orleans), so it needs no explicit call here.)
    orleansConnectionString = builder.Configuration.GetConnectionString("orleans")
        ?? throw new InvalidOperationException(
            "Features:Orleans:Clustering=AdoNet but ConnectionStrings:orleans is not set. " +
            "The Aspire AppHost must add an 'orleans' database and WithReference it on the portal.");
    if (!System.Data.Common.DbProviderFactories.GetProviderInvariantNames().Contains("Npgsql"))
        System.Data.Common.DbProviderFactories.RegisterFactory("Npgsql", Npgsql.NpgsqlFactory.Instance);
}

// Null ⇒ ConfigureMeshWeaverServer keeps the in-memory store, which is correct ONLY for a cluster
// of one: bake mode (localhost clustering by construction), Localhost clustering, and the
// single-process Monolith.
var configurePubSubStore = useAdoNetClustering
    ? new Action<ISiloBuilder>(silo => silo.AddAdoNetGrainStorage(StreamProviders.PubSubStore, o =>
    {
        o.Invariant = "Npgsql";
        o.ConnectionString = orleansConnectionString!;
    }))
    : null;

// How the PARTITIONED persistence provider (and the change listener it spins up via the same
// hook — see PostgreSqlPartitionStorageProvider.CreateChangeListenerDataSource) authenticates.
// The Entra-ID token provider is wired ONLY for an Azure host with NO password; wiring it when a
// password is present throws NotSupportedException on connect and SIGABRTs the portal. On any
// Azure host, SSL is forced (Flexible Server requires it) whichever auth path we take.
Action<NpgsqlDataSourceBuilder>? configurePersistenceDataSource;
if (AzurePostgres.UsesManagedIdentityAuth(connectionString))
    configurePersistenceDataSource = dsb =>
    {
        dsb.ConnectionStringBuilder.SslMode = SslMode.Require;
        // In an AKS pod only the SERVER-SIDE credentials exist (Environment, Workload Identity,
        // Managed Identity). Excluding the dev-machine credentials stops DefaultAzureCredential
        // from probing — and dumping a ~30-line CredentialUnavailableException stack trace for —
        // Azure CLI / PowerShell / azd / Visual Studio on every token acquisition (the
        // credential-chain log noise on the portal pods). The credential that actually succeeds
        // (Workload/Managed Identity) is unchanged.
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
    };
else if (AzurePostgres.IsAzureHost(connectionString))
    // Azure host reached with a password: no token provider (it would throw), but still force SSL.
    configurePersistenceDataSource = dsb => dsb.ConnectionStringBuilder.SslMode = SslMode.Require;
else
    configurePersistenceDataSource = null;

var address = AddressExtensions.CreateMeshAddress();
builder.UseOrleansMeshServer(address, silo =>
    {
        silo.Configure<ClusterOptions>(opts =>
        {
            // Bake mode gets its OWN service identity: bake grains instantiate in the bake
            // cluster because it is the only cluster they exist in, and no discovery round-trip
            // can land on (or starve against) a serving pod's activations — the #1218 class is
            // unrepresentable rather than mitigated. Membership state is keyed by these ids, so
            // the live cluster's tables are never touched.
            opts.ClusterId = bakeMode
                ? $"{MemexDistributedConstants.ClusterId}-bake"
                : MemexDistributedConstants.ClusterId;
            opts.ServiceId = bakeMode
                ? $"{MemexDistributedConstants.ServiceId}-bake"
                : MemexDistributedConstants.ServiceId;
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
        if (bakeMode)
        {
            // A bake silo is a cluster of ONE by design — it must never join (or even see) the
            // serving membership, whatever provider the deployment configured. Localhost
            // clustering is that isolation: no membership store, no gossip, no probes.
            silo.UseLocalhostClustering();
        }
        else if (string.Equals(orleansClustering, "Localhost", StringComparison.OrdinalIgnoreCase))
        {
            silo.UseLocalhostClustering();
        }
        else if (useAdoNetClustering)
        {
            // Real, Postgres-backed cluster membership (self-host / HA). Connection string and
            // Npgsql provider-factory registration are hoisted above, because the streaming
            // pub-sub store derives from the SAME decision and is configured before this lambda.
            silo.UseAdoNetClustering(o =>
            {
                o.Invariant = "Npgsql";
                o.ConnectionString = orleansConnectionString!;
            });
        }
        return silo;
    },
    configurePubSubStore
    )
    .ConfigureServices(services => services
        .AddPartitionedPostgreSqlPersistence(
            configureDataSource: configurePersistenceDataSource))
    .ConfigureMemexMesh(builder.Configuration, builder.Environment.IsDevelopment())
    .ConfigureMemexPortal(builder.Configuration)
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

// Modules that LANDED but have not LOADED (#1979). Loading is restart-as-activation, which makes
// the restart part of the install — so an install whose last step is invisible reads as a broken
// install. DEGRADED, never Unhealthy: the pod serves correctly with what it loaded, and failing
// readiness would stall a rollout over work the rollout itself performs. Reads the PERSISTED
// sidecar, because the process that landed the module is not the process being asked.
builder.Services.AddHealthChecks()
    .AddCheck(
        "pending_module_activation",
        new Memex.Portal.Distributed.PendingModuleActivationHealthCheck(
            MeshWeaver.PluginCatalog.ModuleRoot.Resolve(builder.Configuration)));

// Is this instance ADOPTING the assemblies the registry is meant to serve it, or quietly compiling
// them itself (#1782 gap 4)? With instance-level pre-bake giving way to lazy compile-on-access, a
// fetch miss is absorbed so completely that the whole distribution lane can go dark while every
// surface looks like a healthy day (2026-08-20). DEGRADED on a miss, never Unhealthy: compiling is
// correct behaviour, and failing readiness would turn a distribution regression into an outage.
builder.Services.AddHealthChecks()
    // 🚨 Unhealthy when a declared-required module is missing, so a rollout that would silently
    // drop a feature STALLS and the pods that still have it keep serving. Inert unless the
    // deployment declares Modules:Required.
    .AddCheck("required_modules",
        new Memex.Portal.Distributed.RequiredModulesHealthCheck(builder.Configuration))
    .AddCheck<Memex.Portal.Distributed.BundleAdoptionHealthCheck>("bundle_adoption")
    // 🚨 The other half of the same blindness (#1782 gap 2). Adoption's miss is invisible because
    // a lazy compile absorbs it; an entitlement answer's degradation is invisible because every
    // refusal is byte-identical on the wire by design. DEGRADED, never Unhealthy: serving a
    // previously observed entitlement while the registry is unreachable is the CORRECT answer, and
    // failing readiness over it would turn a brief registry outage into one of ours.
    .AddCheck<Memex.Portal.Distributed.EntitlementAnchorHealthCheck>("entitlement_anchor");

// The same shape, one dependency further out: DbVersionGate proves the MESH database is
// migrated; this proves the ORLEANS database is provisioned. They are different databases,
// provisioned by different phases, and #1798 is what happens when only the first is checked —
// the portal held a valid ConnectionStrings:orleans (so the existing throw above was satisfied)
// while the MIGRATION's secret lacked it, so OrleansClusteringSetup logged "skipping" and created
// nothing. AdoNetGrainStorage.Init then died on `Sequence contains no elements`, which names no
// table, key, or container. Registered ONLY when this silo actually uses AdoNet, and asking for
// exactly the keys `useAdoNetClustering` causes it to configure, so the gate can never demand
// more than the deployment uses.
if (useAdoNetClustering)
    builder.Services.AddHostedService(sp => new Memex.Portal.Distributed.OrleansProvisioningGate(
        orleansConnectionString!,
        requiresGrainStorage: configurePubSubStore is not null,
        sp.GetRequiredService<IHostApplicationLifetime>(),
        sp.GetRequiredService<ILogger<Memex.Portal.Distributed.OrleansProvisioningGate>>()));

// Front-load dynamic NodeType compiles at startup so a fresh pod (every image roll /
// self-update spins one up) doesn't make the first visitor of each type wait out a cold
// Roslyn compile. The sweep is sequential, in dependency order, and RESUMES from the shared
// assembly cache — types already baked for this framework are skipped, so a second replica
// (or a restart) inherits the first pod's work instead of repeating it.
builder.Services.AddDynamicTypePreWarming();

if (bakeMode)
{
    // The sweep settling is this process's whole purpose — settle ⇒ exit, exit code = verdict.
    builder.Services.AddHostedService<Memex.Portal.Distributed.BakeModeCompletion>();
    // A bake Job must never act as a fleet manager: the self-updater patches the DEPLOYMENT'S
    // image on its startup poll, and a short-lived Job doing that mid-roll is exactly the kind of
    // surprise a build process must not spring. Remove the hosted service rather than flag it —
    // there is no configuration in which a bake Job should self-update anything.
    foreach (var descriptor in builder.Services
                 .Where(d => d.ServiceType == typeof(IHostedService)
                     && d.ImplementationType == typeof(Memex.Portal.Shared.SelfUpdate.SelfUpdateHostedService))
                 .ToList())
        builder.Services.Remove(descriptor);
}

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
var gateBakeConfigured = bool.TryParse(builder.Configuration[NodeTypeBakeGateExtensions.EnabledConfigKey],
    out var parsedGateBake) && parsedGateBake;
var bakeSweepEnabled =
    bool.TryParse(builder.Configuration[DynamicTypePreWarmerHostedService.EnabledConfigKey], out var s) && s;

// 🚨 A GATE THAT CANNOT MEASURE DOES NOT ARM (#1981). The gate reads bake state only the SWEEP
// writes, so gate-without-sweep is registered, permanently green, and protects nothing — the exact
// failure this gate exists to prevent, wearing the gate's own uniform. Logging it at Critical (just
// below) was not enough: the log scrolls past while `GatesReadiness` keeps claiming enforcement
// that is not there, and a live portal booted in precisely that state.
//
// Disarming loses NO protection — with the sweep off there was none to lose — and it makes the
// reported state honest, so an UNARMED regression is reported as unarmed instead of as a stall
// nothing enforces. Turning the gate ON therefore means turning the sweep on too, which is what
// the configuration claimed all along.
var gateBake = gateBakeConfigured && bakeSweepEnabled;

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
if (gateBakeConfigured && !bakeSweepEnabled)
    app.Services.GetRequiredService<ILoggerFactory>()
        .CreateLogger("MeshWeaver.Hosting.NodeTypeBakeGate")
        .LogCritical(
            "NodeType bake gate is configured ON ({GateKey}=true) but the pre-warm sweep is OFF "
            + "({SweepKey}!=true), so the gate has nothing to measure. It has been DISARMED rather "
            + "than left registered and permanently green — a gate that reports healthy on every "
            + "rollout protects nothing and hides that it protects nothing. Enable the sweep to arm "
            + "it, or set {GateKeyToDisable}=false so the configuration stops claiming this "
            + "protection.",
            NodeTypeBakeGateExtensions.EnabledConfigKey,
            DynamicTypePreWarmerHostedService.EnabledConfigKey,
            NodeTypeBakeGateExtensions.EnabledConfigKey);

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

