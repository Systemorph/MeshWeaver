namespace Memex.Database.Migration.Migrations;

/// <summary>
/// Upgrade user self-assignments from Viewer to Admin.
/// <c>UserScopeGrantHandler</c> previously granted Viewer on <c>User/{userId}</c>; it now
/// grants Admin so users can fully manage their own namespace.
/// </summary>
public sealed class V04_UpgradeViewerToAdmin : IMigration
{
    public int Version => 4;
    public string Description => "Upgrade user self-assignments from Viewer to Admin";

    public async Task RunAsync(MigrationContext ctx)
    {
        await using var cmd = ctx.DataSource.CreateCommand("""
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
            """);
        await cmd.ExecuteNonQueryAsync();
    }
}
