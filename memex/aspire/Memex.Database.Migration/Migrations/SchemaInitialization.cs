using MeshWeaver.Hosting.PostgreSql;
using MeshWeaver.Mesh;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Memex.Database.Migration.Migrations;

/// <summary>
/// Idempotent DB setup that ALWAYS runs (regardless of fresh vs. existing DB):
/// public-schema tables/indexes/triggers, satellite tables, partition_access stored proc,
/// the admin schema with mesh_nodes for version tracking, and the apitoken token-validation
/// index schema (must exist before any token is minted — the router no longer lazy-creates it).
///
/// New DBs get everything correct from the start. Existing DBs get updated trigger functions
/// and any newly-added objects. Reports whether the DB was fresh by detecting whether any
/// content-partition schemas existed before this run.
/// </summary>
public static class SchemaInitialization
{
    public sealed record Result(bool IsFreshDb);

    public static async Task<Result> RunAsync(
        NpgsqlDataSource dataSource,
        PostgreSqlStorageOptions options,
        string connectionString,
        ILogger logger)
    {
        // Azure-only: grant CREATE on database to azure_pg_admin so managed identities
        // (portal, migration) can create per-organization schemas at runtime.
        if (connectionString.Contains("database.azure.com"))
        {
            var dbName = new NpgsqlConnectionStringBuilder(connectionString).Database;
            await using var grantCmd = dataSource.CreateCommand(
                $"GRANT CREATE ON DATABASE \"{dbName}\" TO azure_pg_admin");
            await grantCmd.ExecuteNonQueryAsync();
            logger.LogInformation("Granted CREATE ON DATABASE to azure_pg_admin.");
        }

        // Public schema: tables, indexes, triggers + satellite tables + partition_access proc.
        await PostgreSqlSchemaInitializer.InitializeAsync(dataSource, options);

        var satelliteTableNames = PartitionDefinition.DefaultSegmentTableMappings().Values;
        await PostgreSqlSchemaInitializer.CreateSatelliteTablesAsync(
            dataSource, options, satelliteTableNames);

        await PostgreSqlSchemaInitializer.InitializePartitionAccessTableAsync(dataSource);

        // Admin schema for version tracking + global catalogs (agents/models/roles).
        // The admin partition is an unversioned mesh_nodes table just like any content
        // partition, and MUST be created in the `admin` schema — MigrationRunner.SaveVersionAsync
        // (Phase 2) writes db_version into admin.mesh_nodes, so the table has to exist first.
        //
        // The table-creation DDL uses an UNQUALIFIED `CREATE TABLE mesh_nodes`, which resolves
        // against search_path. The default `dataSource` has search_path=public, so passing it
        // here only ever (no-op) re-touched public.mesh_nodes and never created admin.mesh_nodes
        // — invisible on a long-lived prod DB where admin.mesh_nodes already exists, but on a
        // FRESH DB (new self-managed Compose/Helm deploy, brand-new Azure customer) SaveVersionAsync
        // then hit `42P01: relation "admin.mesh_nodes" does not exist`. Build an admin-scoped data
        // source (search_path=admin,public) exactly as the per-schema repairs (V02/V07/V13) and the
        // runtime PostgreSqlPartitionStorageProvider do, so the unqualified DDL lands in admin.
        await using (var ensureAdmin = dataSource.CreateCommand("CREATE SCHEMA IF NOT EXISTS admin"))
            await ensureAdmin.ExecuteNonQueryAsync();

        await using var adminDataSource = SchemaHelpers.BuildSchemaDataSource(connectionString, "admin");
        var adminOptions = SchemaHelpers.BuildSchemaOptions(connectionString, "admin", options.VectorDimensions);
        await PostgreSqlSchemaInitializer.InitializeMeshTablesAsync(adminDataSource, adminOptions);

        // ApiToken validation-index schema. ApiTokenService writes the global ApiToken/{hashPrefix}
        // index node (ApiTokenIndex → the user-scoped token node) into the `apitoken` schema, and
        // token validation reads it back by exact path on every bearer request. `ApiToken` is not
        // an OwnsPartition type and the router no longer lazily CREATE-SCHEMAs (0ceba04ce), so the
        // schema must be created EXPLICITLY here — otherwise a fresh DB (e.g. atioz) never gets it
        // and every freshly-minted token (manual AND OAuth) 401s on the next request. Uses the
        // single-source-of-truth per-partition DDL proc installed by InitializeAsync above; the
        // boot-time PostgreSqlPartitionSubscriptionHostedService also provisions it from
        // DefaultPartitionProvider, but the explicit create here covers the migration container and
        // any DB that booted before the partition was declared. Idempotent.
        await using (var ensureApiToken = dataSource.CreateCommand("SELECT public.ensure_partition_schema('apitoken')"))
            await ensureApiToken.ExecuteNonQueryAsync();

        // VUser (virtual-user) partition. VirtualUserMiddleware creates a VUser/{id} node for
        // every cookie-less request (bots, prefetchers, anonymous visitors); like ApiToken, VUser
        // is not an OwnsPartition type and the router never lazily CREATE-SCHEMAs, so without
        // this explicit create a fresh DB (e.g. atioz 2026-06-11) has no `vuser` schema and every
        // anonymous request fails its VUser create with `42P01: relation "vuser.mesh_nodes" does
        // not exist` (made loudly visible by the create fail-closed gate; before that the creates
        // were silently acked-and-lost). Same single-source-of-truth DDL proc as Space creation
        // uses. Idempotent.
        await using (var ensureVUser = dataSource.CreateCommand("SELECT public.ensure_partition_schema('vuser')"))
            await ensureVUser.ExecuteNonQueryAsync();

        // NOTE: the framework schemas `auth` (V27 access-object mirror) and `system_access`
        // (global/root-scope grants) are NOT created here. The portal's
        // PostgreSqlPartitionSubscriptionHostedService provisions them (and every other
        // registered framework partition) at boot, BEFORE any user write — so the mirror
        // trigger always has a destination. Creating them here would also make
        // DetectFreshDbAsync see `auth.mesh_nodes`/`system_access.mesh_nodes` and wrongly
        // classify a fresh DB as non-fresh, running the legacy `user`-schema repair chain
        // (V05+, which references the long-gone `user` schema) instead of fast-forwarding.

        // Detect fresh DB: no CONTENT partition schemas (i.e., no schemas with a mesh_nodes
        // table) existed before this run. Framework schemas don't count.
        var isFreshDb = await DetectFreshDbAsync(dataSource);

        return new Result(isFreshDb);
    }

