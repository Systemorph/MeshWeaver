using System;
using System.Net;
using System.Net.Http;
using System.Threading;
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
            UseSystemTextJsonSerializerWithOptions = jsonOptions,
            // Retry handler for emulator 503 ServiceUnavailable during warmup
            CustomHandlers = { new ServiceUnavailableRetryHandler() }
        });

        // Wait for the emulator to be fully operational
        await WaitForEmulatorReadyAsync();

        // Create database and containers matching the AppHost layout
        var dbResponse = await SharedClient.CreateDatabaseIfNotExistsAsync(DatabaseName);
        await dbResponse.Database.CreateContainerIfNotExistsAsync(
            new ContainerProperties("nodes", "/namespace") { DefaultTimeToLive = -1 });
        await dbResponse.Database.CreateContainerIfNotExistsAsync(
            new ContainerProperties("partitions", "/partitionKey") { DefaultTimeToLive = -1 });

        // Drop and recreate the partition_test database used by PartitionedContainerTests
        // to ensure clean state (containers may have stale partition key definitions)
        try { await SharedClient.GetDatabase("partition_test").DeleteAsync(); } catch (CosmosException) { }
        await SharedClient.CreateDatabaseIfNotExistsAsync("partition_test");
    }

    private async Task WaitForEmulatorReadyAsync()
    {
        const int maxAttempts = 15;
        for (var i = 0; i < maxAttempts; i++)
        {
            try
            {
                await SharedClient!.ReadAccountAsync();
                return;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.ServiceUnavailable)
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

/// <summary>
/// Cosmos SDK request handler that retries on 503 ServiceUnavailable.
/// The emulator returns 503 briefly after startup while partition services warm up.
/// </summary>
internal class ServiceUnavailableRetryHandler : RequestHandler
{
    private const int MaxRetries = 5;

    public override async Task<ResponseMessage> SendAsync(
        RequestMessage request, CancellationToken cancellationToken)
    {
        ResponseMessage? response = null;
        for (var attempt = 0; attempt <= MaxRetries; attempt++)
        {
            response = await base.SendAsync(request, cancellationToken);
            if (response.StatusCode != HttpStatusCode.ServiceUnavailable)
                return response;

            if (attempt < MaxRetries)
                await Task.Delay(2000 * (attempt + 1), cancellationToken);
        }
        return response!;
    }
}

[CollectionDefinition("CosmosEmulator")]
public class CosmosEmulatorCollection : ICollectionFixture<CosmosEmulatorFixture> { }
