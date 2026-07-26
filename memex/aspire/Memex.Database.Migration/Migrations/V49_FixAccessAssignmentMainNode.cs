namespace Memex.Database.Migration.Migrations;

/// <summary>
/// Repairs <c>AccessAssignment.main_node</c> values that point at the <c>_Access</c> satellite
/// CONTAINER instead of the node the grant was made on, then rebuilds the permissions derived from
/// them.
///
/// <para><b>The bug.</b> A grant written without an explicit <c>MainNode</c> (the invite flows, MCP
/// <c>create</c>, any plain <c>CreateNodeRequest</c>) had it auto-stamped to the node's NAMESPACE —
/// <c>{scope}/_Access</c> — by the satellite rule in <c>MeshExtensions</c>. That is a path no node
/// lives at, and <c>main_node</c> is read as a real node in two places that matter:</para>
/// <list type="number">
///   <item><b>Permissions</b>: <c>rebuild_user_effective_permissions()</c> projects each grant at
///     prefix <c>COALESCE(aa.main_node, aa.namespace)</c>, so the grant landed on
///     <c>{scope}/_Access</c> — one level BELOW the node it was granted on. The invitee got rows
///     that cover only the access list, not <c>{scope}</c> itself.</item>
///   <item><b>The invitation</b>: the access-granted mail named and linked <c>main_node</c> —
///     "You've been given access to CollaborationNotus/_Access".</item>
/// </list>
///
/// <para>The stamp is fixed at the source (<c>SatelliteTableMapping.OwnerOfSatellitePath</c>), so
/// new grants are written correctly; this migration repairs the rows already stored. Idempotent:
/// it only touches rows whose <c>main_node</c> still contains an <c>_Access</c> SEGMENT (an
/// <c>_AccessLog</c>-style path is left alone), and re-running it matches nothing.</para>
///
/// <para>Scope is deliberately the <c>access</c> table only — it is the one whose <c>main_node</c>
/// feeds the permission projection. Other satellites (<c>_Thread</c>, <c>_Activity</c>, …) set
/// MainNode explicitly at their write sites or are re-stamped on their next write.</para>
/// </summary>
public sealed class V49_FixAccessAssignmentMainNode : IMigration
{
    public int Version => 49;
    public string Description =>
        "Point AccessAssignment.main_node at the granted node instead of the _Access container (grants projected one level too deep) and rebuild permissions";

    public async Task RunAsync(MigrationContext ctx)
    {
        var schemas = new List<string>();
        await using (var discover = ctx.DataSource.CreateCommand("""
            SELECT t.table_schema
            FROM information_schema.tables t
            WHERE t.table_name = 'access'
              AND t.table_schema NOT IN ('information_schema','pg_catalog','pg_toast')
              AND t.table_schema NOT LIKE '%\_versions'
            ORDER BY t.table_schema
            """))
        await using (var rdr = await discover.ExecuteReaderAsync())
        {
            while (await rdr.ReadAsync())
                schemas.Add(rdr.GetString(0));
        }

        var totalFixed = 0;
        foreach (var schema in schemas)
        {
            var quotedSchema = "\"" + schema.Replace("\"", "\"\"") + "\"";

            // Cut main_node at its FIRST _Access segment: 'Space/_Access' -> 'Space',
            // 'Space/_Access/a1/_Activity' -> 'Space'. The regex anchors on a full SEGMENT, so
            // 'Space/_AccessLog' — an ordinary node — is untouched.
            //
            // A ROOT-LEVEL '_Access' main_node is deliberately NOT repaired here. Its correct value
            // is '' — and '' is the projection's MESH-WIDE prefix (what GlobalAdminSeed writes on
            // purpose for platform admins), so rewriting it would turn dormant rows into global
            // grants during an upgrade. New writes get '' from the fixed stamp; existing ones are
            // reported below for an operator to review deliberately.
            int affected;
            await using (var fix = ctx.DataSource.CreateCommand($"""
                UPDATE {quotedSchema}.access
                SET main_node = regexp_replace(main_node, '/_Access(/.*)?$', '')
                WHERE main_node IS NOT NULL
                  AND main_node ~ '/_Access(/|$)'
                """))
            {
                affected = await fix.ExecuteNonQueryAsync();
            }

            await using (var rootScoped = ctx.DataSource.CreateCommand($"""
                SELECT count(*) FROM {quotedSchema}.access WHERE main_node ~ '^_Access(/|$)'
                """))
            {
                if (await rootScoped.ExecuteScalarAsync() is long count and > 0)
                    ctx.Logger.LogWarning(
                        "Repair v49: '{Schema}' — {Count} grant(s) carry the root-level main_node '_Access'. "
                        + "Left as-is: their correct value ('') is a MESH-WIDE grant, so activating them is an "
                        + "explicit admin decision, not an upgrade side effect", schema, count);
            }

            if (affected == 0)
                continue;

            totalFixed += affected;
            ctx.Logger.LogInformation(
                "Repair v49: '{Schema}' — {Count} AccessAssignment main_node(s) re-pointed at the granted node",
                schema, affected);

            // The permission rows were materialized from the old prefix — re-project them. Guarded:
            // a schema without the rebuild function (very old / bare) still gets its data repaired.
            try
            {
                await using var rebuild = ctx.DataSource.CreateCommand(
                    $"SELECT {quotedSchema}.rebuild_user_effective_permissions()");
                await rebuild.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                ctx.Logger.LogWarning(ex,
                    "Repair v49: '{Schema}' — permission rebuild failed; rows are repaired and the next "
                    + "access write (or the boot-time self-heal) re-projects them", schema);
            }
        }

        ctx.Logger.LogInformation(
            "Repair v49: done — {Count} main_node(s) fixed across {Schemas} schema(s)", totalFixed, schemas.Count);
    }
}
