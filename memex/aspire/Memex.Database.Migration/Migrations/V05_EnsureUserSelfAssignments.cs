namespace Memex.Database.Migration.Migrations;

/// <summary>
/// Ensure every user has an Admin self-assignment, then rebuild permissions across
/// all content partitions.
///
/// Fixes missing AccessAssignment nodes for users onboarded before
/// <c>UserScopeGrantHandler</c> existed, and propagates the new <c>Thread</c> permission
/// (added to Admin/Editor roles) into <c>user_effective_permissions</c>.
/// </summary>
public sealed class V05_EnsureUserSelfAssignments : IMigration
{
    public int Version => 5;
    public string Description => "Ensure user self-assignments and rebuild permissions";

    public async Task RunAsync(MigrationContext ctx)
    {
        await using var cmd = ctx.DataSource.CreateCommand("""
            DO $$
            DECLARE
                user_rec RECORD;
                assignment_exists BOOLEAN;
            BEGIN
                -- For each User node, ensure they have an Admin AccessAssignment on their own scope
                FOR user_rec IN
                    SELECT id, path FROM "user".mesh_nodes WHERE node_type = 'User'
                LOOP
                    -- Check if self-assignment already exists
                    SELECT EXISTS(
                        SELECT 1 FROM "user".access
                        WHERE namespace = 'User/' || user_rec.id || '/_Access'
                          AND content->>'accessObject' = user_rec.id
                    ) INTO assignment_exists;

                    IF NOT assignment_exists THEN
                        INSERT INTO "user".access (id, namespace, name, node_type, content, main_node, last_modified, version, state)
                        VALUES (
                            user_rec.id || '_SelfAccess',
                            'User/' || user_rec.id || '/_Access',
                            user_rec.id || ' Self Access',
                            'AccessAssignment',
                            jsonb_build_object(
                                'accessObject', user_rec.id,
                                'displayName', user_rec.id,
                                'roles', jsonb_build_array(jsonb_build_object('role', 'Admin'))
                            ),
                            'User/' || user_rec.id,
                            NOW(),
                            1,
                            'Active'
                        );
                        RAISE NOTICE 'Created self-assignment for user %', user_rec.id;
                    END IF;
                END LOOP;

                -- Rebuild permissions for user schema
                BEGIN
                    PERFORM "user".rebuild_user_effective_permissions();
                EXCEPTION WHEN OTHERS THEN
                    RAISE NOTICE 'user schema rebuild failed: %', SQLERRM;
                END;

                -- Rebuild permissions for all content partitions
                FOR user_rec IN
                    SELECT schema_name FROM information_schema.schemata s
                    WHERE EXISTS (SELECT 1 FROM information_schema.tables t WHERE t.table_schema = s.schema_name AND t.table_name = 'access')
                    AND s.schema_name NOT IN ('public', 'information_schema', 'pg_catalog', 'pg_toast', 'user')
                    AND s.schema_name NOT LIKE '%\_versions' ESCAPE '\'
                LOOP
                    BEGIN
                        EXECUTE format('SELECT %I.rebuild_user_effective_permissions()', user_rec.schema_name);
                    EXCEPTION WHEN OTHERS THEN
                        RAISE NOTICE 'Schema % rebuild failed: %', user_rec.schema_name, SQLERRM;
                    END;
                END LOOP;
            END $$;
            """);
        await cmd.ExecuteNonQueryAsync();
    }
}
