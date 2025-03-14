using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace MeshWeaver.Portal.MigrationService;

public class PostgreSqlMigrationService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IHostEnvironment _hostEnvironment;
    private readonly IHostApplicationLifetime _hostApplicationLifetime;
    private readonly ILogger<PostgreSqlMigrationService> _logger;
    private readonly ActivitySource _activitySource;

    public PostgreSqlMigrationService(
        IServiceProvider serviceProvider,
        IHostEnvironment hostEnvironment,
        IHostApplicationLifetime hostApplicationLifetime,
        ILogger<PostgreSqlMigrationService> logger)
    {
        _serviceProvider = serviceProvider;
        _hostEnvironment = hostEnvironment;
        _hostApplicationLifetime = hostApplicationLifetime;
        _logger = logger;
        _activitySource = new ActivitySource(_hostEnvironment.ApplicationName);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var activity = _activitySource.StartActivity(_hostEnvironment.ApplicationName, ActivityKind.Client);

        _logger.LogInformation("Starting PostgreSQL database migration");

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<MeshWeaver.Hosting.PostgreSql.MeshWeaverDbContext>();

            await EnsureDatabaseAsync(dbContext, stoppingToken);
            await RunMigrationAsync(dbContext, stoppingToken);

            _logger.LogInformation("PostgreSQL database migration completed successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred while migrating the PostgreSQL database");
            activity?.AddException(ex);
            throw;
        }
        finally
        {
            // This is crucial - it signals Aspire that the migration service has completed its work
            _hostApplicationLifetime.StopApplication();
        }
    }

    private async Task EnsureDatabaseAsync(MeshWeaver.Hosting.PostgreSql.MeshWeaverDbContext dbContext, CancellationToken cancellationToken)
    {
        var dbCreator = dbContext.GetService<IRelationalDatabaseCreator>();

        var strategy = dbContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            // Create the database if it does not exist
            if (!await dbCreator.ExistsAsync(cancellationToken))
            {
                _logger.LogInformation("Creating database as it doesn't exist");
                await dbCreator.CreateAsync(cancellationToken);
            }
        });
    }

    private async Task RunMigrationAsync(MeshWeaver.Hosting.PostgreSql.MeshWeaverDbContext dbContext, CancellationToken cancellationToken)
    {
        var strategy = dbContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            // Run migration in a transaction to avoid partial migration if it fails
            _logger.LogInformation("Applying database migrations");
            await dbContext.Database.MigrateAsync(cancellationToken);
            _logger.LogInformation("Committing transaction");
        });
    }
}
