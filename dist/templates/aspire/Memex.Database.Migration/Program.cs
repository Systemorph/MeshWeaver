using Memex.Portal.ServiceDefaults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MeshWeaver.Hosting.PostgreSql;
using MeshWeaver.Mesh;
using Npgsql;

Console.WriteLine("[Migration] Starting...");
var builder = Host.CreateApplicationBuilder(args);
builder.AddServiceDefaults();

var connectionString = builder.Configuration.GetConnectionString("memex") ?? "";
Console.WriteLine($"[Migration] ConnectionString: {(string.IsNullOrEmpty(connectionString) ? "(empty)" : connectionString[..Math.Min(30, connectionString.Length)] + "...")}");
if (connectionString.Contains("database.azure.com"))
    builder.AddAzureNpgsqlDataSource("memex");
else
    builder.AddNpgsqlDataSource("memex");

// Derive vector dimensions from embedding model (passed by AppHost via Embedding__Model)
var embeddingOptions = builder.Configuration.GetSection("Embedding").Get<EmbeddingOptions>() ?? new EmbeddingOptions();
builder.Services.Configure<PostgreSqlStorageOptions>(o =>
{
    o.ConnectionString = connectionString;
    o.VectorDimensions = embeddingOptions.Dimensions;
});

Console.WriteLine("[Migration] Building host...");
var host = builder.Build();
Console.WriteLine("[Migration] Host built. Resolving services...");

var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Migration");
Console.WriteLine("[Migration] Resolving NpgsqlDataSource...");
var dataSource = host.Services.GetRequiredService<NpgsqlDataSource>();
Console.WriteLine("[Migration] NpgsqlDataSource resolved.");
var options = host.Services.GetRequiredService<IOptions<PostgreSqlStorageOptions>>();

logger.LogInformation("Running database migration...");

// Grant CREATE on database to azure_pg_admin role so managed identities
// (portal, migration) can create per-organization schemas at runtime.
if (connectionString.Contains("database.azure.com"))
{
    await using var grantCmd = dataSource.CreateCommand(
        "GRANT CREATE ON DATABASE memex TO azure_pg_admin");
    await grantCmd.ExecuteNonQueryAsync();
    logger.LogInformation("Granted CREATE ON DATABASE to azure_pg_admin.");
}

// ═══════════════════════════════════════════════════════════════════════════
// Schema initialization — always runs, idempotent (CREATE IF NOT EXISTS).
// Sets up tables, indexes, triggers, and satellite tables in the public schema.
// New DBs get everything correct from the start.
// Existing DBs get updated trigger functions (e.g., fixed role flag values).
// ═══════════════════════════════════════════════════════════════════════════

await PostgreSqlSchemaInitializer.InitializeAsync(dataSource, options.Value);

var satelliteTableNames = MeshWeaver.Mesh.PartitionDefinition.StandardTableMappings.Values;
await PostgreSqlSchemaInitializer.CreateSatelliteTablesAsync(
    dataSource, options.Value, satelliteTableNames);

await PostgreSqlSchemaInitializer.InitializePartitionAccessTableAsync(dataSource);

// ═══════════════════════════════════════════════════════════════════════════
// Versioned migrations — tracked in admin.mesh_nodes as MeshNode(id="db_version").
//
// Two categories:
//   (a) Schema migrations: structural changes needed for both new and existing DBs.
//       These go into PostgreSqlSchemaInitializer (idempotent, always run).
//   (b) Data repairs: fix data written incorrectly by prior code versions.
//       These go here as versioned migrations — only run once, only needed
//       for existing DBs. New DBs never have the bad data.
// ═══════════════════════════════════════════════════════════════════════════

// Ensure admin schema exists for version tracking
await using (var ensureAdmin = dataSource.CreateCommand("CREATE SCHEMA IF NOT EXISTS admin"))
{
    await ensureAdmin.ExecuteNonQueryAsync();
}

