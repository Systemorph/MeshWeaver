using Autofac.Core;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Hosting.AzureBlob;
using MeshWeaver.Hosting.PostgreSql;
using MeshWeaver.Messaging;
using MeshWeaver.Portal.ServiceDefaults;
using MeshWeaver.Portal.Shared.Web;
using MeshWeaver.ShortGuid;
using Microsoft.AspNetCore.DataProtection;
using Orleans.Configuration;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.AddKeyedAzureTableServiceClient("orleans-clustering");
builder.AddKeyedAzureBlobServiceClient(StorageProviders.Documentation);
builder.AddKeyedAzureBlobServiceClient(StorageProviders.Reinsurance);
// Add services to the container.
builder.ConfigureWebPortalServices();
builder.ConfigurePostgreSqlContext("meshweaverdb");

var serviceId = Guid.NewGuid().AsString();
builder.UseOrleansMeshClient(new MeshAddress(serviceId))
    .AddEfCoreSerilog("Frontend", serviceId)
    .AddEfCoreMessageLog("Frontend", serviceId)
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
