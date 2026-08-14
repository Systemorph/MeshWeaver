using Microsoft.Extensions.Logging;

namespace Memex.Database.Migration.Migrations;

/// <summary>
/// Retypes the built-in <c>Slide</c> / <c>Deck</c> nodes to the Publish pack's node-native types
/// (<c>Publish/Slide</c> / <c>Publish/Deck</c>).
///
/// <para><b>Background.</b> The Deck/Slide consolidation (#1589) moves presentation to the
/// pre-installed Publish plugin: <c>Publish/Slide</c> has carried all repo-authored slides since
/// July, and <c>Publish/Deck</c> (Publish v1.0.8) ships the deck Overview/Present areas compiled
/// in-mesh from the pack's shared slide source. The platform's built-in registrations are slated
/// for deletion — but live meshes hold core-typed nodes created directly on the mesh, outside any
/// git repo (AgenticPension on atioz; PartnerRe / PG3 presentations on systemorph). Deleting the
/// built-ins with those rows in place would strip the views from production content, so every
/// install must retype first — this migration is that retype, and the deletion lands only a
/// release AFTER it has run everywhere.</para>
///
/// <para><b>Fix.</b> Across every partition schema, retype <c>node_type = 'Slide'</c> rows to
/// <c>'Publish/Slide'</c> and <c>node_type = 'Deck'</c> rows to <c>'Publish/Deck'</c>. The content
/// is NOT reshaped: the pack's <c>SlideContent</c> / <c>DeckContent</c> records carry the same
/// fields and the same short <c>$type</c> discriminators as the built-ins, and
/// <c>ContentAs&lt;T&gt;</c> recovers same-short-named types across assemblies by design. The
/// per-schema history trigger snapshots the change. <b>Idempotent</b>: a retyped row no longer
/// matches the bare type name.</para>
///
/// <para><b>Type availability.</b> Publish is <c>preInstalled: true</c>, so every install carries
/// the target types; until the pack's next compile a retyped node renders as raw content — never
/// errors — exactly like any node whose type is not yet compiled (same reasoning as
/// <see cref="V48_RetypeBuiltinFeedbackToPlugin"/>).</para>
/// </summary>
public sealed class V53_RetypeBuiltinSlideDeckToPublish : IMigration
{
    public int Version => 53;

    public string Description =>
        "Retype built-in nodeType:Slide/Deck nodes to the Publish pack's Publish/Slide and Publish/Deck (content unchanged — same records, same $type discriminators)";

    public async Task RunAsync(MigrationContext ctx)
    {
        if (ctx.IsFreshDb)
            return; // a fresh database never carried built-in slide or deck nodes

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
                SET node_type = 'Publish/' || node_type
                WHERE node_type IN ('Slide', 'Deck')
                """);
            var n = await cmd.ExecuteNonQueryAsync();
            if (n > 0)
            {
                totalRetyped += n;
                ctx.Logger.LogInformation(
                    "V53: retyped {Count} built-in Slide/Deck node(s) to Publish/* in schema {Schema}",
                    n, schema);
            }
        }

        ctx.Logger.LogInformation(
            "V53: retyped {Total} built-in Slide/Deck node(s) across {Schemas} schema(s)",
            totalRetyped, schemas.Count);
    }
}
