using Memex.Portal.ServiceDefaults;
using Memex.Portal.Shared;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Messaging;

var builder = WebApplication.CreateBuilder(args);

// Configure Memex services (pattern from MeshWeaver.Portal's ConfigureWebPortalServices)
builder.ConfigureMemexServices();

// Add Aspire service defaults (health checks, OpenTelemetry, service discovery)
builder.AddServiceDefaults();

// Configure MeshWeaver mesh
builder.UseMeshWeaver(
    AddressExtensions.CreateMeshAddress(),
    config => config
        .ConfigureMemexPortal()
        .ConfigureMemexMesh(builder.Configuration, builder.Environment.IsDevelopment())
        .InstallAssemblies(typeof(MeshWeaver.Northwind.Application.NorthwindApplicationAttribute).Assembly.Location)
        .UseMonolithMesh()
);

var app = builder.Build();

// Map Aspire default endpoints (health checks)
app.MapDefaultEndpoints();

// Start the Memex portal application (pattern from MeshWeaver.Portal's StartPortalApplication)
app.StartMemexApplication<Memex.Portal.Shared.App>();
