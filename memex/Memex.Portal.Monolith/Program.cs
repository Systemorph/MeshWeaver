using Memex.Portal.ServiceDefaults;
using Memex.Portal.Shared;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.DataProtection;

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

// Add Aspire service defaults (health checks, OpenTelemetry, service discovery)
builder.AddServiceDefaults();

// Configure MeshWeaver mesh
builder.UseMeshWeaver(
    AddressExtensions.CreateMeshAddress(),
    config => config
        .ConfigureMemexPortal()
        .ConfigureMemexMesh(builder.Configuration, builder.Environment.IsDevelopment())
        .UseMonolithMesh()
);

var app = builder.Build();

// Map Aspire default endpoints (health checks)
app.MapDefaultEndpoints();

// Start the Memex portal application (pattern from MeshWeaver.Portal's StartPortalApplication)
app.StartMemexApplication<Memex.Portal.Shared.App>();
