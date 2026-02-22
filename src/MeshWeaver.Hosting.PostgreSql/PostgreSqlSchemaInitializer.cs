using Npgsql;

namespace MeshWeaver.Hosting.PostgreSql;

/// <summary>
/// Creates the PostgreSQL schema (tables, indexes, triggers) if not already present.
/// </summary>
public static class PostgreSqlSchemaInitializer
{
    public static async Task InitializeAsync(NpgsqlDataSource dataSource, PostgreSqlStorageOptions options, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(GetSchemaScript(options));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string GetSchemaScript(PostgreSqlStorageOptions options)
    {
        var dim = options.VectorDimensions;
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

            -- Rebuild function: reads AccessAssignment and GroupMembership MeshNodes from mesh_nodes
            -- AccessAssignment content: {"subjectId":"...","roles":[{"roleId":"...","denied":false},...]}
            CREATE OR REPLACE FUNCTION rebuild_user_effective_permissions() RETURNS void AS $$
            BEGIN
                TRUNCATE user_effective_permissions_shadow;

                -- Direct entries from AccessAssignment MeshNodes
                -- Unnest the roles[] array to get each role assignment
                INSERT INTO user_effective_permissions_shadow (user_id, node_path_prefix, permission, is_allow)
                SELECT
                    aa.content->>'subjectId' AS user_id,
                    aa.namespace AS node_path_prefix,
                    perm.permission,
                    NOT COALESCE((role_entry->>'denied')::boolean, false) AS is_allow
                FROM mesh_nodes aa
                CROSS JOIN LATERAL jsonb_array_elements(aa.content->'roles') AS role_entry
                CROSS JOIN LATERAL (
                    SELECT COALESCE(
                        (SELECT (role_node.content->>'permissions')::int
                         FROM mesh_nodes role_node
                         WHERE role_node.node_type = 'Role'
                           AND role_node.id = role_entry->>'roleId'
                         LIMIT 1),
                        -- Fallback: built-in role lookup
                        CASE role_entry->>'roleId'
                            WHEN 'Admin' THEN 31
                            WHEN 'Editor' THEN 23
                            WHEN 'Viewer' THEN 1
                            WHEN 'Commenter' THEN 17
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
                    ) AS permission
                ) perm
                WHERE aa.node_type = 'AccessAssignment'
                  AND aa.content->>'subjectId' IS NOT NULL
                  AND aa.content->'roles' IS NOT NULL
                ON CONFLICT (user_id, node_path_prefix, permission) DO UPDATE
                    SET is_allow = CASE WHEN EXCLUDED.is_allow = false THEN false ELSE user_effective_permissions_shadow.is_allow END;

                -- Group expansion: read GroupMembership MeshNodes
                INSERT INTO user_effective_permissions_shadow (user_id, node_path_prefix, permission, is_allow)
                WITH RECURSIVE all_members AS (
                    SELECT gm.namespace AS group_path, gm.content->>'memberId' AS member_id
                    FROM mesh_nodes gm WHERE gm.node_type = 'GroupMembership'
                    UNION
                    SELECT am.group_path, gm.content->>'memberId'
                    FROM all_members am
                    JOIN mesh_nodes gm ON gm.node_type = 'GroupMembership'
                        AND gm.namespace = am.member_id
                ),
                leaf_members AS (
                    SELECT group_path, member_id FROM all_members
                    WHERE NOT EXISTS (
                        SELECT 1 FROM mesh_nodes gm
                        WHERE gm.node_type = 'GroupMembership' AND gm.namespace = all_members.member_id
                    )
                )
                SELECT lm.member_id, aa.namespace, perm.permission,
                       NOT COALESCE((role_entry->>'denied')::boolean, false) AS is_allow
                FROM mesh_nodes aa
                JOIN leaf_members lm ON aa.content->>'subjectId' = lm.group_path
                CROSS JOIN LATERAL jsonb_array_elements(aa.content->'roles') AS role_entry
                CROSS JOIN LATERAL (
                    SELECT COALESCE(
                        (SELECT (role_node.content->>'permissions')::int
                         FROM mesh_nodes role_node
                         WHERE role_node.node_type = 'Role'
                           AND role_node.id = role_entry->>'roleId'
                         LIMIT 1),
                        CASE role_entry->>'roleId'
                            WHEN 'Admin' THEN 31
                            WHEN 'Editor' THEN 23
                            WHEN 'Viewer' THEN 1
                            WHEN 'Commenter' THEN 17
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
                    ) AS permission
                ) perm
                WHERE aa.node_type = 'AccessAssignment'
                  AND aa.content->'roles' IS NOT NULL
                ON CONFLICT (user_id, node_path_prefix, permission) DO UPDATE
                    SET is_allow = CASE WHEN EXCLUDED.is_allow = false THEN false ELSE user_effective_permissions_shadow.is_allow END;

                -- Atomic swap
                ALTER TABLE user_effective_permissions RENAME TO user_effective_permissions_old;
                ALTER TABLE user_effective_permissions_shadow RENAME TO user_effective_permissions;
                ALTER TABLE user_effective_permissions_old RENAME TO user_effective_permissions_shadow;
            END;
            $$ LANGUAGE plpgsql;

            -- Trigger function: fires when AccessAssignment or GroupMembership nodes change
            CREATE OR REPLACE FUNCTION trg_mesh_node_access_changed() RETURNS TRIGGER AS $$
            BEGIN
                IF (TG_OP = 'DELETE' AND OLD.node_type IN ('AccessAssignment', 'GroupMembership'))
                   OR (TG_OP IN ('INSERT', 'UPDATE') AND NEW.node_type IN ('AccessAssignment', 'GroupMembership'))
                   OR (TG_OP = 'UPDATE' AND OLD.node_type IN ('AccessAssignment', 'GroupMembership'))
                THEN
                    PERFORM rebuild_user_effective_permissions();
                END IF;
                RETURN NULL;
            END;
            $$ LANGUAGE plpgsql;

            -- Trigger on mesh_nodes for access changes
            DO $$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'mesh_node_access_changed') THEN
                    CREATE TRIGGER mesh_node_access_changed
                        AFTER INSERT OR UPDATE OR DELETE ON mesh_nodes
                        FOR EACH ROW EXECUTE FUNCTION trg_mesh_node_access_changed();
                END IF;
            END;
            $$;

            -- Drop legacy triggers and tables if they exist
            DROP TRIGGER IF EXISTS access_control_changed ON access_control;
            DROP TRIGGER IF EXISTS group_members_changed ON group_members;
            DROP FUNCTION IF EXISTS trg_access_control_changed();
            DROP TABLE IF EXISTS access_control;
            DROP TABLE IF EXISTS group_members;

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
