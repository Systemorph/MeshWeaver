using Microsoft.Extensions.Logging;

namespace Memex.Database.Migration.Migrations;

/// <summary>
/// Retypes the retired BUILT-IN feedback nodes to the node-native Feedback plugin type.
///
/// <para><b>Background.</b> Core used to ship a built-in <c>Feedback</c> node type
/// (<c>MeshWeaver.Graph.Configuration.FeedbackNodeType.AddFeedbackType</c> registered the static
/// top-level <c>Feedback</c> node; <c>MeshWeaver.Mesh.Contract.Feedback</c> was its content record).
/// That built-in has been retired in favour of the node-native <b>Feedback plugin</b>
/// (<c>nodeType:"Feedback/Feedback"</c>, content <c>FeedbackContent</c>) which owns the shared
/// <c>Feedback</c> space and the editable preview-and-submit flow. Removing the built-in frees the
/// top-level <c>Feedback</c> path — the static node was the "existing node" that blocked creating the
/// plugin's Space — and drops the old content type, so any node still typed <c>Feedback</c> would
/// deserialize to null once core no longer registers that discriminator.</para>
///
/// <para><b>Fix.</b> Across every partition schema, retype <c>node_type = 'Feedback'</c> rows to
/// <c>'Feedback/Feedback'</c> and reshape their content (JSONB) from the old <c>Feedback</c> record to
/// <c>FeedbackContent</c>: <c>text→message</c>, <c>location→mainNodePath</c>,
/// <c>submittedAt→timestamp</c>, <c>submittedBy</c>/<c>submittedByName</c> preserved, <c>status</c>
/// defaulted to <c>New</c>, and the <c>$type</c> discriminator switched to <c>FeedbackContent</c>.
/// <c>jsonb_strip_nulls</c> drops fields that were absent. The per-schema history trigger snapshots
/// the change. <b>Idempotent</b>: a retyped row no longer matches <c>node_type = 'Feedback'</c>.</para>
///
/// <para><b>Type availability.</b> The live <c>Feedback/Feedback</c> type definition is supplied by
/// the Feedback plugin (compiled on the mesh when the plugin is installed). Until then a retyped node
/// renders as raw content — never errors — exactly like any node whose type is not yet compiled. The
/// migration must run BEFORE the plugin's <c>Feedback</c> Space can be created (it frees the path), so
/// this ordering is intentional.</para>
/// </summary>
public sealed class V48_RetypeBuiltinFeedbackToPlugin : IMigration
{
    public int Version => 48;

    public string Description =>
        "Retype built-in nodeType:Feedback nodes to the node-native Feedback plugin (Feedback/Feedback + FeedbackContent), reshaping the old Feedback content record";

    public async Task RunAsync(MigrationContext ctx)
    {
        if (ctx.IsFreshDb)
            return; // a fresh database never carried the built-in feedback nodes

        // Every partition schema that owns a mesh_nodes table. Skip the cross-schema *_versions
        // history layouts (they carry no live nodes of their own).
        var schemas = new List<string>();
        await using (var discover = ctx.DataSource.CreateCommand("""
            SELECT table_schema
            FROM information_schema.tables
            WHERE table_name = 'mesh_nodes'
              AND table_schema NOT IN ('information_schema','pg_catalog','pg_toast')
              AND table_schema NOT LIKE '%\_versions'
            ORDER BY table_schema
            """))
        await using (var rdr = await discover.ExecuteReaderAsync())
        {
            while (await rdr.ReadAsync())
                schemas.Add(rdr.GetString(0));
        }

        var totalRetyped = 0;
        foreach (var schema in schemas)
        {
            var quotedSchema = "\"" + schema.Replace("\"", "\"\"") + "\"";
            await using var cmd = ctx.DataSource.CreateCommand($"""
                UPDATE {quotedSchema}.mesh_nodes
                SET node_type = 'Feedback/Feedback',
                    content = jsonb_strip_nulls(jsonb_build_object(
                        '$type',           'FeedbackContent',
                        'message',         content->>'text',
                        'submittedBy',     content->>'submittedBy',
                        'submittedByName', content->>'submittedByName',
                        'timestamp',       content->>'submittedAt',
                        'mainNodePath',    content->>'location',
                        'status',          COALESCE(content->>'status', 'New')
                    ))
                WHERE node_type = 'Feedback'
                """);
            var n = await cmd.ExecuteNonQueryAsync();
            if (n > 0)
            {
                totalRetyped += n;
                ctx.Logger.LogInformation(
                    "V48: retyped {Count} built-in Feedback node(s) to Feedback/Feedback in schema {Schema}",
                    n, schema);
            }
        }

        ctx.Logger.LogInformation(
            "V48: retyped {Total} built-in Feedback node(s) across {Schemas} schema(s)",
            totalRetyped, schemas.Count);
    }
}
