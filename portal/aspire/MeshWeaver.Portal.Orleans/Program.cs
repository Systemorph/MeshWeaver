using MeshWeaver.Hosting.Orleans;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.Portal.ServiceDefaults;
using MeshWeaver.Portal.Shared.Mesh;

var builder = WebApplication.CreateBuilder(args);
builder.AddAspireServiceDefaults();
builder.AddKeyedAzureTableClient(StorageProviders.MeshCatalog);
builder.AddKeyedAzureTableClient(StorageProviders.Activity);
builder.AddKeyedRedisClient("orleans-redis",
    configureOptions: options =>
    {
        // Increase timeout values
        options.ConnectTimeout = 5000;           // 5 seconds
        options.SyncTimeout = 15000;             // 15 seconds
        options.ConnectRetry = 3;                // Retry count
        options.AbortOnConnectFail = false;      // Don't abort on initial connection failure
        options.KeepAlive = 60;                  // Send keepalive every 60 seconds
    });
builder.AddKeyedRedisClient(StorageProviders.AddressRegistry,
    configureOptions: options =>
    {
        // Increase timeout values
        options.ConnectTimeout = 5000;           // 5 seconds
        options.SyncTimeout = 15000;             // 15 seconds
        options.ConnectRetry = 3;                // Retry count
        options.AbortOnConnectFail = false;      // Don't abort on initial connection failure
        options.KeepAlive = 60;                  // Send keepalive every 60 seconds
    });

var address = new MeshAddress();



builder.UseOrleansMeshServer(address)
    .ConfigurePortalMesh()
    .AddPostgresSerilog()
    ;

var app = builder.Build();
app.MapDefaultEndpoints();
app.Run();
