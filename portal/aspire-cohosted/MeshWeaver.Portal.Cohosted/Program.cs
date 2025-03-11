using MeshWeaver.Hosting.AzureBlob;
using MeshWeaver.Hosting.Orleans;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.Portal.ServiceDefaults;
using MeshWeaver.Portal.Shared.Mesh;
using MeshWeaver.Portal.Shared.Web;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.AddKeyedAzureTableClient("orleans-clustering");
builder.AddKeyedAzureBlobClient(StorageProviders.Articles);

// Add services to the container.
builder.ConfigureWebPortalServices();
builder.UseOrleansMeshServer(new MeshAddress())
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
app.StartPortalApplication();