    private static async Task<bool> DetectFreshDbAsync(NpgsqlDataSource dataSource)
    {
        await using var cmd = dataSource.CreateCommand("""
            SELECT count(*) FROM information_schema.schemata s
            WHERE EXISTS (
                SELECT 1 FROM information_schema.tables t
                WHERE t.table_schema = s.schema_name AND t.table_name = 'mesh_nodes'
            )
            -- Framework schemas are NOT content partitions — they must never make a fresh DB
            -- look non-fresh (which would run the legacy `user`-schema repair chain instead of
            -- fast-forwarding). Only schemas that can exist BEFORE the first migration run
            -- belong here: public (root), admin (created by this very function), and the
            -- partitions this function provisions explicitly (apitoken, vuser) plus auth
            -- (access-object mirror). The DbVersionGate keeps the portal from boot-provisioning
            -- anything else ahead of the migration, so the former system_*/portal/kernel/pg_*
            -- entries were dead defensiveness and are gone.
            AND s.schema_name NOT IN ('public', 'admin', 'auth', 'apitoken', 'vuser')
            AND s.schema_name NOT LIKE '%\_versions' ESCAPE '\'
            """);
        var schemaCount = (long)(await cmd.ExecuteScalarAsync())!;
        return schemaCount == 0;
    }
}
