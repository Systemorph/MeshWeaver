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
        return $"""
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
                embedding       vector({dim}),
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

            -- access_control
            CREATE TABLE IF NOT EXISTS access_control (
                namespace       TEXT        NOT NULL,
                access_object   TEXT        NOT NULL,
                permission      TEXT        NOT NULL,
                is_allow        BOOLEAN     NOT NULL DEFAULT TRUE,
                assigned_at     TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                assigned_by     TEXT,
                PRIMARY KEY (namespace, access_object, permission)
            );

            -- group_members (access_object_id can be a user or another group)
            CREATE TABLE IF NOT EXISTS group_members (
                group_id          TEXT NOT NULL,
                access_object_id  TEXT NOT NULL,
                added_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                PRIMARY KEY (group_id, access_object_id)
            );
            CREATE INDEX IF NOT EXISTS idx_gm_access_object ON group_members (access_object_id);

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

            -- Rebuild function (supports nested groups via recursive CTE)
            CREATE OR REPLACE FUNCTION rebuild_user_effective_permissions() RETURNS void AS $$
            BEGIN
                TRUNCATE user_effective_permissions_shadow;

                -- Direct entries (access_object is not used as a group_id, i.e. it's a leaf user)
                INSERT INTO user_effective_permissions_shadow (user_id, node_path_prefix, permission, is_allow)
                SELECT ac.access_object, ac.namespace, ac.permission, ac.is_allow
                FROM access_control ac
                WHERE NOT EXISTS (SELECT 1 FROM group_members gm WHERE gm.group_id = ac.access_object)
                ON CONFLICT (user_id, node_path_prefix, permission) DO UPDATE
                    SET is_allow = CASE WHEN EXCLUDED.is_allow = false THEN false ELSE user_effective_permissions_shadow.is_allow END;

                -- Recursive group expansion (handles groups within groups)
                INSERT INTO user_effective_permissions_shadow (user_id, node_path_prefix, permission, is_allow)
                WITH RECURSIVE all_members AS (
                    -- Base: direct members
                    SELECT group_id, access_object_id AS member_id
                    FROM group_members
                    UNION
                    -- Recursive: if a member is itself a group, expand its members
                    SELECT am.group_id, gm.access_object_id AS member_id
                    FROM all_members am
                    JOIN group_members gm ON gm.group_id = am.member_id
                ),
                leaf_members AS (
                    SELECT group_id, member_id
                    FROM all_members
                    WHERE NOT EXISTS (SELECT 1 FROM group_members gm WHERE gm.group_id = all_members.member_id)
                )
                SELECT lm.member_id, ac.namespace, ac.permission, ac.is_allow
                FROM access_control ac
                JOIN leaf_members lm ON ac.access_object = lm.group_id
                ON CONFLICT (user_id, node_path_prefix, permission) DO UPDATE
                    SET is_allow = CASE WHEN EXCLUDED.is_allow = false THEN false ELSE user_effective_permissions_shadow.is_allow END;

                -- Atomic swap
                ALTER TABLE user_effective_permissions RENAME TO user_effective_permissions_old;
                ALTER TABLE user_effective_permissions_shadow RENAME TO user_effective_permissions;
                ALTER TABLE user_effective_permissions_old RENAME TO user_effective_permissions_shadow;
            END;
            $$ LANGUAGE plpgsql;

            -- Trigger function
            CREATE OR REPLACE FUNCTION trg_access_control_changed() RETURNS TRIGGER AS $$
            BEGIN
                PERFORM rebuild_user_effective_permissions();
                RETURN NULL;
            END;
            $$ LANGUAGE plpgsql;

            -- Triggers (use DO block to avoid errors if they already exist)
            DO $$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'access_control_changed') THEN
                    CREATE TRIGGER access_control_changed
                        AFTER INSERT OR UPDATE OR DELETE ON access_control
                        FOR EACH STATEMENT EXECUTE FUNCTION trg_access_control_changed();
                END IF;
                IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'group_members_changed') THEN
                    CREATE TRIGGER group_members_changed
                        AFTER INSERT OR UPDATE OR DELETE ON group_members
                        FOR EACH STATEMENT EXECUTE FUNCTION trg_access_control_changed();
                END IF;
            END;
            $$;

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
