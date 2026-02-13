using System;
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
        _container = new CosmosDbBuilder("mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:latest").Build();
        await _container.StartAsync();

        var jsonOptions = StorageImporter.CreateFullImportOptions();

        SharedClient = new CosmosClient(_container.GetConnectionString(), new CosmosClientOptions
        {
            HttpClientFactory = () => _container.HttpClient,
            ConnectionMode = ConnectionMode.Gateway,
            UseSystemTextJsonSerializerWithOptions = jsonOptions
        });

        // Create database and containers matching the AppHost layout
        var dbResponse = await SharedClient.CreateDatabaseIfNotExistsAsync(DatabaseName);
        await dbResponse.Database.CreateContainerIfNotExistsAsync(
            new ContainerProperties("nodes", "/namespace") { DefaultTimeToLive = -1 });
        await dbResponse.Database.CreateContainerIfNotExistsAsync(
            new ContainerProperties("partitions", "/partitionKey") { DefaultTimeToLive = -1 });
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
