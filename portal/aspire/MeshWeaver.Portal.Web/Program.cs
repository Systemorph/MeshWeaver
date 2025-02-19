using MeshWeaver.Connection.Orleans;
using MeshWeaver.Hosting.AzureBlob;
using MeshWeaver.Mesh;
using MeshWeaver.Portal.ServiceDefaults;
using MeshWeaver.Portal.Shared.Mesh;
using MeshWeaver.Portal.Shared.Web;
using Microsoft.Extensions.Azure;

var builder = WebApplication.CreateBuilder(args);

builder.AddAspireServiceDefaults();
builder.AddKeyedRedisClient("orleans-redis");

builder.AddKeyedAzureBlobClient(StorageProviders.Articles);
// Add services to the container.
builder.ConfigureWebPortalServices();
builder.UseOrleansMeshClient()
            .ConfigureWebPortalMesh()
            .ConfigureServices(services => services.AddAzureBlobArticles())
    ;


var app = builder.Build();
app.StartPortalApplication();
