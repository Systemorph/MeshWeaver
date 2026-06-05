using MeshWeaver.Hosting.PostgreSql;
using MeshWeaver.Mesh;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Memex.Database.Migration.Migrations;

/// <summary>
/// Idempotent DB setup that ALWAYS runs (regardless of fresh vs. existing DB):
/// public-schema tables/indexes/triggers, satellite tables, partition_access stored proc,
/// and the admin schema with mesh_nodes for version tracking.
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

        var satelliteTableNames = PartitionDefinition.StandardTableMappings.Values;
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

        // Auth mirror schema. The V27 mirror trigger (mirror_access_object_to_auth_schema)
        // inserts User/Group/Role/VUser/ApiToken/Space rows into auth.mesh_nodes. On a FRESH
        // DB there is no legacy `user` schema for V27 to rename, and the storage router no
        // longer lazily CREATE SCHEMAs on first write — so create `auth` eagerly here via the
        // same stored proc every partition uses, so the trigger always has a destination.
        // Trigger-populated only; application code never writes to auth.
        await using (var ensureAuth = dataSource.CreateCommand("SELECT public.ensure_partition_schema('auth')"))
            await ensureAuth.ExecuteNonQueryAsync();

        // Global access-grant schema. Root-scope / global AccessAssignments (namespace `_Access`,
        // MainNode="") persist into system_access.access — the platform-admin / global-viewer
        // grant scope SecurityService reads. Created eagerly (no lazy create); ensure_partition_schema
        // provisions the full standard table set, of which `access` is the one used.
        await using (var ensureSysAccess = dataSource.CreateCommand("SELECT public.ensure_partition_schema('system_access')"))
            await ensureSysAccess.ExecuteNonQueryAsync();

        // Detect fresh DB: no partition schemas (i.e., no schemas with a mesh_nodes table)
        // existed before this run. The admin schema doesn't count.
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
            AND s.schema_name NOT IN ('public', 'admin', 'information_schema', 'pg_catalog', 'pg_toast')
            AND s.schema_name NOT LIKE '%\_versions' ESCAPE '\'
            """);
        var schemaCount = (long)(await cmd.ExecuteScalarAsync())!;
        return schemaCount == 0;
    }
}
