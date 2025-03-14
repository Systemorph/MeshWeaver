using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using System;

namespace MeshWeaver.Hosting.PostgreSql;

/// <summary>
/// Design-time factory for creating MeshWeaverDbContext instances during migration operations.
/// </summary>
public class MeshWeaverDbContextFactory : IDesignTimeDbContextFactory<MeshWeaverDbContext>
{
    /// <summary>
    /// Creates a new instance of MeshWeaverDbContext for design-time activities like migrations.
    /// </summary>
    /// <param name="args">Command line arguments.</param>
    /// <returns>A configured MeshWeaverDbContext instance.</returns>
    public MeshWeaverDbContext CreateDbContext(string[] args)
    {
        var options = new PostgreSqlOptions();

        // Override connection string if provided in args
        if (args.Length > 0 && !string.IsNullOrEmpty(args[0]))
        {
            options.ConnectionString = args[0];
        }

        var optionsBuilder = new DbContextOptionsBuilder<MeshWeaverDbContext>();
        optionsBuilder.UseNpgsql(options.ConnectionString, npgsqlOptions =>
        {
            // Configure migrations to be in the current assembly
            npgsqlOptions.MigrationsAssembly(typeof(MeshWeaverDbContextFactory).Assembly.GetName().Name);

            if (options.MaxRetryCount > 0)
            {
                npgsqlOptions.EnableRetryOnFailure(
                    options.MaxRetryCount,
                    TimeSpan.FromSeconds(options.MaxRetryDelay),
                    null);
            }

            if (options.CommandTimeout.HasValue)
            {
                npgsqlOptions.CommandTimeout(options.CommandTimeout.Value);
            }
        });

        if (options.EnableSensitiveDataLogging)
        {
            optionsBuilder.EnableSensitiveDataLogging();
        }

        return new MeshWeaverDbContext(optionsBuilder.Options);
    }
}
