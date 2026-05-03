using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Memex.Portal.Distributed;

/// <summary>
/// Hard gate that runs once at portal startup: queries
/// <c>admin.mesh_nodes WHERE id='db_version'</c> and refuses to start the
/// host if the row is missing or its <c>Version</c> is below
/// <see cref="ExpectedDbVersion"/>.
///
/// <para>Why a gate, not just a healthcheck: Aspire's
/// <c>WaitForCompletion(dbMigration)</c> is a soft dependency hint at deploy
/// time. In Container Apps, the portal and migration run as independently
/// scheduled containers — a crashed migration silently lets the portal start
/// with a half-migrated DB. The previous prod symptom was exactly this: V02
/// crashed, V10 never ran, no per-user schemas, every user denied at the
/// permission layer because the synced AccessAssignment query couldn't find
/// the partition. Failing portal startup makes the bad state loudly visible
/// in the Container App revision status (Failed) and prevents traffic from
/// being routed to a broken portal.</para>
///
/// <para>Bump <see cref="ExpectedDbVersion"/> in lock-step with the highest
/// <c>Vxx_*.cs</c> migration in <c>Memex.Database.Migration</c>. Mismatch
/// between the version this portal expects and what the runner produced
/// fails-loud at startup with a clear diagnostic.</para>
/// </summary>
public sealed class DbVersionGate(
    NpgsqlDataSource dataSource,
    IHostApplicationLifetime lifetime,
    ILogger<DbVersionGate> logger) : IHostedService
{
    /// <summary>
    /// Highest migration version this portal build expects to find in the DB.
    /// Keep in sync with the highest Vxx_*.cs file in
    /// <c>memex/aspire/Memex.Database.Migration/Migrations/</c>.
    /// </summary>
    public const int ExpectedDbVersion = 16;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var cmd = dataSource.CreateCommand("""
                SELECT (content->>'Version')::int AS v
                  FROM admin.mesh_nodes
                 WHERE id = 'db_version' AND namespace = ''
                 LIMIT 1
                """);
            var raw = await cmd.ExecuteScalarAsync(cancellationToken);
            var version = raw switch
            {
                int v => v,
                long l => (int)l,
                _ => 0
            };

            if (version < ExpectedDbVersion)
            {
                logger.LogCritical(
                    "DB migration incomplete: admin.mesh_nodes.db_version={Actual} < expected {Expected}. " +
                    "The db-migration container probably crashed mid-run — check its ACA logs " +
                    "(`az containerapp logs show -n db-migration -g <resource-group> --tail 200`). " +
                    "Refusing to start the portal until the DB is fully migrated.",
                    version, ExpectedDbVersion);
                lifetime.StopApplication();
                return;
            }

            logger.LogInformation(
                "DB version check passed: admin.mesh_nodes.db_version={Version} (expected ≥ {Expected}).",
                version, ExpectedDbVersion);
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01")
        {
            // Table doesn't exist at all — even schema-init didn't run.
            logger.LogCritical(ex,
                "DB schema not initialised: admin.mesh_nodes table is missing. " +
                "The db-migration resource almost certainly never ran. " +
                "Refusing to start the portal.");
            lifetime.StopApplication();
        }
        catch (Exception ex)
        {
            // Any other connection / auth error — also fail closed. Better to
            // surface the auth/connection problem at startup than at first
            // permission check.
            logger.LogCritical(ex,
                "DB version check failed unexpectedly. Refusing to start the portal.");
            lifetime.StopApplication();
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Liveness/readiness wrapper around the same db_version check, so external
/// monitors (uptime ping, ACA platform probe) can detect a half-migrated DB
/// even after the portal somehow gets past startup.
/// </summary>
public sealed class DbVersionHealthCheck(NpgsqlDataSource dataSource) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var cmd = dataSource.CreateCommand("""
                SELECT (content->>'Version')::int FROM admin.mesh_nodes
                 WHERE id = 'db_version' AND namespace = '' LIMIT 1
                """);
            var raw = await cmd.ExecuteScalarAsync(cancellationToken);
            var version = raw switch { int v => v, long l => (int)l, _ => 0 };
            return version >= DbVersionGate.ExpectedDbVersion
                ? HealthCheckResult.Healthy($"db_version={version}")
                : HealthCheckResult.Unhealthy(
                    $"db_version={version} < expected {DbVersionGate.ExpectedDbVersion}");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("db_version check threw", ex);
        }
    }
}
