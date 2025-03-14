using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using MeshWeaver.Hosting.PostgreSql;

namespace MeshWeaver.Portal.MigrationService;

public class PostgreSqlMigrationService : BackgroundService
{
    private readonly IServiceProvider serviceProvider;
    private readonly IHostEnvironment hostEnvironment;
    private readonly IHostApplicationLifetime hostApplicationLifetime;
    private readonly ILogger<PostgreSqlMigrationService> logger;
    private readonly ActivitySource activitySource;

    public PostgreSqlMigrationService(
        IServiceProvider serviceProvider,
        IHostEnvironment hostEnvironment,
        IHostApplicationLifetime hostApplicationLifetime,
        ILogger<PostgreSqlMigrationService> logger)
    {
        this.serviceProvider = serviceProvider;
        this.hostEnvironment = hostEnvironment;
        this.hostApplicationLifetime = hostApplicationLifetime;
        this.logger = logger;
        activitySource = new ActivitySource(this.hostEnvironment.ApplicationName);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var activity = activitySource.StartActivity(hostEnvironment.ApplicationName, ActivityKind.Client);

        logger.LogInformation("Starting PostgreSQL database migration");

        try
        {
            using var scope = serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<MeshWeaverDbContext>();

            await EnsureDatabaseAsync(dbContext, stoppingToken);
            await RunMigrationAsync(dbContext, stoppingToken);

            logger.LogInformation("PostgreSQL database migration completed successfully");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "An error occurred while migrating the PostgreSQL database");
            activity?.AddException(ex);
            throw;
        }
        finally
        {
            // This is crucial - it signals Aspire that the migration service has completed its work
            hostApplicationLifetime.StopApplication();
        }
    }

    private async Task EnsureDatabaseAsync(MeshWeaverDbContext dbContext, CancellationToken cancellationToken)
    {
        var dbCreator = dbContext.GetService<IRelationalDatabaseCreator>();

        var strategy = dbContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            // Create the database if it does not exist
            if (!await dbCreator.ExistsAsync(cancellationToken))
            {
                logger.LogInformation("Creating database as it doesn't exist");
                await dbCreator.CreateAsync(cancellationToken);
            }
        });
    }

    private async Task RunMigrationAsync(MeshWeaverDbContext dbContext, CancellationToken cancellationToken)
    {
        var strategy = dbContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            // Run migration in a transaction to avoid partial migration if it fails
            logger.LogInformation("Applying database migrations");
            await dbContext.Database.MigrateAsync(cancellationToken);
            logger.LogInformation("Committing transaction");
        });
    }
}
