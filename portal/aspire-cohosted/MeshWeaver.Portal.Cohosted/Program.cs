using Azure.Data.Tables;
using MeshWeaver.Hosting.AzureBlob;
using MeshWeaver.Hosting.Orleans;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.Portal.ServiceDefaults;
using MeshWeaver.Portal.Shared.Mesh;
using MeshWeaver.Portal.Shared.Web;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.AddKeyedAzureBlobClient(StorageProviders.Articles);

// Add services to the container.
builder.ConfigureWebPortalServices();
builder.UseOrleansMeshServer(new MeshAddress(), silo =>
    silo.UseAzureStorageClustering(o =>
    {
        var connectionString = builder.Configuration.GetConnectionString("orleans-clustering")!;

        // If it's a URL-style connection string, convert it to proper format
        if (connectionString.StartsWith("https://"))
        {
            // Extract account name from URL
            var accountName = connectionString
                .Replace("https://", "")
                .Split('.')[0];

            // Use DefaultAzureCredential (managed identity) instead of account key
            var endpoint = new Uri(connectionString);
            o.TableServiceClient = new TableServiceClient(
                endpoint,
                new Azure.Identity.DefaultAzureCredential());
        }
        else
        {
            o.TableServiceClient = new TableServiceClient(connectionString);
        }

    }) // Add this configuration for startup delays
    // Configure clustering membership options
    )
    

    .ConfigurePortalMesh()
    .AddPostgresSerilog()
    .ConfigureWebPortal()
    .ConfigureServices(services =>
    {
        services.AddDataProtection();
        return services.AddAzureBlobArticles();
    });


// Add PostgreSQL using the Aspire-managed container
builder.AddNpgsqlDataSource("meshweaverdb"); // Uses the container reference from AppHost

var app = builder.Build();
app.MapDefaultEndpoints();

app.StartPortalApplication();
