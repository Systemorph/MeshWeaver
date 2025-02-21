using MeshWeaver.Connection.Orleans;
using MeshWeaver.Hosting.AzureBlob;
using MeshWeaver.Mesh;
using MeshWeaver.Portal.ServiceDefaults;
using MeshWeaver.Portal.Shared.Web;

var builder = WebApplication.CreateBuilder(args);

builder.AddAspireServiceDefaults();
builder.AddKeyedRedisClient("orleans-redis");

builder.AddKeyedAzureBlobClient(StorageProviders.Articles);
// Add services to the container.
builder.ConfigureWebPortalServices();
builder.UseOrleansMeshClient()
            .AddPostgresSerilog()
            .ConfigureWebPortalMesh()
            .ConfigureServices(services => services.AddAzureBlobArticles())
    ;
// Add PostgreSQL using the Aspire-managed container
builder.AddNpgsqlDataSource("meshweaverdb"); // Uses the container reference from AppHost

var app = builder.Build();
app.StartPortalApplication();
