using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Distributed;

/// <summary>
/// Blocks app startup until Cosmos DB is reachable.
/// Called between Build() and Run() to prevent Orleans grain failures during emulator startup.
/// </summary>
public static class CosmosReadinessProbe
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(5);

    public static async Task WaitForCosmosAsync(IServiceProvider services)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("CosmosReadinessProbe");
        var container = services.GetKeyedService<Container>("nodes");
        if (container == null)
        {
            logger.LogWarning("No keyed Container 'nodes' found — skipping Cosmos readiness probe");
            return;
        }

        var started = DateTimeOffset.UtcNow;
        while (true)
        {
            try
            {
                await container.ReadContainerAsync();
                logger.LogInformation("Cosmos DB is ready ({Elapsed:F1}s)",
                    (DateTimeOffset.UtcNow - started).TotalSeconds);
                return;
            }
            catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
            {
                var elapsed = DateTimeOffset.UtcNow - started;
                if (elapsed > MaxWait)
                {
                    logger.LogError("Cosmos DB not available after {Elapsed:F0}s — giving up", elapsed.TotalSeconds);
                    throw;
                }
                logger.LogInformation("Waiting for Cosmos DB ({Elapsed:F0}s)...", elapsed.TotalSeconds);
                await Task.Delay(RetryDelay);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var elapsed = DateTimeOffset.UtcNow - started;
                if (elapsed > MaxWait)
                {
                    logger.LogError(ex, "Cosmos DB probe failed after {Elapsed:F0}s", elapsed.TotalSeconds);
                    throw;
                }
                logger.LogInformation("Waiting for Cosmos DB ({Elapsed:F0}s)...", elapsed.TotalSeconds);
                await Task.Delay(RetryDelay);
            }
        }
    }
}
