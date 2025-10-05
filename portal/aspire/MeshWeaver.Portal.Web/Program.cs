using MeshWeaver.Connection.Orleans;
using MeshWeaver.Hosting.AzureBlob;
using MeshWeaver.Hosting.PostgreSql;
using MeshWeaver.Messaging;
using MeshWeaver.Portal.ServiceDefaults;
using MeshWeaver.Portal.Shared.Web;
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

var address = new MeshAddress();
builder.UseOrleansMeshClient(address, client =>
        client.Configure<ClusterOptions>(opts =>
        {
            opts.ClusterId = OrleansConstants.ClusterId;
            opts.ServiceId = OrleansConstants.ServiceId;
        })
        .Configure<GatewayOptions>(opts =>
        {
            opts.GatewayListRefreshPeriod = TimeSpan.FromSeconds(10);
        })
    //.AddClientConnectionRetryFilter<OrleansClientConnectionRetryFilter>()
    )
    .AddEfCoreSerilog("Frontend", address.Id)
    .AddEfCoreMessageLog("Frontend", address.Id)
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
