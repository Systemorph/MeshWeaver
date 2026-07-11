using Microsoft.Extensions.Logging;

namespace Memex.Database.Migration.Migrations;

/// <summary>
/// Retrofits the per-provider brand logo onto EXISTING AI model-catalog nodes
/// (<c>node_type = 'LanguageModel' | 'ModelProvider'</c>) so they read as their maker
/// instead of the generic sparkle/key.
///
/// <para>Background: the live seeder now stamps a brand icon at create time
/// (<c>MeshWeaver.AI.ModelProviderIcons</c>, wired into <c>BuiltInLanguageModelProvider</c>),
/// but the platform catalog nodes are seeded <c>ExcludeThisAndChildren</c> — the importer
/// creates them once and never overwrites them — so an already-deployed catalog keeps its old
/// <c>"Sparkle"</c>/<c>"Key"</c> icons on upgrade. This one-time backfill patches the
/// <c>icon</c> column (a top-level <c>mesh_nodes</c> column, not inside <c>content</c>) for those nodes.</para>
///
/// <para>The brand map below MIRRORS <c>ModelProviderIcons.Brands</c> as of V41 — a deliberate,
/// point-in-time snapshot (a migration is a frozen historical artifact and must not depend on
/// live code that keeps evolving). New nodes get their icon from the live resolver; this migration
/// only backfills old rows.</para>
///
/// <para>Non-destructive + idempotent:</para>
/// <list type="bullet">
///   <item>Only rows whose icon is still a generic value (<c>Sparkle</c>/<c>Key</c> or the
///         <c>sparkle.svg</c>/<c>key.svg</c> defaults, or NULL) are touched — an admin's custom
///         icon is never clobbered.</item>
///   <item>Only when a brand actually resolves for the row, and only when it differs from the
///         current icon — so a second run patches nothing.</item>
/// </list>
///
/// <para>Runs across EVERY partition schema (not just <c>provider</c>): the platform catalog has
/// lived under <c>Provider</c> and <c>Admin/Provider</c> across versions, and users' own
/// bring-your-own provider/model nodes live in their partition schema — iterating by
/// <c>node_type</c> finds them all regardless of where they sit. Best-effort per schema
/// (log-and-continue), mirroring <see cref="V29_PinDocsForExistingUsers"/>.</para>
/// </summary>
public sealed class V41_RetrofitModelCatalogIcons : IMigration
{
    public int Version => 41;
    public string Description => "Retrofit per-provider brand icons onto existing LanguageModel/ModelProvider nodes";

