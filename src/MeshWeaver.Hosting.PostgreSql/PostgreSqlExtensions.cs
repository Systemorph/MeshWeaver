using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MeshWeaver.Hosting.PostgreSql;

/// <summary>
/// Extension methods for registering the database context in dependency injection.
/// </summary>
public static class PostgreSqlExtensions
{
    /// <summary>
    /// Adds the MeshWeaver PostgreSQL DbContext to the service collection.
    /// </summary>
    /// <param name="services">The service collection to add the DbContext to.</param>
    /// <param name="connectionString">The PostgreSQL connection string.</param>
    /// <returns>The updated service collection.</returns>
    public static IServiceCollection AddMeshWeaverPostgreSql(this IServiceCollection services, string connectionString)
    {
        services.AddDbContext<MeshWeaverDbContext>(options =>
            options.UseNpgsql(connectionString, npgsqlOptions =>
            {
                // Enable resilient PostgreSQL connections
                npgsqlOptions.EnableRetryOnFailure();
            }));

        // Register the DbContext for data protection
        services.AddDataProtection()
            .PersistKeysToDbContext<MeshWeaverDbContext>();

        return services;
    }

    /// <summary>
    /// Adds the MeshWeaver PostgreSQL DbContext to the service collection with customization options.
    /// </summary>
    /// <param name="services">The service collection to add the DbContext to.</param>
    /// <param name="optionsAction">Action to configure the DbContext options.</param>
    /// <returns>The updated service collection.</returns>
    public static IServiceCollection AddMeshWeaverPostgreSql(
        this IServiceCollection services,
        Action<DbContextOptionsBuilder> optionsAction)
    {
        services.AddDbContext<MeshWeaverDbContext>(optionsAction);

        // Register the DbContext for data protection
        services.AddDataProtection()
            .PersistKeysToDbContext<MeshWeaverDbContext>();

        return services;
    }

    /// <summary>
    /// Adds the MeshWeaver PostgreSQL DbContext configured by PostgreSqlOptions.
    /// </summary>
    /// <param name="services">The service collection to add the DbContext to.</param>
    /// <param name="configureOptions">Action to configure PostgreSQL options.</param>
    /// <returns>The updated service collection.</returns>
    public static IServiceCollection AddMeshWeaverPostgreSql(
        this IServiceCollection services,
        Action<PostgreSqlOptions> configureOptions)
    {
        // Add options configuration
        services.Configure(configureOptions);

        // Add DbContext using the options
        services.AddDbContext<MeshWeaverDbContext>((sp, options) =>
        {
            var postgresOptions = sp.GetRequiredService<IOptions<PostgreSqlOptions>>().Value;

            options.UseNpgsql(postgresOptions.ConnectionString, npgsqlOptions =>
            {
                // Enable resilient PostgreSQL connections
                npgsqlOptions.EnableRetryOnFailure(
                    postgresOptions.MaxRetryCount,
                    TimeSpan.FromSeconds(postgresOptions.MaxRetryDelay),
                    null);

                if (postgresOptions.CommandTimeout.HasValue)
                {
                    npgsqlOptions.CommandTimeout(postgresOptions.CommandTimeout.Value);
                }
            });

            if (postgresOptions.EnableSensitiveDataLogging)
            {
                options.EnableSensitiveDataLogging();
            }
        });

        // Register the DbContext for data protection
        services.AddDataProtection()
            .PersistKeysToDbContext<MeshWeaverDbContext>();

        return services;
    }
}
