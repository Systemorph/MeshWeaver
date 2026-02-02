using Loom.Portal.ServiceDefaults;
using Loom.Portal.Shared;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Messaging;

var builder = WebApplication.CreateBuilder(args);

// Configure Loom services (pattern from MeshWeaver.Portal's ConfigureWebPortalServices)
builder.ConfigureLoomServices();

// Add Aspire service defaults (health checks, OpenTelemetry, service discovery)
builder.AddServiceDefaults();

// Configure MeshWeaver mesh
builder.UseMeshWeaver(
    AddressExtensions.CreateMeshAddress(),
    config => config
        .ConfigureLoomPortal()
        .ConfigureLoomMesh(builder.Configuration, builder.Environment.IsDevelopment())
        .UseMonolithMesh()
);

var app = builder.Build();

// Map Aspire default endpoints (health checks)
app.MapDefaultEndpoints();

// Start the Loom portal application (pattern from MeshWeaver.Portal's StartPortalApplication)
app.StartLoomApplication<Loom.Portal.Shared.App>();
