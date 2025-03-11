using MeshWeaver.Hosting.Orleans;
using MeshWeaver.Messaging;
using MeshWeaver.Portal.ServiceDefaults;
using MeshWeaver.Portal.Shared.Mesh;
using Orleans.Configuration;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.AddKeyedAzureTableClient("orleans-clustering");

// Create MeshAddress instance
var address = new MeshAddress();

// Configure Orleans with Azure Table Storage
builder.UseOrleansMeshServer(address)
    .ConfigurePortalMesh()
    .AddPostgresSerilog()
    .ConfigureServices(services =>
    {
        services.Configure<SiloMessagingOptions>(options =>
        {
            // Increase timeouts
            options.ResponseTimeout = TimeSpan.FromSeconds(30);
            options.ResponseTimeoutWithDebugger = TimeSpan.FromMinutes(5);
        });

        services.Configure<ConnectionOptions>(options =>
        {
            // Increase connection retry settings
            options.OpenConnectionTimeout = TimeSpan.FromSeconds(30);
        });
        return services;
    });

var app = builder.Build();
app.MapDefaultEndpoints();
app.Run();
