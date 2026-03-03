using Microsoft.Extensions.Options;
using MeshWeaver.Hosting.PostgreSql;
using Npgsql;

public class Worker(
    NpgsqlDataSource dataSource,
    IOptions<PostgreSqlStorageOptions> options,
    IHostApplicationLifetime lifetime,
    ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Running database migration...");
        await PostgreSqlSchemaInitializer.InitializeAsync(dataSource, options.Value, stoppingToken);
        logger.LogInformation("Database migration completed successfully.");
        lifetime.StopApplication();
    }
}
