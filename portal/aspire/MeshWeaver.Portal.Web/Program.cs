using MeshWeaver.Connection.Orleans;
using MeshWeaver.Hosting.AzureBlob;
using MeshWeaver.Hosting.PostgreSql;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.Portal.ServiceDefaults;
using MeshWeaver.Portal.Shared.Web;
using Microsoft.AspNetCore.DataProtection;
using Orleans.Configuration;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.AddKeyedAzureTableClient("orleans-clustering");
builder.AddKeyedAzureBlobClient(StorageProviders.Articles);

// Add services to the container.
builder.ConfigureWebPortalServices();
builder.ConfigurePostgreSqlContext();
builder.UseOrleansMeshClient(new MeshAddress())
    .AddEfCoreSerilog()
    .ConfigureWebPortal()
    .ConfigureServices(services =>
    {
        services.Configure<ConnectionOptions>(options =>
        {
            options.OpenConnectionTimeout = TimeSpan.FromMinutes(1); // Try longer timeout
        });
        // Configure Data Protection to persist keys to PostgreSQL using MeshWeaverDbContext
        services.AddDataProtection().PersistKeysToDbContext<MeshWeaverDbContext>();
        return services.AddAzureBlobArticles();
    });

var app = builder.Build();
app.MapDefaultEndpoints();
app.StartPortalApplication();
