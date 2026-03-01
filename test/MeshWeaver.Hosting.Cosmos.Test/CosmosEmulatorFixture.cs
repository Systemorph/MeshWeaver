using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using MeshWeaver.Hosting.Cosmos;
using MeshWeaver.Hosting.Persistence;
using Microsoft.Azure.Cosmos;
using Testcontainers.CosmosDb;
using Xunit;

namespace MeshWeaver.Hosting.Cosmos.Test;

/// <summary>
/// Shared fixture that starts a Cosmos DB emulator container.
/// Use with [Collection("CosmosEmulator")] on test classes.
/// </summary>
public class CosmosEmulatorFixture : IAsyncLifetime
{
    internal const string DatabaseName = "cosmostest";

    private CosmosDbContainer? _container;

    /// <summary>
    /// Shared CosmosClient configured for the emulator (SSL bypass, STJ serialization).
    /// Static so Orleans silo configurators can access it.
    /// </summary>
    internal static CosmosClient? SharedClient { get; private set; }

    public async ValueTask InitializeAsync()
    {
        _container = new CosmosDbBuilder("mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:latest")
            .WithEnvironment("AZURE_COSMOS_EMULATOR_PARTITION_COUNT", "25")
            .Build();
        await _container.StartAsync();

        var jsonOptions = StorageImporter.CreateFullImportOptions();

        SharedClient = new CosmosClient(_container.GetConnectionString(), new CosmosClientOptions
        {
            HttpClientFactory = () => _container.HttpClient,
            ConnectionMode = ConnectionMode.Gateway,
            UseSystemTextJsonSerializerWithOptions = jsonOptions
        });

        // Wait for the emulator to be fully operational
        await WaitForEmulatorReadyAsync();

        // Create database and containers matching the AppHost layout
        var dbResponse = await SharedClient.CreateDatabaseIfNotExistsAsync(DatabaseName);
        await dbResponse.Database.CreateContainerIfNotExistsAsync(
            new ContainerProperties("nodes", "/namespace") { DefaultTimeToLive = -1 });
        await dbResponse.Database.CreateContainerIfNotExistsAsync(
            new ContainerProperties("partitions", "/partitionKey") { DefaultTimeToLive = -1 });

    }

    private async Task WaitForEmulatorReadyAsync()
    {
        // The emulator returns 503 for several seconds after the container starts.
        // Wait until a full document round-trip succeeds.
        const int maxAttempts = 30;
        for (var i = 0; i < maxAttempts; i++)
        {
            try
            {
                var dbResp = await SharedClient!.CreateDatabaseIfNotExistsAsync("_probe");
                var cResp = await dbResp.Database.CreateContainerIfNotExistsAsync(
                    new ContainerProperties("_probe", "/id"));
                await cResp.Container.UpsertItemAsync(
                    new { id = "ping", value = "ok" }, new PartitionKey("ping"));
                await cResp.Container.ReadItemAsync<object>("ping", new PartitionKey("ping"));
                await dbResp.Database.DeleteAsync();
                return;
            }
            catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.ServiceUnavailable
                                              or HttpStatusCode.Gone)
            {
                await Task.Delay(2000);
            }
            catch (HttpRequestException)
            {
                await Task.Delay(2000);
            }
        }
        throw new TimeoutException($"Cosmos emulator did not become ready after {maxAttempts} attempts.");
    }

    public async ValueTask DisposeAsync()
    {
        SharedClient?.Dispose();
        SharedClient = null;

        if (_container != null)
            await _container.DisposeAsync();
    }
}

[CollectionDefinition("CosmosEmulator")]
public class CosmosEmulatorCollection : ICollectionFixture<CosmosEmulatorFixture> { }