// Ensure admin.mesh_nodes exists (may not if this is a fresh DB before partition init)
await PostgreSqlSchemaInitializer.InitializeMeshTablesAsync(
    dataSource, options.Value);

// Read current DB version (0 = fresh DB or pre-versioning)
int currentVersion = 0;
try
{
    await using var readVersion = dataSource.CreateCommand("""
        SELECT (content->>'Version')::int FROM admin.mesh_nodes
        WHERE id = 'db_version' AND namespace = '' LIMIT 1
        """);
    var result = await readVersion.ExecuteScalarAsync();
    if (result is int v) currentVersion = v;
    else if (result is long l) currentVersion = (int)l;
}
catch
{
    // Table may not exist yet — version = 0 (fresh DB)
}

logger.LogInformation("Current DB version: {Version}", currentVersion);

// Detect fresh DB (no partition schemas exist yet)
bool isFreshDb;
await using (var checkSchemas = dataSource.CreateCommand("""
    SELECT count(*) FROM information_schema.schemata s
    WHERE EXISTS (
        SELECT 1 FROM information_schema.tables t
        WHERE t.table_schema = s.schema_name AND t.table_name = 'mesh_nodes'
    )
    AND s.schema_name NOT IN ('public', 'information_schema', 'pg_catalog', 'pg_toast')
    AND s.schema_name NOT LIKE '%\_versions' ESCAPE '\'
    """))
{
    var schemaCount = (long)(await checkSchemas.ExecuteScalarAsync())!;
    isFreshDb = schemaCount == 0;
}

if (isFreshDb)
{
    logger.LogInformation("Fresh database detected — skipping data repairs (no existing data to fix).");
    currentVersion = 1; // Skip all repair migrations
}

// ── Data repair v1: Move AccessAssignments to correct table + namespace ──
// Bug: AddUserRoleAsync wrote AccessAssignment nodes to mesh_nodes (wrong table)
// with namespace={scope}/{userId}_Access (missing _Access segment).
// Fix: Move to access table, add _Access to namespace, rebuild permissions.
if (currentVersion < 1)
{
    logger.LogInformation("Running repair v1: Move AccessAssignments to access table with _Access namespace...");
    await using (var cmd = dataSource.CreateCommand("""
        DO $$
        DECLARE
            schema_rec RECORD;
            moved_count INT;
            ns_count INT;
            cols TEXT := 'namespace, id, name, node_type, description, category, icon, display_order, last_modified, version, state, content, desired_id, main_node, embedding';
        BEGIN
            FOR schema_rec IN
                SELECT schema_name FROM information_schema.schemata s
                WHERE EXISTS (SELECT 1 FROM information_schema.tables t WHERE t.table_schema = s.schema_name AND t.table_name = 'mesh_nodes')
                AND EXISTS (SELECT 1 FROM information_schema.tables t WHERE t.table_schema = s.schema_name AND t.table_name = 'access')
                AND s.schema_name NOT IN ('public', 'information_schema', 'pg_catalog', 'pg_toast')
                AND s.schema_name NOT LIKE '%\_versions' ESCAPE '\'
            LOOP
                -- Move AccessAssignments from mesh_nodes to access table
                EXECUTE format(
                    'INSERT INTO %I.access (' || cols || ') SELECT ' || cols || ' FROM %I.mesh_nodes WHERE node_type = ''AccessAssignment'' ON CONFLICT (namespace, id) DO NOTHING',
                    schema_rec.schema_name, schema_rec.schema_name
                );
                GET DIAGNOSTICS moved_count = ROW_COUNT;
                IF moved_count > 0 THEN
                    EXECUTE format(
                        'DELETE FROM %I.mesh_nodes WHERE node_type = ''AccessAssignment''',
                        schema_rec.schema_name
                    );
                    RAISE NOTICE 'Schema %: moved % AccessAssignment(s) from mesh_nodes to access', schema_rec.schema_name, moved_count;
                END IF;

                -- Fix namespace: ensure _Access segment is present
                EXECUTE format(
                    'UPDATE %I.access SET namespace = namespace || ''/_Access'' WHERE node_type = ''AccessAssignment'' AND namespace NOT LIKE ''%%/_Access''',
                    schema_rec.schema_name
                );
                GET DIAGNOSTICS ns_count = ROW_COUNT;
                IF ns_count > 0 THEN
                    RAISE NOTICE 'Schema %: fixed % namespace(s) to include /_Access', schema_rec.schema_name, ns_count;
                END IF;

                -- Rebuild permissions
                BEGIN
                    EXECUTE format('SELECT %I.rebuild_user_effective_permissions()', schema_rec.schema_name);
                EXCEPTION WHEN OTHERS THEN
                    RAISE NOTICE 'Schema %: rebuild failed: %', schema_rec.schema_name, SQLERRM;
                END;
            END LOOP;
        END $$;
        """))
    {
        await cmd.ExecuteNonQueryAsync();
    }

    currentVersion = 1;
    logger.LogInformation("Repair v1 completed.");
}

