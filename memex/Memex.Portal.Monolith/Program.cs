using Memex.Portal.ServiceDefaults;
using Memex.Portal.Shared;
using MeshWeaver.ContentCollections;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

// Configure Memex services (pattern from MeshWeaver.Portal's ConfigureWebPortalServices)
builder.ConfigureMemexServices();

// Data protection: persist keys to local file system (single-instance monolith)
var keysPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "Memex", "DataProtection-Keys");
Directory.CreateDirectory(keysPath);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keysPath));

// NodeType compile cache: filesystem-backed in monolith (shared-blob isn't available
// without an Azure account). Versioned entries under {LocalAppData}/Memex/assembly-cache
// persist across restarts; cross-replica sharing isn't applicable here since the
// monolith runs in a single process.
var assemblyCachePath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "Memex", "assembly-cache");
builder.Services.AddFileSystemAssemblyStore(assemblyCachePath);

// Add Aspire service defaults (health checks, OpenTelemetry, service discovery)
builder.AddServiceDefaults();

// Resolve the graph storage base path for sample data source registration
var graphBasePath = builder.Configuration["Graph:Storage:BasePath"];
if (!string.IsNullOrEmpty(graphBasePath) && !Path.IsPathRooted(graphBasePath))
    graphBasePath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), graphBasePath));

// Configure MeshWeaver mesh
builder.UseMeshWeaver(
    AddressExtensions.CreateMeshAddress(),
    config =>
    {
        // Read storage config for static file serving
        var storageConfig = builder.Configuration.GetSection("Storage").Get<ContentCollectionConfig>();

        config = config
            .ConfigureMemexPortal()
            .ConfigureMemexMesh(builder.Configuration, builder.Environment.IsDevelopment());

        // Register storage collection at mesh level for static file serving (monolith only)
        if (storageConfig != null)
        {
            storageConfig = storageConfig with { IsEditable = false, IsStatic = true, ExposeInChildren = false };
            config.ConfigureHub(hub => hub.AddContentCollection(_ => storageConfig));
        }

        // Register sample data source repositories (file system only)
        if (!string.IsNullOrEmpty(graphBasePath))
        {
            config = config
                .AddFileSystemDataSource("ACME", "ACME Corporation",
                    Path.Combine(graphBasePath, "ACME"), "Sample ACME organization data")
                .AddFileSystemDataSource("Northwind", "Northwind Traders",
                    Path.Combine(graphBasePath, "Northwind"), "Sample Northwind trading data")
                .AddFileSystemDataSource("Cornerstone", "Cornerstone",
                    Path.Combine(graphBasePath, "Cornerstone"), "Sample Cornerstone data")
                .AddFileSystemDataSource("FutuRe", "FutuRe",
                    Path.Combine(graphBasePath, "FutuRe"), "Sample FutuRe reinsurance data")
                .AddFileSystemDataSource("SocialMedia", "Social Media",
                    Path.Combine(graphBasePath, "SocialMedia"), "Social media post planning demo");
        }

        return config.UseMonolithMesh();
    }
);

var app = builder.Build();

// Map Aspire default endpoints (health checks)
app.MapDefaultEndpoints();

// Start the Memex portal application (pattern from MeshWeaver.Portal's StartPortalApplication)
app.StartMemexApplication<Memex.Portal.Shared.App>();