    // One statement, run per schema on a single connection: (re)create a session-local resolver that
    // mirrors ModelProviderIcons, then UPDATE. CREATE OR REPLACE guards against a pooled connection
    // that already defined it. lower(v) LIKE '%token%' == the resolver's case-insensitive Contains;
    // WHEN order == the resolver's token order (first match wins), so mixtral/codestral precede
    // mistral and llama precedes meta.
    private const string Sql =
        """
        CREATE OR REPLACE FUNCTION pg_temp.brand_icon(v text) RETURNS text AS $fn$
          SELECT CASE
            WHEN v IS NULL THEN NULL
            WHEN lower(v) LIKE '%claude%'    OR lower(v) LIKE '%anthropic%'                                THEN 'anthropic'
            WHEN lower(v) LIKE '%gemini%'    OR lower(v) LIKE '%gemma%'                                    THEN 'google'
            WHEN lower(v) LIKE '%deepseek%'                                                                THEN 'deepseek'
            WHEN lower(v) LIKE '%mixtral%'   OR lower(v) LIKE '%codestral%' OR lower(v) LIKE '%ministral%'
              OR lower(v) LIKE '%magistral%' OR lower(v) LIKE '%devstral%'  OR lower(v) LIKE '%mistral%'   THEN 'mistral'
            WHEN lower(v) LIKE '%llama%'                                                                   THEN 'meta'
            WHEN lower(v) LIKE '%grok%'                                                                    THEN 'xai'
            WHEN lower(v) LIKE '%qwen%'      OR lower(v) LIKE '%qwq%'                                       THEN 'qwen'
            WHEN lower(v) LIKE '%copilot%'                                                                 THEN 'githubcopilot'
            WHEN lower(v) LIKE '%openrouter%'                                                              THEN 'openrouter'
            WHEN lower(v) LIKE '%perplexity%' OR lower(v) LIKE '%sonar%'                                   THEN 'perplexity'
            WHEN lower(v) LIKE '%ollama%'                                                                  THEN 'ollama'
            WHEN lower(v) LIKE '%gpt%'       OR lower(v) LIKE '%chatgpt%'   OR lower(v) LIKE '%davinci%'
              OR lower(v) LIKE '%dall-e%'    OR lower(v) LIKE '%openai%'                                   THEN 'openai'
            WHEN lower(v) LIKE '%meta%'                                                                    THEN 'meta'
            WHEN lower(v) LIKE '%xai%'                                                                     THEN 'xai'
            WHEN lower(v) LIKE '%azure%'     OR lower(v) LIKE '%microsoft%'                                THEN 'azure'
            ELSE NULL
          END
        $fn$ LANGUAGE sql IMMUTABLE;

        UPDATE mesh_nodes AS m
           SET icon = '/static/NodeTypeIcons/' || b.brand || '.svg',
               last_modified = NOW(),
               version = version + 1
          FROM (
            SELECT namespace, id,
                   CASE WHEN node_type = 'ModelProvider'
                        -- provider node: brand from its name, then its content.provider
                        THEN coalesce(pg_temp.brand_icon(name),
                                      pg_temp.brand_icon(content->>'provider'),
                                      pg_temp.brand_icon(content->>'Provider'))
                        -- model node: brand from the model id first (name/content.id), then its provider
                        ELSE coalesce(pg_temp.brand_icon(name),
                                      pg_temp.brand_icon(content->>'id'),
                                      pg_temp.brand_icon(content->>'Id'),
                                      pg_temp.brand_icon(content->>'provider'),
                                      pg_temp.brand_icon(content->>'Provider'))
                   END AS brand
              FROM mesh_nodes
             WHERE node_type IN ('LanguageModel', 'ModelProvider')
          ) AS b
         WHERE m.namespace = b.namespace
           AND m.id = b.id
           AND b.brand IS NOT NULL
           -- never clobber an admin's custom icon: only replace the generic defaults
           AND (m.icon IS NULL
                OR m.icon IN ('Sparkle', 'Key',
                              '/static/NodeTypeIcons/sparkle.svg',
                              '/static/NodeTypeIcons/key.svg'))
           -- idempotent: skip rows already on the target brand icon
           AND m.icon IS DISTINCT FROM '/static/NodeTypeIcons/' || b.brand || '.svg';
        """;

    public async Task RunAsync(MigrationContext ctx)
    {
        var schemas = await SchemaHelpers.DiscoverPartitionSchemasAsync(ctx.DataSource);
        var total = 0;
        foreach (var schema in schemas)
        {
            try
            {
                await using var ds = SchemaHelpers.BuildSchemaDataSource(ctx.ConnectionString, schema);
                await using var cmd = ds.CreateCommand(Sql);
                var updated = await cmd.ExecuteNonQueryAsync();
                if (updated > 0)
                {
                    total += updated;
                    ctx.Logger.LogInformation(
                        "[V41] Retrofitted brand icons on {Count} model/provider node(s) in schema {Schema}",
                        updated, schema);
                }
            }
            catch (Exception ex)
            {
                ctx.Logger.LogWarning(ex, "[V41] Skipped schema {Schema} while retrofitting model icons", schema);
            }
        }
        ctx.Logger.LogInformation("[V41] Retrofitted brand icons on {Total} model/provider node(s) across {Schemas} schema(s)",
            total, schemas.Count);
    }
}
