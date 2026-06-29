namespace Memex.Database.Migration.Migrations;

/// <summary>
/// Move <c>AccessAssignment</c> nodes from <c>mesh_nodes</c> to <c>access</c>, fix the
/// missing <c>/_Access</c> namespace segment, then rebuild permissions.
///
/// Bug: <c>AddUserRoleAsync</c> wrote AccessAssignment nodes to <c>mesh_nodes</c> with
/// <c>namespace={scope}/{userId}_Access</c> (missing slash + _Access segment), so the
/// trigger never fired and the user got no effective permissions.
/// </summary>
public sealed class V01_MoveAccessAssignments : IMigration
{
    public int Version => 1;
    public string Description => "Move AccessAssignments to access table with _Access namespace";

    public async Task RunAsync(MigrationContext ctx)
    {
        await using var cmd = ctx.DataSource.CreateCommand("""
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
            """);
        await cmd.ExecuteNonQueryAsync();
    }
}
