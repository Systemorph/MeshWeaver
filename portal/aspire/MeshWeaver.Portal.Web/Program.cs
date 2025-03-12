using Azure.Storage.Blobs;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Hosting.AzureBlob;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.Portal.ServiceDefaults;
using MeshWeaver.Portal.Shared.Web;
using Orleans.Configuration;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.AddKeyedAzureTableClient("orleans-clustering");
builder.AddKeyedAzureBlobClient(StorageProviders.Articles);

// Add services to the container.
builder.ConfigureWebPortalServices();
builder.UseOrleansMeshClient(new MeshAddress())
    .AddPostgresSerilog()
    .ConfigureWebPortal()
    .ConfigureServices(services =>
    {
        services.Configure<ConnectionOptions>(options =>
        {
            options.OpenConnectionTimeout = TimeSpan.FromMinutes(1); // Try longer timeout
        });
        services.AddDataProtection();
        return services.AddAzureBlobArticles();
    });


// Add PostgreSQL using the Aspire-managed container
builder.AddNpgsqlDataSource("meshweaverdb"); // Uses the container reference from AppHost

var app = builder.Build();
app.MapDefaultEndpoints();
app.StartPortalApplication();
