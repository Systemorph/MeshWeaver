using MeshWeaver.Connection.Orleans;
using MeshWeaver.Hosting.AzureBlob;
using MeshWeaver.Mesh;
using MeshWeaver.Portal.ServiceDefaults;
using MeshWeaver.Portal.Shared.Web;
using Microsoft.AspNetCore.DataProtection;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

builder.AddAspireServiceDefaults();

// Configure Data Protection to use Redis
var redis = ConnectionMultiplexer.Connect(builder.Configuration.GetConnectionString("orleans-redis"));
builder.Services.AddDataProtection()
    .PersistKeysToStackExchangeRedis(redis, "DataProtection-Keys")
    .SetApplicationName("MeshWeaver");

// Use the configureOptions parameter to configure StackExchange.Redis options
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

builder.AddKeyedAzureBlobClient(StorageProviders.Articles);
// Add services to the container.
builder.ConfigureWebPortalServices();
builder.UseOrleansMeshClient()
            .AddPostgresSerilog()
            .ConfigureWebPortal()
            .ConfigureServices(services => services.AddAzureBlobArticles())
    ;
// Add PostgreSQL using the Aspire-managed container
builder.AddNpgsqlDataSource("meshweaverdb"); // Uses the container reference from AppHost

var app = builder.Build();
app.StartPortalApplication();