// ── Data repair v2: Re-create trigger functions + populate partition_access ──
// The schema initializer now includes partition_access sync in
// rebuild_user_effective_permissions() with hardcoded schema name.
// For existing DBs: re-run schema init per schema to update the function,
// then rebuild permissions which populates partition_access.
if (currentVersion < 2)
{
    logger.LogInformation("Running repair v2: Update trigger functions and populate partition_access...");

    // Discover existing partition schemas
    var schemas = new List<string>();
    await using (var listCmd = dataSource.CreateCommand("""
        SELECT schema_name FROM information_schema.schemata s
        WHERE EXISTS (SELECT 1 FROM information_schema.tables t WHERE t.table_schema = s.schema_name AND t.table_name = 'mesh_nodes')
        AND s.schema_name NOT IN ('public', 'information_schema', 'pg_catalog', 'pg_toast')
        AND s.schema_name NOT LIKE '%\_versions' ESCAPE '\'
        ORDER BY s.schema_name
        """))
    {
        await using var rdr = await listCmd.ExecuteReaderAsync();
        while (await rdr.ReadAsync()) schemas.Add(rdr.GetString(0));
    }

    foreach (var schema in schemas)
    {
        logger.LogInformation("Repair v2: Updating trigger function for schema {Schema}...", schema);

        // Build a temporary SearchPath data source to re-create the trigger function
        var csb = new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = $"{schema},public" };
        var dsb = new NpgsqlDataSourceBuilder(csb.ConnectionString);
        dsb.UseVector();
        await using var schemaDs = dsb.Build();

        // Check if this schema has a versions schema
        var versionsSchema = schema + "_versions";
        bool hasVersions;
        await using (var checkCmd = dataSource.CreateCommand(
            "SELECT EXISTS (SELECT 1 FROM information_schema.schemata WHERE schema_name = $1)"))
        {
            checkCmd.Parameters.AddWithValue(versionsSchema);
            hasVersions = (bool)(await checkCmd.ExecuteScalarAsync())!;
        }

        var schemaOpts = new PostgreSqlStorageOptions
        {
            ConnectionString = csb.ConnectionString,
            VectorDimensions = options.Value.VectorDimensions,
            Schema = schema
        };

        if (hasVersions)
        {
            var vCsb = new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = $"{versionsSchema},public" };
            await using var versionsDs = new NpgsqlDataSourceBuilder(vCsb.ConnectionString).Build();
            await PostgreSqlSchemaInitializer.InitializeWithVersionsSchemaAsync(
                dataSource, schemaDs, versionsDs, schemaOpts, versionsSchema);
        }
        else
        {
            await PostgreSqlSchemaInitializer.InitializeMeshTablesAsync(schemaDs, schemaOpts);
        }

        // Now rebuild permissions — the updated function will populate partition_access
        try
        {
            await using var rebuildCmd = dataSource.CreateCommand(
                $"SELECT \"{schema}\".rebuild_user_effective_permissions()");
            await rebuildCmd.ExecuteNonQueryAsync();
            logger.LogInformation("Repair v2: Schema {Schema} — rebuilt permissions + partition_access", schema);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Repair v2: Schema {Schema} — rebuild failed", schema);
        }
    }

    currentVersion = 2;
    logger.LogInformation("Repair v2 completed.");
}

