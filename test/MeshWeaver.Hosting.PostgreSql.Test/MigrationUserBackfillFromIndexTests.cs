using System;
using System.Reactive.Linq;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Guards the V05 migration's user self-assignment backfill against the legacy <c>user</c>
/// schema: Users are sourced from the central index (<c>public.top_level_index</c> — the #16
/// partition-root materialized view, since Users are partition roots) and the self-Admin grant
/// is written into the USER'S OWN partition's <c>access</c> table at <c>{id}/_Access</c>. There
/// is NO <c>user</c> schema — on a fresh DB it never exists, and referencing it (the original
/// V05) aborted the whole migration with <c>42P01: relation "user".mesh_nodes does not exist</c>.
///
/// <para>This pins the exact SQL the migration runs (kept in sync with
/// <c>V05_EnsureUserSelfAssignments</c>).</para>
/// </summary>
[Collection("PostgreSql")]
public class MigrationUserBackfillFromIndexTests
{
    private readonly PostgreSqlFixture _fixture;

    public MigrationUserBackfillFromIndexTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    // The matview-sourced backfill loop from V05_EnsureUserSelfAssignments (must match it).
    private const string V05BackfillSql = """
        DO $$
        DECLARE user_rec RECORD;
        BEGIN
            FOR user_rec IN
                SELECT id FROM public.top_level_index WHERE node_type = 'User'
            LOOP
                BEGIN
                    EXECUTE format($ins$
                        INSERT INTO %1$I.access
                            (id, namespace, name, node_type, content, main_node, last_modified, version, state)
                        SELECT %2$L || '_Access', %2$L || '/_Access', %2$L || ' Access', 'AccessAssignment',
                               jsonb_build_object('accessObject', %2$L, 'displayName', %2$L,
                                   'roles', jsonb_build_array(jsonb_build_object('role', 'Admin'))),
                               %2$L, NOW(), 1, 2
                        WHERE NOT EXISTS (
                            SELECT 1 FROM %1$I.access
                            WHERE namespace = %2$L || '/_Access'
                              AND content->>'accessObject' = %2$L)
                    $ins$, lower(user_rec.id), user_rec.id);
                EXCEPTION WHEN OTHERS THEN
                    RAISE NOTICE 'Self-assignment backfill for user % failed: %', user_rec.id, SQLERRM;
                END;
            END LOOP;
        END $$;
        """;

    [Fact(Timeout = 60000)]
    public void V05Backfill_SourcesUsersFromMatview_WritesSelfGrantIntoUserPartition_NoUserSchema()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = $"miguser{Guid.NewGuid():N}".ToLowerInvariant()[..16];

        // 1. Provision the user's partition (schema + mesh_nodes + access + satellites).
        _fixture.DataSource.ExecuteNonQuery($"SELECT public.ensure_partition_schema('{user}')", ct)
            .Should().Within(30.Seconds()).Emit();

        // 2. Seed the User partition-root row (namespace='', id=user, node_type='User').
        _fixture.DataSource.ExecuteNonQuery($"""
            INSERT INTO "{user}".mesh_nodes
                (id, namespace, name, node_type, content, main_node, last_modified, version, state)
            VALUES ('{user}', '', '{user}', 'User', jsonb_build_object(), '{user}', NOW(), 1, 2)
            """, ct).Should().Within(30.Seconds()).Emit();

        // 3. Materialize the central index so the User appears as a partition root.
        _fixture.DataSource.ExecuteNonQuery($"""
            INSERT INTO public.searchable_schemas (schema_name) VALUES ('{user}') ON CONFLICT DO NOTHING;
            SELECT public.rebuild_top_level_index();
            """, ct).Should().Within(30.Seconds()).Emit();
        // The User partition root must be in the matview.
        _fixture.DataSource.ScalarLong(
            "SELECT count(*) FROM public.top_level_index WHERE node_type = 'User' AND id = @u",
            new[] { ("u", (object)user) }, ct)
            .Should().Within(30.Seconds()).Be(1L);

        // 4. Run the V05 matview-sourced backfill.
        _fixture.DataSource.ExecuteNonQuery(V05BackfillSql, ct).Should().Within(30.Seconds()).Emit();

        // 5. The self-Admin grant landed in the USER'S OWN partition `access` table.
        _fixture.DataSource.ScalarLong($"""
            SELECT count(*) FROM "{user}".access
            WHERE namespace = '{user}/_Access' AND content->>'accessObject' = '{user}'
            """, ct).Should().Within(30.Seconds()).Be(1L);

        // 6. Idempotent — a second run inserts nothing (WHERE NOT EXISTS).
        _fixture.DataSource.ExecuteNonQuery(V05BackfillSql, ct).Should().Within(30.Seconds()).Emit();
        _fixture.DataSource.ScalarLong($"""
            SELECT count(*) FROM "{user}".access WHERE namespace = '{user}/_Access'
            """, ct).Should().Within(30.Seconds()).Be(1L);

        // 7. No legacy `user` schema was created or required.
        _fixture.DataSource.ScalarLong(
            "SELECT count(*) FROM information_schema.schemata WHERE schema_name = 'user'", ct)
            .Should().Within(30.Seconds()).Be(0L);
    }
}
