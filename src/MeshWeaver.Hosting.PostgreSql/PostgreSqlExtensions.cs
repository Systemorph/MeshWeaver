using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
    /// <param name="connectionStringOrName">Either a direct connection string or a named connection.</param>
    /// <returns>The updated service collection.</returns>
    public static IServiceCollection AddPostgreSqlMeshContext(this IServiceCollection services,
        string connectionStringOrName)
    {
        services.AddDbContext<MeshWeaverDbContext>((sp, options) =>
        {
            var configuration = sp.GetRequiredService<IConfiguration>();
            // Try to get as a connection string name first
            var connectionString = configuration.GetConnectionString(connectionStringOrName)
                                  ?? connectionStringOrName; // Fall back to direct connection string

            options.UseNpgsql(connectionString, npgsqlOptions =>
            {
                // Enable resilient PostgreSQL connections
                npgsqlOptions.EnableRetryOnFailure();
            });
        });

        return services;
    }
}
