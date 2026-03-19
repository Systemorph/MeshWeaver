using Npgsql;

namespace MeshWeaver.Hosting.PostgreSql;

/// <summary>
/// Creates the PostgreSQL schema (tables, indexes, triggers) if not already present.
/// </summary>
public static class PostgreSqlSchemaInitializer
{
    /// <summary>
    /// Creates the partition_access table in the public schema.
    /// This table maps users to partition schemas they can access, populated by
    /// rebuild_user_effective_permissions() trigger in each partition schema.
    /// </summary>
    public static async Task InitializePartitionAccessTableAsync(NpgsqlDataSource dataSource, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand("""
            CREATE TABLE IF NOT EXISTS public.partition_access (
                user_id    TEXT NOT NULL,
                partition  TEXT NOT NULL,
                PRIMARY KEY (user_id, partition)
            );
            CREATE INDEX IF NOT EXISTS idx_partition_access_user ON public.partition_access (user_id);
            """);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Ensures the vector extension exists and initializes the full schema.
    /// Creates the extension as plain SQL first, then reloads types so the
    /// UseVector() plugin can resolve the vector OID for parameterized queries.
    /// </summary>
    public static async Task InitializeAsync(NpgsqlDataSource dataSource, PostgreSqlStorageOptions options, CancellationToken ct = default)
    {
        // Step 1: Create the vector extension using plain SQL (no vector parameters).
        // Even if UseVector() can't find the type yet, plain SQL commands work fine.
        await using (var cmd = dataSource.CreateCommand("CREATE EXTENSION IF NOT EXISTS vector"))
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Step 2: Reload the type catalog so UseVector() picks up the new vector type.
        await using (var conn = await dataSource.OpenConnectionAsync(ct))
        {
            await conn.ReloadTypesAsync();
        }

        // Step 3: Run the full schema script (tables, indexes, triggers).
        await using (var cmd = dataSource.CreateCommand(GetSchemaScript(options)))
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>
    /// Initializes mesh tables only (no history/versioning). Used for unversioned partitions (Portal, Kernel).
    /// </summary>
    public static async Task InitializeMeshTablesAsync(
        NpgsqlDataSource schemaDataSource, PostgreSqlStorageOptions options, CancellationToken ct = default)
    {
        await using (var conn = await schemaDataSource.OpenConnectionAsync(ct))
        {
            await conn.ReloadTypesAsync();
        }

        await using (var cmd = schemaDataSource.CreateCommand(GetUnversionedSchemaScript(options)))
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>
    /// Initializes mesh tables in the org schema and mesh_node_history in a separate versions schema.
    /// The trigger on mesh_nodes writes cross-schema to {versionsSchema}.mesh_node_history.
    /// </summary>
    public static async Task InitializeWithVersionsSchemaAsync(
        NpgsqlDataSource baseDataSource,
        NpgsqlDataSource schemaDataSource,
        NpgsqlDataSource versionsDataSource,
        PostgreSqlStorageOptions options,
        string versionsSchema,
        CancellationToken ct = default)
    {
        // Step 1: Create the vector extension using plain SQL
        await using (var cmd = baseDataSource.CreateCommand("CREATE EXTENSION IF NOT EXISTS vector"))
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Step 2: Reload types on both data sources
        await using (var conn = await schemaDataSource.OpenConnectionAsync(ct))
        {
            await conn.ReloadTypesAsync();
        }

        // Step 3: Create versions schema tables (mesh_node_history)
        await using (var cmd = versionsDataSource.CreateCommand(GetVersionsSchemaScript()))
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Step 4: Create mesh schema tables + cross-schema trigger
        await using (var cmd = schemaDataSource.CreateCommand(GetMeshSchemaScript(options, versionsSchema)))
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>
    /// Creates satellite tables (same structure as mesh_nodes) for satellite node types.
    /// Each table name comes from the PartitionDefinition.TableMappings values.
    /// </summary>
    public static async Task CreateSatelliteTablesAsync(
        NpgsqlDataSource schemaDataSource,
        PostgreSqlStorageOptions options,
        IEnumerable<string> tableNames,
        CancellationToken ct = default)
    {
        var dim = options.VectorDimensions;
        foreach (var tableName in tableNames.Distinct())
        {
            var sql = $$"""
                CREATE TABLE IF NOT EXISTS "{{tableName}}" (
                    namespace       TEXT        NOT NULL DEFAULT '',
                    id              TEXT        NOT NULL,
                    path            TEXT        GENERATED ALWAYS AS (
                                        CASE WHEN namespace = '' THEN id ELSE namespace || '/' || id END
                                    ) STORED,
                    name            TEXT,
                    node_type       TEXT,
                    description     TEXT,
                    category        TEXT,
                    icon            TEXT,
                    display_order   INTEGER,
                    last_modified   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                    version         BIGINT      NOT NULL DEFAULT 0,
                    state           SMALLINT    NOT NULL DEFAULT 0,
                    content         JSONB,
                    desired_id      TEXT,
                    main_node       TEXT,
                    embedding       vector({{dim}}),
                    PRIMARY KEY (namespace, id)
                );

                CREATE INDEX IF NOT EXISTS "idx_{{tableName}}_path" ON "{{tableName}}" (path);
                CREATE INDEX IF NOT EXISTS "idx_{{tableName}}_main_node" ON "{{tableName}}" (main_node);
                CREATE INDEX IF NOT EXISTS "idx_{{tableName}}_node_type" ON "{{tableName}}" (node_type);
                CREATE INDEX IF NOT EXISTS "idx_{{tableName}}_last_modified" ON "{{tableName}}" (last_modified DESC);
                """;

            await using var cmd = schemaDataSource.CreateCommand(sql);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>
    /// Returns SQL for the versions schema: mesh_node_history table + indexes.
    /// </summary>
    internal static string GetVersionsSchemaScript()
    {
        return """
            -- mesh_node_history: versioned copies of mesh_nodes (PK includes version)
            CREATE TABLE IF NOT EXISTS mesh_node_history (
                namespace       TEXT        NOT NULL DEFAULT '',
                id              TEXT        NOT NULL,
                path            TEXT        GENERATED ALWAYS AS (
                                    CASE WHEN namespace = '' THEN id ELSE namespace || '/' || id END
                                ) STORED,
                name            TEXT,
                node_type       TEXT,
                description     TEXT,
                category        TEXT,
                icon            TEXT,
                display_order   INTEGER,
                last_modified   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                version         BIGINT      NOT NULL DEFAULT 0,
                state           SMALLINT    NOT NULL DEFAULT 0,
                content         JSONB,
                desired_id      TEXT,
                changed_by      TEXT,
                main_node       TEXT,
                PRIMARY KEY (namespace, id, version)
            );

            CREATE INDEX IF NOT EXISTS idx_mnh_path ON mesh_node_history (path);
            CREATE INDEX IF NOT EXISTS idx_mnh_path_version ON mesh_node_history (path, version DESC);
            """;
    }

    /// <summary>
    /// Returns SQL for the mesh schema: all tables except mesh_node_history,
    /// with a trigger that writes cross-schema to {versionsSchema}.mesh_node_history.
    /// </summary>
    internal static string GetMeshSchemaScript(PostgreSqlStorageOptions options, string versionsSchema)
    {
        var dim = options.VectorDimensions;
        var schemaName = options.Schema ?? "public";
        return $$"""
            CREATE EXTENSION IF NOT EXISTS vector;

            -- mesh_nodes
            CREATE TABLE IF NOT EXISTS mesh_nodes (
                namespace       TEXT        NOT NULL DEFAULT '',
                id              TEXT        NOT NULL,
                path            TEXT        GENERATED ALWAYS AS (
                                    CASE WHEN namespace = '' THEN id ELSE namespace || '/' || id END
                                ) STORED,
                name            TEXT,
                node_type       TEXT,
                description     TEXT,
                category        TEXT,
                icon            TEXT,
                display_order   INTEGER,
                last_modified   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                version         BIGINT      NOT NULL DEFAULT 0,
                state           SMALLINT    NOT NULL DEFAULT 0,
                content         JSONB,
                desired_id      TEXT,
                main_node       TEXT,
                embedding       vector({{dim}}),
                PRIMARY KEY (namespace, id)
            );

            CREATE INDEX IF NOT EXISTS idx_mn_path ON mesh_nodes (path);
            CREATE INDEX IF NOT EXISTS idx_mn_path_prefix ON mesh_nodes (path text_pattern_ops);
            CREATE INDEX IF NOT EXISTS idx_mn_namespace ON mesh_nodes (namespace);
            CREATE INDEX IF NOT EXISTS idx_mn_node_type ON mesh_nodes (node_type);
            CREATE INDEX IF NOT EXISTS idx_mn_content ON mesh_nodes USING gin (content jsonb_path_ops);
            CREATE INDEX IF NOT EXISTS idx_mn_text_search ON mesh_nodes USING gin (
                to_tsvector('english', COALESCE(name,'') || ' ' || COALESCE(description,'') || ' ' || COALESCE(node_type,''))
            );
            CREATE INDEX IF NOT EXISTS idx_mn_embedding ON mesh_nodes USING hnsw (embedding vector_cosine_ops);

            -- Migrate embedding column if dimensions changed
            DO $migrate$
            DECLARE cur_dim INT;
            BEGIN
                SELECT atttypmod INTO cur_dim FROM pg_attribute
                WHERE attrelid = 'mesh_nodes'::regclass AND attname = 'embedding' AND atttypmod > 0;
                IF cur_dim IS NOT NULL AND cur_dim != {{dim}} THEN
                    DROP INDEX IF EXISTS idx_mn_embedding;
                    ALTER TABLE mesh_nodes ALTER COLUMN embedding TYPE vector({{dim}}) USING NULL;
                    CREATE INDEX idx_mn_embedding ON mesh_nodes USING hnsw (embedding vector_cosine_ops);
                END IF;
            END $migrate$;

            -- partition_objects
            CREATE TABLE IF NOT EXISTS partition_objects (
                id              TEXT        NOT NULL,
                partition_key   TEXT        NOT NULL,
                type_name       TEXT,
                data            JSONB       NOT NULL,
                last_modified   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                PRIMARY KEY (partition_key, id)
            );

            -- user_activity
            CREATE TABLE IF NOT EXISTS user_activity (
                user_id         TEXT        NOT NULL,
                node_path       TEXT        NOT NULL,
                activity_type   SMALLINT    NOT NULL DEFAULT 0,
                first_accessed  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                last_accessed   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                access_count    INTEGER     NOT NULL DEFAULT 1,
                node_name       TEXT,
                node_type       TEXT,
                namespace       TEXT,
                PRIMARY KEY (user_id, node_path)
            );
            CREATE INDEX IF NOT EXISTS idx_ua_user_last ON user_activity (user_id, last_accessed DESC);
            CREATE INDEX IF NOT EXISTS idx_ua_node_type ON user_activity (user_id, node_type);

            -- change_logs (bundled activity logs)
            CREATE TABLE IF NOT EXISTS change_logs (
                id              TEXT        NOT NULL PRIMARY KEY,
                hub_path        TEXT        NOT NULL,
                changed_by      TEXT,
                category        TEXT        NOT NULL,
                start_time      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                end_time        TIMESTAMPTZ,
                change_count    INTEGER     NOT NULL DEFAULT 0,
                status          SMALLINT    NOT NULL DEFAULT 1,
                messages        JSONB
            );
            CREATE INDEX IF NOT EXISTS idx_cl_hub ON change_logs (hub_path, start_time DESC);
            CREATE INDEX IF NOT EXISTS idx_cl_user ON change_logs (changed_by, start_time DESC);

            -- user_effective_permissions (denormalized)
            CREATE TABLE IF NOT EXISTS user_effective_permissions (
                user_id          TEXT     NOT NULL,
                node_path_prefix TEXT     NOT NULL,
                permission       TEXT     NOT NULL,
                is_allow         BOOLEAN  NOT NULL,
                PRIMARY KEY (user_id, node_path_prefix, permission)
            );
            CREATE INDEX IF NOT EXISTS idx_uep_user_perm ON user_effective_permissions (user_id, permission);
            CREATE INDEX IF NOT EXISTS idx_uep_path ON user_effective_permissions (node_path_prefix text_pattern_ops);

            -- Shadow table for atomic rebuild
            CREATE TABLE IF NOT EXISTS user_effective_permissions_shadow (LIKE user_effective_permissions INCLUDING ALL);

            -- Rebuild function: reads AccessAssignment from access satellite table, GroupMembership from mesh_nodes
            -- AccessAssignment content: {"accessObject":"...","roles":[{"role":"...","denied":true},...]}
            CREATE OR REPLACE FUNCTION rebuild_user_effective_permissions() RETURNS void AS $$
            BEGIN
                TRUNCATE user_effective_permissions_shadow;

                -- Direct entries from AccessAssignment nodes (access satellite table)
                INSERT INTO user_effective_permissions_shadow (user_id, node_path_prefix, permission, is_allow)
                SELECT
                    aa.content->>'accessObject' AS user_id,
                    COALESCE(aa.main_node, aa.namespace) AS node_path_prefix,
                    perm.permission,
                    NOT COALESCE((role_entry->>'denied')::boolean, false) AS is_allow
                FROM access aa
                CROSS JOIN LATERAL jsonb_array_elements(aa.content->'roles') AS role_entry
                CROSS JOIN LATERAL (
                    SELECT COALESCE(
                        (SELECT (role_node.content->>'permissions')::int
                         FROM mesh_nodes role_node
                         WHERE role_node.node_type = 'Role'
                           AND role_node.id = role_entry->>'role'
                         LIMIT 1),
                        CASE role_entry->>'role'
                            WHEN 'Admin' THEN 63
                            WHEN 'PlatformAdmin' THEN 63
                            WHEN 'Editor' THEN 55
                            WHEN 'Viewer' THEN 33
                            WHEN 'Commenter' THEN 49
                            ELSE 0
                        END
                    ) AS permissions
                ) r
                CROSS JOIN LATERAL (
                    SELECT unnest(
                        CASE WHEN (r.permissions & 1) > 0 THEN ARRAY['Read'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 2) > 0 THEN ARRAY['Create'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 4) > 0 THEN ARRAY['Update'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 8) > 0 THEN ARRAY['Delete'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 16) > 0 THEN ARRAY['Comment'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 32) > 0 THEN ARRAY['Execute'] ELSE ARRAY[]::text[] END
                    ) AS permission
                ) perm
                WHERE aa.content->>'accessObject' IS NOT NULL
                  AND aa.content->'roles' IS NOT NULL
                ON CONFLICT (user_id, node_path_prefix, permission) DO UPDATE
                    SET is_allow = CASE WHEN EXCLUDED.is_allow = false THEN false ELSE user_effective_permissions_shadow.is_allow END;

                -- Group expansion: read GroupMembership MeshNodes
                INSERT INTO user_effective_permissions_shadow (user_id, node_path_prefix, permission, is_allow)
                WITH RECURSIVE all_members AS (
                    SELECT group_entry->>'group' AS group_path, gm.content->>'member' AS member_id
                    FROM mesh_nodes gm
                    CROSS JOIN LATERAL jsonb_array_elements(gm.content->'groups') AS group_entry
                    WHERE gm.node_type = 'GroupMembership'
                    UNION
                    SELECT am.group_path, gm.content->>'member'
                    FROM all_members am
                    JOIN mesh_nodes gm ON gm.node_type = 'GroupMembership'
                    WHERE EXISTS (
                        SELECT 1 FROM jsonb_array_elements(gm.content->'groups') g
                        WHERE g->>'group' = am.member_id
                    )
                ),
                leaf_members AS (
                    SELECT group_path, member_id FROM all_members
                    WHERE NOT EXISTS (
                        SELECT 1 FROM mesh_nodes gm2
                        CROSS JOIN LATERAL jsonb_array_elements(gm2.content->'groups') AS g2
                        WHERE gm2.node_type = 'GroupMembership'
                          AND g2->>'group' = all_members.member_id
                    )
                )
                SELECT lm.member_id, COALESCE(aa.main_node, aa.namespace), perm.permission,
                       NOT COALESCE((role_entry->>'denied')::boolean, false) AS is_allow
                FROM access aa
                JOIN leaf_members lm ON aa.content->>'accessObject' = lm.group_path
                CROSS JOIN LATERAL jsonb_array_elements(aa.content->'roles') AS role_entry
                CROSS JOIN LATERAL (
                    SELECT COALESCE(
                        (SELECT (role_node.content->>'permissions')::int
                         FROM mesh_nodes role_node
                         WHERE role_node.node_type = 'Role'
                           AND role_node.id = role_entry->>'role'
                         LIMIT 1),
                        CASE role_entry->>'role'
                            WHEN 'Admin' THEN 63
                            WHEN 'PlatformAdmin' THEN 63
                            WHEN 'Editor' THEN 55
                            WHEN 'Viewer' THEN 33
                            WHEN 'Commenter' THEN 49
                            ELSE 0
                        END
                    ) AS permissions
                ) r
                CROSS JOIN LATERAL (
                    SELECT unnest(
                        CASE WHEN (r.permissions & 1) > 0 THEN ARRAY['Read'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 2) > 0 THEN ARRAY['Create'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 4) > 0 THEN ARRAY['Update'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 8) > 0 THEN ARRAY['Delete'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 16) > 0 THEN ARRAY['Comment'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 32) > 0 THEN ARRAY['Execute'] ELSE ARRAY[]::text[] END
                    ) AS permission
                ) perm
                WHERE aa.content->'roles' IS NOT NULL
                ON CONFLICT (user_id, node_path_prefix, permission) DO UPDATE
                    SET is_allow = CASE WHEN EXCLUDED.is_allow = false THEN false ELSE user_effective_permissions_shadow.is_allow END;

                -- Direct entries from access_control table (convenience methods)
                INSERT INTO user_effective_permissions_shadow (user_id, node_path_prefix, permission, is_allow)
                SELECT subject, node_path, permission, is_allow
                FROM access_control
                ON CONFLICT (user_id, node_path_prefix, permission) DO UPDATE
                    SET is_allow = CASE WHEN EXCLUDED.is_allow = false THEN false ELSE user_effective_permissions_shadow.is_allow END;

                -- Group expansion from group_members + access_control
                INSERT INTO user_effective_permissions_shadow (user_id, node_path_prefix, permission, is_allow)
                WITH RECURSIVE all_members AS (
                    SELECT group_name, member_id FROM group_members
                    UNION
                    SELECT am.group_name, gm.member_id
                    FROM all_members am
                    JOIN group_members gm ON gm.group_name = am.member_id
                ),
                leaf_members AS (
                    SELECT group_name, member_id FROM all_members
                    WHERE member_id NOT IN (SELECT DISTINCT group_name FROM group_members)
                )
                SELECT lm.member_id, ac.node_path, ac.permission, ac.is_allow
                FROM access_control ac
                JOIN leaf_members lm ON lm.group_name = ac.subject
                ON CONFLICT (user_id, node_path_prefix, permission) DO UPDATE
                    SET is_allow = CASE WHEN EXCLUDED.is_allow = false THEN false ELSE user_effective_permissions_shadow.is_allow END;

                -- Apply PartitionAccessPolicy caps
                INSERT INTO user_effective_permissions_shadow (user_id, node_path_prefix, permission, is_allow)
                SELECT DISTINCT
                    uep.user_id,
                    policy.namespace AS node_path_prefix,
                    perm.permission,
                    false
                FROM mesh_nodes policy
                CROSS JOIN (SELECT DISTINCT user_id FROM user_effective_permissions_shadow) uep
                CROSS JOIN (
                    SELECT unnest(ARRAY['Read','Create','Update','Delete','Comment']) AS permission,
                           unnest(ARRAY['read','create','update','delete','comment']) AS field
                ) perm
                WHERE policy.node_type = 'PartitionAccessPolicy'
                  AND policy.id = '_Policy'
                  AND (policy.content->>perm.field)::boolean = false
                ON CONFLICT (user_id, node_path_prefix, permission) DO UPDATE SET is_allow = false;

                -- Atomic swap
                ALTER TABLE user_effective_permissions RENAME TO user_effective_permissions_old;
                ALTER TABLE user_effective_permissions_shadow RENAME TO user_effective_permissions;
                ALTER TABLE user_effective_permissions_old RENAME TO user_effective_permissions_shadow;

                -- Sync partition_access: upsert users with Read permission, remove revoked
                BEGIN
                    INSERT INTO public.partition_access (user_id, partition)
                    SELECT DISTINCT user_id, '{{schemaName}}'
                    FROM user_effective_permissions
                    WHERE permission = 'Read' AND is_allow = true
                    ON CONFLICT (user_id, partition) DO NOTHING;

                    DELETE FROM public.partition_access
                    WHERE partition = '{{schemaName}}'
                      AND user_id NOT IN (
                        SELECT user_id FROM user_effective_permissions
                        WHERE permission = 'Read' AND is_allow = true
                      );
                EXCEPTION WHEN undefined_table THEN
                    -- partition_access table may not exist yet (first migration)
                    NULL;
                END;
            END;
            $$ LANGUAGE plpgsql;

            -- Access satellite table (must exist before trigger creation)
            CREATE TABLE IF NOT EXISTS access (
                namespace       TEXT        NOT NULL DEFAULT '',
                id              TEXT        NOT NULL,
                path            TEXT        GENERATED ALWAYS AS (
                                    CASE WHEN namespace = '' THEN id ELSE namespace || '/' || id END
                                ) STORED,
                name            TEXT,
                node_type       TEXT,
                description     TEXT,
                category        TEXT,
                icon            TEXT,
                display_order   INTEGER,
                last_modified   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                version         BIGINT      NOT NULL DEFAULT 0,
                state           SMALLINT    NOT NULL DEFAULT 0,
                content         JSONB,
                desired_id      TEXT,
                main_node       TEXT,
                embedding       vector({{dim}}),
                PRIMARY KEY (namespace, id)
            );
            CREATE INDEX IF NOT EXISTS idx_access_path ON access (path);
            CREATE INDEX IF NOT EXISTS idx_access_main_node ON access (main_node);
            CREATE INDEX IF NOT EXISTS idx_access_node_type ON access (node_type);
            CREATE INDEX IF NOT EXISTS idx_access_last_modified ON access (last_modified DESC);

            -- Trigger function: fires on any change to the access satellite table
            CREATE OR REPLACE FUNCTION trg_access_changed() RETURNS TRIGGER AS $$
            BEGIN
                PERFORM rebuild_user_effective_permissions();
                RETURN NULL;
            END;
            $$ LANGUAGE plpgsql;

            -- Drop old trigger on mesh_nodes if it exists
            DROP TRIGGER IF EXISTS mesh_node_access_changed ON mesh_nodes;

            -- Trigger on access table for access changes
            DROP TRIGGER IF EXISTS access_changed ON access;
            CREATE TRIGGER access_changed
                AFTER INSERT OR UPDATE OR DELETE ON access
                FOR EACH STATEMENT EXECUTE FUNCTION trg_access_changed();

            -- Simple access_control and group_members tables used by convenience methods
            CREATE TABLE IF NOT EXISTS access_control (
                node_path   TEXT    NOT NULL,
                subject     TEXT    NOT NULL,
                permission  TEXT    NOT NULL,
                is_allow    BOOLEAN NOT NULL,
                PRIMARY KEY (node_path, subject, permission)
            );

            CREATE TABLE IF NOT EXISTS group_members (
                group_name  TEXT    NOT NULL,
                member_id   TEXT    NOT NULL,
                PRIMARY KEY (group_name, member_id)
            );

            -- Node type permission flags (populated from DI-registered NodeTypePermission records)
            CREATE TABLE IF NOT EXISTS node_type_permissions (
                node_type   TEXT    NOT NULL PRIMARY KEY,
                public_read BOOLEAN NOT NULL DEFAULT false
            );

            -- Drop legacy triggers if they exist (tables are now reused)
            DROP TRIGGER IF EXISTS access_control_changed ON access_control;
            DROP TRIGGER IF EXISTS group_members_changed ON group_members;
            DROP FUNCTION IF EXISTS trg_access_control_changed();

            -- Notify function for change notifications
            CREATE OR REPLACE FUNCTION notify_mesh_node_changes() RETURNS TRIGGER AS $$
            BEGIN
                IF TG_OP = 'DELETE' THEN
                    PERFORM pg_notify('mesh_node_changes',
                        json_build_object('path', CASE WHEN OLD.namespace = '' THEN OLD.id ELSE OLD.namespace || '/' || OLD.id END, 'op', 'DELETE')::text);
                    RETURN OLD;
                ELSE
                    PERFORM pg_notify('mesh_node_changes',
                        json_build_object('path', CASE WHEN NEW.namespace = '' THEN NEW.id ELSE NEW.namespace || '/' || NEW.id END, 'op', TG_OP)::text);
                    RETURN NEW;
                END IF;
            END;
            $$ LANGUAGE plpgsql;

            DO $$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'mesh_node_notify') THEN
                    CREATE TRIGGER mesh_node_notify
                        AFTER INSERT OR UPDATE OR DELETE ON mesh_nodes
                        FOR EACH ROW EXECUTE FUNCTION notify_mesh_node_changes();
                END IF;
            END;
            $$;

            -- Trigger to copy all rows to history in the versions schema
            CREATE OR REPLACE FUNCTION trg_mesh_node_to_history() RETURNS TRIGGER AS $$
            BEGIN
                BEGIN
                    EXECUTE format(
                        'INSERT INTO %I.mesh_node_history (
                            namespace, id, name, node_type, description, category, icon,
                            display_order, last_modified, version, state, content, desired_id, main_node
                        ) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14)',
                        '{{versionsSchema}}'
                    ) USING
                        NEW.namespace, NEW.id, NEW.name, NEW.node_type, NEW.description,
                        NEW.category, NEW.icon, NEW.display_order, NEW.last_modified,
                        NEW.version, NEW.state, NEW.content, NEW.desired_id, NEW.main_node;
                EXCEPTION WHEN unique_violation THEN
                    -- Already exists, skip
                END;
                RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;

            DO $$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'mesh_node_copy_to_history') THEN
                    CREATE TRIGGER mesh_node_copy_to_history
                        AFTER INSERT OR UPDATE ON mesh_nodes
                        FOR EACH ROW EXECUTE FUNCTION trg_mesh_node_to_history();
                END IF;
            END;
            $$;
            """;
    }

    private static string GetSchemaScript(PostgreSqlStorageOptions options)
    {
        var dim = options.VectorDimensions;
        var schemaName = options.Schema ?? "public";
        return $$"""
            CREATE EXTENSION IF NOT EXISTS vector;

            -- mesh_nodes
            CREATE TABLE IF NOT EXISTS mesh_nodes (
                namespace       TEXT        NOT NULL DEFAULT '',
                id              TEXT        NOT NULL,
                path            TEXT        GENERATED ALWAYS AS (
                                    CASE WHEN namespace = '' THEN id ELSE namespace || '/' || id END
                                ) STORED,
                name            TEXT,
                node_type       TEXT,
                description     TEXT,
                category        TEXT,
                icon            TEXT,
                display_order   INTEGER,
                last_modified   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                version         BIGINT      NOT NULL DEFAULT 0,
                state           SMALLINT    NOT NULL DEFAULT 0,
                content         JSONB,
                desired_id      TEXT,
                main_node       TEXT,
                embedding       vector({{dim}}),
                PRIMARY KEY (namespace, id)
            );

            CREATE INDEX IF NOT EXISTS idx_mn_path ON mesh_nodes (path);
            CREATE INDEX IF NOT EXISTS idx_mn_path_prefix ON mesh_nodes (path text_pattern_ops);
            CREATE INDEX IF NOT EXISTS idx_mn_namespace ON mesh_nodes (namespace);
            CREATE INDEX IF NOT EXISTS idx_mn_node_type ON mesh_nodes (node_type);
            CREATE INDEX IF NOT EXISTS idx_mn_content ON mesh_nodes USING gin (content jsonb_path_ops);
            CREATE INDEX IF NOT EXISTS idx_mn_text_search ON mesh_nodes USING gin (
                to_tsvector('english', COALESCE(name,'') || ' ' || COALESCE(description,'') || ' ' || COALESCE(node_type,''))
            );
            CREATE INDEX IF NOT EXISTS idx_mn_embedding ON mesh_nodes USING hnsw (embedding vector_cosine_ops);

            -- Migrate embedding column if dimensions changed
            DO $migrate$
            DECLARE cur_dim INT;
            BEGIN
                SELECT atttypmod INTO cur_dim FROM pg_attribute
                WHERE attrelid = 'mesh_nodes'::regclass AND attname = 'embedding' AND atttypmod > 0;
                IF cur_dim IS NOT NULL AND cur_dim != {{dim}} THEN
                    DROP INDEX IF EXISTS idx_mn_embedding;
                    ALTER TABLE mesh_nodes ALTER COLUMN embedding TYPE vector({{dim}}) USING NULL;
                    CREATE INDEX idx_mn_embedding ON mesh_nodes USING hnsw (embedding vector_cosine_ops);
                END IF;
            END $migrate$;

            -- partition_objects
            CREATE TABLE IF NOT EXISTS partition_objects (
                id              TEXT        NOT NULL,
                partition_key   TEXT        NOT NULL,
                type_name       TEXT,
                data            JSONB       NOT NULL,
                last_modified   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                PRIMARY KEY (partition_key, id)
            );

            -- user_activity
            CREATE TABLE IF NOT EXISTS user_activity (
                user_id         TEXT        NOT NULL,
                node_path       TEXT        NOT NULL,
                activity_type   SMALLINT    NOT NULL DEFAULT 0,
                first_accessed  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                last_accessed   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                access_count    INTEGER     NOT NULL DEFAULT 1,
                node_name       TEXT,
                node_type       TEXT,
                namespace       TEXT,
                PRIMARY KEY (user_id, node_path)
            );
            CREATE INDEX IF NOT EXISTS idx_ua_user_last ON user_activity (user_id, last_accessed DESC);
            CREATE INDEX IF NOT EXISTS idx_ua_node_type ON user_activity (user_id, node_type);

            -- change_logs (bundled activity logs)
            CREATE TABLE IF NOT EXISTS change_logs (
                id              TEXT        NOT NULL PRIMARY KEY,
                hub_path        TEXT        NOT NULL,
                changed_by      TEXT,
                category        TEXT        NOT NULL,
                start_time      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                end_time        TIMESTAMPTZ,
                change_count    INTEGER     NOT NULL DEFAULT 0,
                status          SMALLINT    NOT NULL DEFAULT 1,
                messages        JSONB
            );
            CREATE INDEX IF NOT EXISTS idx_cl_hub ON change_logs (hub_path, start_time DESC);
            CREATE INDEX IF NOT EXISTS idx_cl_user ON change_logs (changed_by, start_time DESC);

            -- user_effective_permissions (denormalized)
            CREATE TABLE IF NOT EXISTS user_effective_permissions (
                user_id          TEXT     NOT NULL,
                node_path_prefix TEXT     NOT NULL,
                permission       TEXT     NOT NULL,
                is_allow         BOOLEAN  NOT NULL,
                PRIMARY KEY (user_id, node_path_prefix, permission)
            );
            CREATE INDEX IF NOT EXISTS idx_uep_user_perm ON user_effective_permissions (user_id, permission);
            CREATE INDEX IF NOT EXISTS idx_uep_path ON user_effective_permissions (node_path_prefix text_pattern_ops);

            -- Shadow table for atomic rebuild
            CREATE TABLE IF NOT EXISTS user_effective_permissions_shadow (LIKE user_effective_permissions INCLUDING ALL);

            -- Rebuild function: reads AccessAssignment from access satellite table, GroupMembership from mesh_nodes
            -- AccessAssignment content: {"accessObject":"...","roles":[{"role":"...","denied":true},...]}
            CREATE OR REPLACE FUNCTION rebuild_user_effective_permissions() RETURNS void AS $$
            BEGIN
                TRUNCATE user_effective_permissions_shadow;

                -- Direct entries from AccessAssignment nodes (access satellite table)
                -- Unnest the roles[] array to get each role assignment
                INSERT INTO user_effective_permissions_shadow (user_id, node_path_prefix, permission, is_allow)
                SELECT
                    aa.content->>'accessObject' AS user_id,
                    COALESCE(aa.main_node, aa.namespace) AS node_path_prefix,
                    perm.permission,
                    NOT COALESCE((role_entry->>'denied')::boolean, false) AS is_allow
                FROM access aa
                CROSS JOIN LATERAL jsonb_array_elements(aa.content->'roles') AS role_entry
                CROSS JOIN LATERAL (
                    SELECT COALESCE(
                        (SELECT (role_node.content->>'permissions')::int
                         FROM mesh_nodes role_node
                         WHERE role_node.node_type = 'Role'
                           AND role_node.id = role_entry->>'role'
                         LIMIT 1),
                        -- Fallback: built-in role lookup
                        CASE role_entry->>'role'
                            WHEN 'Admin' THEN 63
                            WHEN 'PlatformAdmin' THEN 63
                            WHEN 'Editor' THEN 55
                            WHEN 'Viewer' THEN 33
                            WHEN 'Commenter' THEN 49
                            ELSE 0
                        END
                    ) AS permissions
                ) r
                CROSS JOIN LATERAL (
                    SELECT unnest(
                        CASE WHEN (r.permissions & 1) > 0 THEN ARRAY['Read'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 2) > 0 THEN ARRAY['Create'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 4) > 0 THEN ARRAY['Update'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 8) > 0 THEN ARRAY['Delete'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 16) > 0 THEN ARRAY['Comment'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 32) > 0 THEN ARRAY['Execute'] ELSE ARRAY[]::text[] END
                    ) AS permission
                ) perm
                WHERE aa.content->>'accessObject' IS NOT NULL
                  AND aa.content->'roles' IS NOT NULL
                ON CONFLICT (user_id, node_path_prefix, permission) DO UPDATE
                    SET is_allow = CASE WHEN EXCLUDED.is_allow = false THEN false ELSE user_effective_permissions_shadow.is_allow END;

                -- Group expansion: read GroupMembership MeshNodes
                -- New model: content has "member" + "groups" array of {"group":"..."}
                INSERT INTO user_effective_permissions_shadow (user_id, node_path_prefix, permission, is_allow)
                WITH RECURSIVE all_members AS (
                    SELECT group_entry->>'group' AS group_path, gm.content->>'member' AS member_id
                    FROM mesh_nodes gm
                    CROSS JOIN LATERAL jsonb_array_elements(gm.content->'groups') AS group_entry
                    WHERE gm.node_type = 'GroupMembership'
                    UNION
                    SELECT am.group_path, gm.content->>'member'
                    FROM all_members am
                    JOIN mesh_nodes gm ON gm.node_type = 'GroupMembership'
                    WHERE EXISTS (
                        SELECT 1 FROM jsonb_array_elements(gm.content->'groups') g
                        WHERE g->>'group' = am.member_id
                    )
                ),
                leaf_members AS (
                    SELECT group_path, member_id FROM all_members
                    WHERE NOT EXISTS (
                        SELECT 1 FROM mesh_nodes gm2
                        CROSS JOIN LATERAL jsonb_array_elements(gm2.content->'groups') AS g2
                        WHERE gm2.node_type = 'GroupMembership'
                          AND g2->>'group' = all_members.member_id
                    )
                )
                SELECT lm.member_id, COALESCE(aa.main_node, aa.namespace), perm.permission,
                       NOT COALESCE((role_entry->>'denied')::boolean, false) AS is_allow
                FROM access aa
                JOIN leaf_members lm ON aa.content->>'accessObject' = lm.group_path
                CROSS JOIN LATERAL jsonb_array_elements(aa.content->'roles') AS role_entry
                CROSS JOIN LATERAL (
                    SELECT COALESCE(
                        (SELECT (role_node.content->>'permissions')::int
                         FROM mesh_nodes role_node
                         WHERE role_node.node_type = 'Role'
                           AND role_node.id = role_entry->>'role'
                         LIMIT 1),
                        CASE role_entry->>'role'
                            WHEN 'Admin' THEN 63
                            WHEN 'PlatformAdmin' THEN 63
                            WHEN 'Editor' THEN 55
                            WHEN 'Viewer' THEN 33
                            WHEN 'Commenter' THEN 49
                            ELSE 0
                        END
                    ) AS permissions
                ) r
                CROSS JOIN LATERAL (
                    SELECT unnest(
                        CASE WHEN (r.permissions & 1) > 0 THEN ARRAY['Read'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 2) > 0 THEN ARRAY['Create'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 4) > 0 THEN ARRAY['Update'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 8) > 0 THEN ARRAY['Delete'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 16) > 0 THEN ARRAY['Comment'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 32) > 0 THEN ARRAY['Execute'] ELSE ARRAY[]::text[] END
                    ) AS permission
                ) perm
                WHERE aa.content->'roles' IS NOT NULL
                ON CONFLICT (user_id, node_path_prefix, permission) DO UPDATE
                    SET is_allow = CASE WHEN EXCLUDED.is_allow = false THEN false ELSE user_effective_permissions_shadow.is_allow END;

                -- Direct entries from access_control table (convenience methods)
                INSERT INTO user_effective_permissions_shadow (user_id, node_path_prefix, permission, is_allow)
                SELECT subject, node_path, permission, is_allow
                FROM access_control
                ON CONFLICT (user_id, node_path_prefix, permission) DO UPDATE
                    SET is_allow = CASE WHEN EXCLUDED.is_allow = false THEN false ELSE user_effective_permissions_shadow.is_allow END;

                -- Group expansion from group_members + access_control
                INSERT INTO user_effective_permissions_shadow (user_id, node_path_prefix, permission, is_allow)
                WITH RECURSIVE all_members AS (
                    SELECT group_name, member_id FROM group_members
                    UNION
                    SELECT am.group_name, gm.member_id
                    FROM all_members am
                    JOIN group_members gm ON gm.group_name = am.member_id
                ),
                leaf_members AS (
                    SELECT group_name, member_id FROM all_members
                    WHERE member_id NOT IN (SELECT DISTINCT group_name FROM group_members)
                )
                SELECT lm.member_id, ac.node_path, ac.permission, ac.is_allow
                FROM access_control ac
                JOIN leaf_members lm ON lm.group_name = ac.subject
                ON CONFLICT (user_id, node_path_prefix, permission) DO UPDATE
                    SET is_allow = CASE WHEN EXCLUDED.is_allow = false THEN false ELSE user_effective_permissions_shadow.is_allow END;

                -- Apply PartitionAccessPolicy caps: deny permissions set to false
                -- For each policy, insert deny rows at the policy namespace for ALL users
                -- for permissions that the policy explicitly denies (field value = false).
                -- The most-specific-prefix-wins query logic then correctly denies those permissions.
                INSERT INTO user_effective_permissions_shadow (user_id, node_path_prefix, permission, is_allow)
                SELECT DISTINCT
                    uep.user_id,
                    policy.namespace AS node_path_prefix,
                    perm.permission,
                    false
                FROM mesh_nodes policy
                CROSS JOIN (SELECT DISTINCT user_id FROM user_effective_permissions_shadow) uep
                CROSS JOIN (
                    SELECT unnest(ARRAY['Read','Create','Update','Delete','Comment']) AS permission,
                           unnest(ARRAY['read','create','update','delete','comment']) AS field
                ) perm
                WHERE policy.node_type = 'PartitionAccessPolicy'
                  AND policy.id = '_Policy'
                  AND (policy.content->>perm.field)::boolean = false
                ON CONFLICT (user_id, node_path_prefix, permission) DO UPDATE SET is_allow = false;

                -- Atomic swap
                ALTER TABLE user_effective_permissions RENAME TO user_effective_permissions_old;
                ALTER TABLE user_effective_permissions_shadow RENAME TO user_effective_permissions;
                ALTER TABLE user_effective_permissions_old RENAME TO user_effective_permissions_shadow;

                -- Sync partition_access: upsert users with Read permission, remove revoked
                BEGIN
                    INSERT INTO public.partition_access (user_id, partition)
                    SELECT DISTINCT user_id, '{{schemaName}}'
                    FROM user_effective_permissions
                    WHERE permission = 'Read' AND is_allow = true
                    ON CONFLICT (user_id, partition) DO NOTHING;

                    DELETE FROM public.partition_access
                    WHERE partition = '{{schemaName}}'
                      AND user_id NOT IN (
                        SELECT user_id FROM user_effective_permissions
                        WHERE permission = 'Read' AND is_allow = true
                      );
                EXCEPTION WHEN undefined_table THEN
                    -- partition_access table may not exist yet (first migration)
                    NULL;
                END;
            END;
            $$ LANGUAGE plpgsql;

            -- Access satellite table (must exist before trigger creation)
            CREATE TABLE IF NOT EXISTS access (
                namespace       TEXT        NOT NULL DEFAULT '',
                id              TEXT        NOT NULL,
                path            TEXT        GENERATED ALWAYS AS (
                                    CASE WHEN namespace = '' THEN id ELSE namespace || '/' || id END
                                ) STORED,
                name            TEXT,
                node_type       TEXT,
                description     TEXT,
                category        TEXT,
                icon            TEXT,
                display_order   INTEGER,
                last_modified   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                version         BIGINT      NOT NULL DEFAULT 0,
                state           SMALLINT    NOT NULL DEFAULT 0,
                content         JSONB,
                desired_id      TEXT,
                main_node       TEXT,
                embedding       vector({{dim}}),
                PRIMARY KEY (namespace, id)
            );
            CREATE INDEX IF NOT EXISTS idx_access_path ON access (path);
            CREATE INDEX IF NOT EXISTS idx_access_main_node ON access (main_node);
            CREATE INDEX IF NOT EXISTS idx_access_node_type ON access (node_type);
            CREATE INDEX IF NOT EXISTS idx_access_last_modified ON access (last_modified DESC);

            -- Trigger function: fires on any change to the access satellite table
            CREATE OR REPLACE FUNCTION trg_access_changed() RETURNS TRIGGER AS $$
            BEGIN
                PERFORM rebuild_user_effective_permissions();
                RETURN NULL;
            END;
            $$ LANGUAGE plpgsql;

            -- Drop old trigger on mesh_nodes if it exists
            DROP TRIGGER IF EXISTS mesh_node_access_changed ON mesh_nodes;

            -- Trigger on access table for access changes
            DROP TRIGGER IF EXISTS access_changed ON access;
            CREATE TRIGGER access_changed
                AFTER INSERT OR UPDATE OR DELETE ON access
                FOR EACH STATEMENT EXECUTE FUNCTION trg_access_changed();

            -- Simple access_control and group_members tables used by convenience methods
            CREATE TABLE IF NOT EXISTS access_control (
                node_path   TEXT    NOT NULL,
                subject     TEXT    NOT NULL,
                permission  TEXT    NOT NULL,
                is_allow    BOOLEAN NOT NULL,
                PRIMARY KEY (node_path, subject, permission)
            );

            CREATE TABLE IF NOT EXISTS group_members (
                group_name  TEXT    NOT NULL,
                member_id   TEXT    NOT NULL,
                PRIMARY KEY (group_name, member_id)
            );

            -- Node type permission flags (populated from DI-registered NodeTypePermission records)
            CREATE TABLE IF NOT EXISTS node_type_permissions (
                node_type   TEXT    NOT NULL PRIMARY KEY,
                public_read BOOLEAN NOT NULL DEFAULT false
            );

            -- Drop legacy triggers if they exist (tables are now reused)
            DROP TRIGGER IF EXISTS access_control_changed ON access_control;
            DROP TRIGGER IF EXISTS group_members_changed ON group_members;
            DROP FUNCTION IF EXISTS trg_access_control_changed();

            -- Notify function for change notifications
            CREATE OR REPLACE FUNCTION notify_mesh_node_changes() RETURNS TRIGGER AS $$
            BEGIN
                IF TG_OP = 'DELETE' THEN
                    PERFORM pg_notify('mesh_node_changes',
                        json_build_object('path', CASE WHEN OLD.namespace = '' THEN OLD.id ELSE OLD.namespace || '/' || OLD.id END, 'op', 'DELETE')::text);
                    RETURN OLD;
                ELSE
                    PERFORM pg_notify('mesh_node_changes',
                        json_build_object('path', CASE WHEN NEW.namespace = '' THEN NEW.id ELSE NEW.namespace || '/' || NEW.id END, 'op', TG_OP)::text);
                    RETURN NEW;
                END IF;
            END;
            $$ LANGUAGE plpgsql;

            DO $$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'mesh_node_notify') THEN
                    CREATE TRIGGER mesh_node_notify
                        AFTER INSERT OR UPDATE OR DELETE ON mesh_nodes
                        FOR EACH ROW EXECUTE FUNCTION notify_mesh_node_changes();
                END IF;
            END;
            $$;

            -- mesh_node_history: versioned copies of mesh_nodes
            CREATE TABLE IF NOT EXISTS mesh_node_history (
                namespace       TEXT        NOT NULL DEFAULT '',
                id              TEXT        NOT NULL,
                path            TEXT        GENERATED ALWAYS AS (
                                    CASE WHEN namespace = '' THEN id ELSE namespace || '/' || id END
                                ) STORED,
                name            TEXT,
                node_type       TEXT,
                description     TEXT,
                category        TEXT,
                icon            TEXT,
                display_order   INTEGER,
                last_modified   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                version         BIGINT      NOT NULL DEFAULT 0,
                state           SMALLINT    NOT NULL DEFAULT 0,
                content         JSONB,
                desired_id      TEXT,
                changed_by      TEXT,
                main_node       TEXT,
                PRIMARY KEY (namespace, id, version)
            );

            CREATE INDEX IF NOT EXISTS idx_mnh_path ON mesh_node_history (path);
            CREATE INDEX IF NOT EXISTS idx_mnh_path_version ON mesh_node_history (path, version DESC);

            -- Trigger to copy all rows to history on every insert/update
            CREATE OR REPLACE FUNCTION trg_mesh_node_to_history() RETURNS TRIGGER AS $$
            BEGIN
                INSERT INTO mesh_node_history (
                    namespace, id, name, node_type, description, category, icon,
                    display_order, last_modified, version, state, content, desired_id, main_node
                )
                VALUES (
                    NEW.namespace, NEW.id, NEW.name, NEW.node_type, NEW.description,
                    NEW.category, NEW.icon, NEW.display_order, NEW.last_modified,
                    NEW.version, NEW.state, NEW.content, NEW.desired_id, NEW.main_node
                )
                ON CONFLICT (namespace, id, version) DO NOTHING;
                RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;

            DO $$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'mesh_node_copy_to_history') THEN
                    CREATE TRIGGER mesh_node_copy_to_history
                        AFTER INSERT OR UPDATE ON mesh_nodes
                        FOR EACH ROW EXECUTE FUNCTION trg_mesh_node_to_history();
                END IF;
            END;
            $$;
            """;
    }

    /// <summary>
    /// Returns SQL for unversioned partitions: core tables only, no history table or triggers.
    /// </summary>
    private static string GetUnversionedSchemaScript(PostgreSqlStorageOptions options)
    {
        var dim = options.VectorDimensions;
        var schemaName = options.Schema ?? "public";
        return $$"""
            CREATE EXTENSION IF NOT EXISTS vector;

            CREATE TABLE IF NOT EXISTS mesh_nodes (
                namespace       TEXT        NOT NULL DEFAULT '',
                id              TEXT        NOT NULL,
                path            TEXT        GENERATED ALWAYS AS (
                                    CASE WHEN namespace = '' THEN id ELSE namespace || '/' || id END
                                ) STORED,
                name            TEXT,
                node_type       TEXT,
                description     TEXT,
                category        TEXT,
                icon            TEXT,
                display_order   INTEGER,
                last_modified   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                version         BIGINT      NOT NULL DEFAULT 0,
                state           SMALLINT    NOT NULL DEFAULT 0,
                content         JSONB,
                desired_id      TEXT,
                main_node       TEXT,
                embedding       vector({{dim}}),
                PRIMARY KEY (namespace, id)
            );

            CREATE INDEX IF NOT EXISTS idx_mn_path ON mesh_nodes (path);
            CREATE INDEX IF NOT EXISTS idx_mn_path_prefix ON mesh_nodes (path text_pattern_ops);
            CREATE INDEX IF NOT EXISTS idx_mn_namespace ON mesh_nodes (namespace);
            CREATE INDEX IF NOT EXISTS idx_mn_node_type ON mesh_nodes (node_type);
            CREATE INDEX IF NOT EXISTS idx_mn_content ON mesh_nodes USING gin (content jsonb_path_ops);

            -- Notify function for change notifications
            CREATE OR REPLACE FUNCTION notify_mesh_node_changes() RETURNS TRIGGER AS $$
            BEGIN
                IF TG_OP = 'DELETE' THEN
                    PERFORM pg_notify('mesh_node_changes',
                        json_build_object('path', CASE WHEN OLD.namespace = '' THEN OLD.id ELSE OLD.namespace || '/' || OLD.id END, 'op', 'DELETE')::text);
                    RETURN OLD;
                ELSE
                    PERFORM pg_notify('mesh_node_changes',
                        json_build_object('path', CASE WHEN NEW.namespace = '' THEN NEW.id ELSE NEW.namespace || '/' || NEW.id END, 'op', TG_OP)::text);
                    RETURN NEW;
                END IF;
            END;
            $$ LANGUAGE plpgsql;

            DO $$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'mesh_node_notify') THEN
                    CREATE TRIGGER mesh_node_notify
                        AFTER INSERT OR UPDATE OR DELETE ON mesh_nodes
                        FOR EACH ROW EXECUTE FUNCTION notify_mesh_node_changes();
                END IF;
            END;
            $$;
            """;
    }
}
