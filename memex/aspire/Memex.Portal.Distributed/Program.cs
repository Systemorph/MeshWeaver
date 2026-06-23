using Azure.Identity;
using Azure.Storage.Blobs;
using Memex.Portal.ServiceDefaults;
using Memex.Portal.Shared;
using Microsoft.AspNetCore.DataProtection;
using MeshWeaver.Graph.Configuration;
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
    builder.Services.AddFileSystemAssemblyStore(Path.Combine(dataRoot, "assembly-cache"));

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

// Register embedding provider if configured (Cohere embed-v4 via Azure Foundry)
var embeddingOptions = builder.Configuration.GetSection("Embedding").Get<EmbeddingOptions>() ?? new EmbeddingOptions();
builder.Services.AddAzureFoundryEmbeddings(embeddingOptions);

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
                    var credential = new DefaultAzureCredential();
                    dsb.UsePeriodicPasswordProvider(async (_, ct) =>
                    {
                        var token = await credential.GetTokenAsync(
                            new Azure.Core.TokenRequestContext(["https://ossrdbms-aad.database.windows.net/.default"]), ct);
                        return token.Token;
                    }, TimeSpan.FromMinutes(4), TimeSpan.FromSeconds(10));
                }
                : null))
    .ConfigureMemexMesh(builder.Configuration, builder.Environment.IsDevelopment())
    .ConfigureMemexPortal();

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

var app = builder.Build();

app.MapDefaultEndpoints();
app.StartMemexApplication<Memex.Portal.Shared.App>();