// ── Data repair v3: Drop rogue schemas created from path segments ──
// Bug: paths like "login", "markdown", "onboarding" etc. created schemas
// that shouldn't exist as partitions. Drop them to keep discovery clean.
if (currentVersion < 3)
{
    logger.LogInformation("Running repair v3: Drop rogue schemas...");
    var rogueSchemas = new[] {
        "_access", "_address_", "_graph", "_settings", "_tracking", "_thread", "_source", "_test",
        "login", "markdown", "onboarding", "welcome", "settings", "storage",
        "p", "mesh", "thread", "agent", "partition", "organization", "vuser"
    };
    foreach (var rogue in rogueSchemas)
    {
        try
        {
            await using var dropCmd = dataSource.CreateCommand($"DROP SCHEMA IF EXISTS \"{rogue}\" CASCADE");
            await dropCmd.ExecuteNonQueryAsync();
            await using var dropVCmd = dataSource.CreateCommand($"DROP SCHEMA IF EXISTS \"{rogue}_versions\" CASCADE");
            await dropVCmd.ExecuteNonQueryAsync();
            logger.LogInformation("Repair v3: Dropped rogue schema {Schema}", rogue);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Repair v3: Failed to drop schema {Schema}", rogue);
        }
    }
    currentVersion = 3;
    logger.LogInformation("Repair v3 completed.");
}

// ── Data repair v4: Upgrade user self-assignments from Viewer to Admin ──
// UserScopeGrantHandler previously granted Viewer on User/{userId}.
// Now grants Admin so users can fully manage their own namespace.
if (currentVersion < 4)
{
    logger.LogInformation("Running repair v4: Upgrade user self-assignments from Viewer to Admin...");
    await using (var cmd = dataSource.CreateCommand("""
        DO $$
        DECLARE
            schema_rec RECORD;
            updated_count INT;
        BEGIN
            FOR schema_rec IN
                SELECT schema_name FROM information_schema.schemata s
                WHERE EXISTS (SELECT 1 FROM information_schema.tables t WHERE t.table_schema = s.schema_name AND t.table_name = 'access')
                AND s.schema_name NOT IN ('public', 'information_schema', 'pg_catalog', 'pg_toast')
                AND s.schema_name NOT LIKE '%\_versions' ESCAPE '\'
            LOOP
                -- Update self-assignments: namespace=User/{id}/_Access, accessObject={id}
                -- Replace Viewer with Admin in the roles array for self-assignments only
                EXECUTE format(
                    'UPDATE %I.access
                     SET content = jsonb_set(
                         content,
                         ''{roles}'',
                         (SELECT jsonb_agg(
                             CASE WHEN elem->>''role'' = ''Viewer''
                                  THEN jsonb_set(elem, ''{role}'', ''"Admin"'')
                                  ELSE elem
                             END
                         ) FROM jsonb_array_elements(content->''roles'') AS elem)
                     )
                     WHERE node_type = ''AccessAssignment''
                       AND namespace LIKE ''User/%%/_Access''
                       AND namespace = ''User/'' || (content->>''accessObject'') || ''/_Access''
                       AND EXISTS (SELECT 1 FROM jsonb_array_elements(content->''roles'') r WHERE r->>''role'' = ''Viewer'')
                       AND NOT EXISTS (SELECT 1 FROM jsonb_array_elements(content->''roles'') r WHERE r->>''role'' = ''Admin'')',
                    schema_rec.schema_name
                );
                GET DIAGNOSTICS updated_count = ROW_COUNT;
                IF updated_count > 0 THEN
                    RAISE NOTICE 'Schema %: upgraded % self-assignment(s) from Viewer to Admin', schema_rec.schema_name, updated_count;
                END IF;

                -- Rebuild permissions
                BEGIN
                    EXECUTE format('SELECT %I.rebuild_user_effective_permissions()', schema_rec.schema_name);
                EXCEPTION WHEN OTHERS THEN
                    RAISE NOTICE 'Schema %: rebuild failed: %', schema_rec.schema_name, SQLERRM;
                END;
            END LOOP;
        END $$;
        """))
    {
        await cmd.ExecuteNonQueryAsync();
    }

    currentVersion = 4;
    logger.LogInformation("Repair v4 completed.");
}

