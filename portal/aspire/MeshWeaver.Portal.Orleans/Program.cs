using MeshWeaver.Hosting.AzureBlob;
using MeshWeaver.Hosting.Orleans;
using MeshWeaver.Hosting.PostgreSql;
using MeshWeaver.Messaging;
using MeshWeaver.Portal.ServiceDefaults;
using MeshWeaver.Portal.Shared.Mesh;
using MeshWeaver.Portal.Shared.Web;
using Microsoft.AspNetCore.DataProtection;
using Orleans.Configuration;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.AddKeyedAzureTableServiceClient("orleans-clustering");
builder.AddKeyedAzureBlobServiceClient(StorageProviders.Documentation);
builder.AddKeyedAzureBlobServiceClient(StorageProviders.Reinsurance);

// Add web portal services
builder.ConfigureWebPortalServices();
builder.ConfigurePostgreSqlContext("meshweaverdb");

// Configure Orleans with Azure Table Storage
var serviceId = OrleansConstants.ServiceId;
var address = new MeshAddress();
builder.UseOrleansMeshServer(address, silo =>
        silo.Configure<ClusterOptions>(opts =>
        {
            opts.ClusterId = OrleansConstants.ClusterId;
            opts.ServiceId = OrleansConstants.ServiceId;
        })
    )
    .ConfigurePortalMesh()
    .AddEfCoreSerilog("Silo", serviceId)
    .AddEfCoreMessageLog("Silo", serviceId)
    .ConfigureWebPortal(builder.Configuration)
    .ConfigureServices(services =>
    {
        services.Configure<ConnectionOptions>(options =>
        {
            options.OpenConnectionTimeout = TimeSpan.FromMinutes(1); // Try longer timeout
        });
        // Configure Data Protection to persist keys to PostgreSQL using MeshWeaverDbContext
        services.AddDataProtection().PersistKeysToDbContext<MeshWeaverDbContext>();
        return services
            .AddAzureBlob();
    });

var app = builder.Build();
app.MapDefaultEndpoints();
app.StartPortalApplication();

internal sealed class OrleansClientConnectionRetryFilter : IClientConnectionRetryFilter
{
    private int _retryCount = 0;
    private const int MaxRetry = 20;
    private const int Delay = 2_000;

    public async Task<bool> ShouldRetryConnectionAttempt(
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (_retryCount >= MaxRetry)
        {
            return false;
        }

        if (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(++_retryCount * Delay, cancellationToken);
            return true;
        }

        return false;
    }
}