// ── Always: populate searchable_schemas from remaining content partitions ──
// This runs every time (not versioned) since it's idempotent and schemas may change.
{
    // Discover content schemas (same logic as PostgreSqlPartitionedStoreFactory.DiscoverPartitionsAsync)
    var contentSchemas = new List<string>();
    var excludedSchemas = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "admin", "portal", "kernel",
        "_access", "_address_", "_graph", "_settings", "_tracking", "_thread", "_source", "_test",
        "login", "markdown", "onboarding", "welcome", "settings", "storage",
        "p", "mesh", "thread", "agent", "partition", "organization", "vuser",
        "public", "information_schema", "pg_catalog", "pg_toast"
    };

    await using (var discoverCmd = dataSource.CreateCommand("""
        SELECT schema_name FROM information_schema.schemata s
        WHERE EXISTS (SELECT 1 FROM information_schema.tables t WHERE t.table_schema = s.schema_name AND t.table_name = 'mesh_nodes')
        AND s.schema_name NOT IN ('public', 'information_schema', 'pg_catalog', 'pg_toast')
        AND s.schema_name NOT LIKE '%\_versions' ESCAPE '\'
        ORDER BY s.schema_name
        """))
    {
        await using var rdr = await discoverCmd.ExecuteReaderAsync();
        while (await rdr.ReadAsync())
        {
            var schema = rdr.GetString(0);
            if (!excludedSchemas.Contains(schema))
                contentSchemas.Add(schema);
        }
    }

    // Populate searchable_schemas
    await using (var clearCmd = dataSource.CreateCommand("DELETE FROM public.searchable_schemas"))
        await clearCmd.ExecuteNonQueryAsync();

    foreach (var schema in contentSchemas)
    {
        await using var insertCmd = dataSource.CreateCommand(
            "INSERT INTO public.searchable_schemas (schema_name) VALUES ($1) ON CONFLICT DO NOTHING");
        insertCmd.Parameters.AddWithValue(schema);
        await insertCmd.ExecuteNonQueryAsync();
    }

    logger.LogInformation("Searchable schemas: [{Schemas}]", string.Join(", ", contentSchemas));
}

// Save current version
await using (var saveVersion = dataSource.CreateCommand("""
    INSERT INTO admin.mesh_nodes (namespace, id, name, node_type, state, content, last_modified, main_node)
    VALUES ('', 'db_version', 'Database Version', 'Settings', 2,
            jsonb_build_object('Version', @version, 'LastMigration', now()::text),
            now(), 'db_version')
    ON CONFLICT (namespace, id) DO UPDATE SET
        content = jsonb_build_object('Version', @version, 'LastMigration', now()::text),
        last_modified = now()
    """))
{
    saveVersion.Parameters.AddWithValue("@version", currentVersion);
    await saveVersion.ExecuteNonQueryAsync();
}

logger.LogInformation("Database migration completed. Version: {Version}", currentVersion);

// Signal completion to Aspire (health check passes, then process exits cleanly)
await host.StartAsync();
await host.StopAsync();
