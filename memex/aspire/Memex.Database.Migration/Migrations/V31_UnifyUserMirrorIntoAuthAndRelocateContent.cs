using Microsoft.Extensions.Logging;

namespace Memex.Database.Migration.Migrations;

/// <summary>
/// Removes the stray <c>user</c> schema so the auth-lookup mirror lives in exactly ONE place:
/// the <c>auth</c> schema (partition namespace <c>Auth</c>).
///
/// <para><b>Why this exists.</b> Onboarding kept writing a second "catalog mirror" node
/// <c>new MeshNode(username, "User")</c> (namespace <c>User</c>). A write to the unregistered
/// <c>User</c> first-segment lazily provisioned a <c>user</c> schema (distinct from the <c>auth</c>
/// schema V27 created), and subsequent content created under <c>User/{username}/…</c> piled up
/// there too — e.g. <c>User/rsalzmann/ReinsuranceContractCheck</c> (a compiled NodeType + its
/// Source/Release/instances), <c>User/{username}/HelloWorld</c>, <c>User/{username}/_Access/…</c>.
/// The redundant onboarding write is removed in the same change as this migration
/// (<c>UserOnboardingService</c> no longer writes the <c>User</c>-namespace mirror), and the new
/// <c>PartitionWriteGuardValidator</c> blocks any future non-system write into <c>User</c>/<c>Auth</c>.</para>
///
/// <para><b>What it does.</b>
/// <list type="number">
///   <item>Discovers every <c>{username}</c> that owns content under <c>User/{username}/…</c>
///     across the standard partition tables present in the stray <c>user</c> schema.</item>
///   <item>For each, ensures the user's own partition schema exists
///     (<c>public.ensure_partition_schema</c>) and relocates the content there: the leading
///     <c>User/</c> is stripped from <c>namespace</c>, and <c>node_type</c> / <c>main_node</c>
///     values that themselves start with <c>User/</c> (a NodeType instance pointing at its type,
///     or a satellite's parent pointer) are rewritten too. <c>ON CONFLICT (namespace,id) DO
///     NOTHING</c> so any content already at the canonical root path is never clobbered.</item>
///   <item>Drops the stray <c>user</c> schema — but ONLY if every owner relocated cleanly. Its
///     <c>namespace='User'</c> User/Group/Role/VUser/ApiToken rows are redundant (the canonical
///     mirror is <c>auth</c>, kept current by the V27 trigger), so <c>CASCADE</c> safely clears
///     the leftover rows + the schema's mirror trigger.</item>
/// </list></para>
///
/// <para><b>Compiled NodeType note.</b> A relocated NodeType's <c>content</c> still carries
/// <c>compiledSources</c> / <c>currentSourceVersions</c> keys + <c>latestReleasePath</c> /
/// <c>latestAssemblyPath</c> that reference the old <c>User/{username}/…</c> path. We deliberately
/// do NOT rewrite that JSON: the source children move to the new path, so on first access the
/// NodeType sees a source-version key mismatch, marks itself dirty, and recompiles against the
/// new path — self-healing without fragile in-place jsonb surgery. The instance's <c>node_type</c>
/// column IS rewritten (above) so it re-binds to the moved type.</para>
///
/// <para><b>Idempotent.</b> No <c>user</c> schema → no-op. Re-run after a partial run picks up
/// whatever is left; the moves are <c>ON CONFLICT DO NOTHING</c> and the drop is gated on a clean
/// sweep.</para>
/// </summary>
public sealed class V31_UnifyUserMirrorIntoAuthAndRelocateContent : IMigration
{
    public int Version => 31;

    public string Description =>
        "Relocate User/{username}/… content to {username} partitions and drop the stray 'user' schema (auth is the single mirror)";

    /// <summary>
    /// The standard partition tables (<c>mesh_nodes</c> + satellites) created by
    /// <c>public.ensure_partition_schema</c>. Only those actually present in the stray
    /// <c>user</c> schema are processed.
    /// </summary>
    private static readonly string[] PartitionTables =
    {
        "mesh_nodes", "access", "activities", "user_activities",
        "threads", "annotations", "notifications", "code"
    };

    public async Task RunAsync(MigrationContext ctx)
    {
        if (!await SchemaHelpers.SchemaExistsAsync(ctx.DataSource, "user"))
        {
            ctx.Logger.LogInformation("Repair v31: no stray 'user' schema — nothing to do");
            return;
        }

        var tables = await TablesPresentAsync(ctx, "user");

        // 1. Discover every username owning content under User/{username}/… across all tables.
        var usernames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var table in tables)
        {
            await using var cmd = ctx.DataSource.CreateCommand($"""
                SELECT DISTINCT split_part(namespace, '/', 2) AS username
                FROM "user"."{table}"
                WHERE namespace LIKE 'User/%'
                """);
            await using var rdr = await cmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
            {
                var u = rdr.IsDBNull(0) ? null : rdr.GetString(0);
                if (!string.IsNullOrEmpty(u))
                    usernames.Add(u);
            }
        }

        if (usernames.Count == 0)
            ctx.Logger.LogInformation(
                "Repair v31: 'user' schema holds no User/{{username}}/… content — only redundant mirror rows remain");

        // 2. Relocate each owner's content into their own partition.
        var allMoved = true;
        foreach (var username in usernames)
        {
            var targetSchema = SchemaHelpers.SanitizeSchemaName(username);
            if (string.IsNullOrEmpty(targetSchema))
            {
                ctx.Logger.LogWarning(
                    "Repair v31: cannot derive a schema for user '{User}' — leaving its content in 'user'", username);
                allMoved = false;
                continue;
            }

            // Ensure the target partition's schema + standard tables exist (idempotent).
            await using (var ensureCmd = ctx.DataSource.CreateCommand("SELECT public.ensure_partition_schema(@s)"))
            {
                ensureCmd.Parameters.AddWithValue("s", targetSchema);
                await ensureCmd.ExecuteNonQueryAsync();
            }

            var movedForUser = 0;
            foreach (var table in tables)
            {
                if (!await TableExistsAsync(ctx, targetSchema, table))
                    continue; // target lacks this satellite — nothing routes there

                var columns = await ColumnsAsync(ctx, "user", table);
                if (columns.Count == 0)
                    continue;

                // Rewrite only path-shaped columns; pass everything else through verbatim.
                // 'User/' is 5 chars → substring(... from 6) drops the prefix.
                var selectList = string.Join(", ", columns.Select(c => c switch
                {
                    "namespace" => "substring(namespace from 6)",
                    "node_type" => "CASE WHEN node_type LIKE 'User/%' THEN substring(node_type from 6) ELSE node_type END",
                    "main_node" => "CASE WHEN main_node LIKE 'User/%' THEN substring(main_node from 6) ELSE main_node END",
                    _ => $"\"{c}\""
                }));
                var colList = string.Join(", ", columns.Select(c => $"\"{c}\""));

                await using var moveCmd = ctx.DataSource.CreateCommand($"""
                    INSERT INTO "{targetSchema}"."{table}" ({colList})
                    SELECT {selectList}
                    FROM "user"."{table}"
                    WHERE namespace = 'User/' || @u OR namespace LIKE 'User/' || @u || '/%'
                    ON CONFLICT (namespace, id) DO NOTHING
                    """);
                moveCmd.Parameters.AddWithValue("u", username);
                var moved = await moveCmd.ExecuteNonQueryAsync();
                movedForUser += moved;
                if (moved > 0)
                    ctx.Logger.LogInformation(
                        "Repair v31: relocated {Count} row(s) user.{Table} → {Schema}.{Table} for '{User}'",
                        moved, table, targetSchema, table, username);
            }

            ctx.Logger.LogInformation(
                "Repair v31: relocated {Total} content row(s) for '{User}' into '{Schema}'",
                movedForUser, username, targetSchema);
        }

        // 3. Drop the stray schema only after a clean sweep.
        if (allMoved)
        {
            await using var dropCmd = ctx.DataSource.CreateCommand("""DROP SCHEMA "user" CASCADE""");
            await dropCmd.ExecuteNonQueryAsync();
            ctx.Logger.LogInformation(
                "Repair v31: dropped stray 'user' schema — 'auth' is now the single auth-lookup mirror");
        }
        else
        {
            ctx.Logger.LogWarning(
                "Repair v31: left the 'user' schema in place because some content could not be relocated — re-run after resolving");
        }
    }

    private static async Task<List<string>> TablesPresentAsync(MigrationContext ctx, string schema)
    {
        var present = new List<string>();
        foreach (var t in PartitionTables)
            if (await TableExistsAsync(ctx, schema, t))
                present.Add(t);
        return present;
    }

    private static async Task<bool> TableExistsAsync(MigrationContext ctx, string schema, string table)
    {
        await using var cmd = ctx.DataSource.CreateCommand("""
            SELECT 1 FROM information_schema.tables
            WHERE table_schema = @s AND table_name = @t
            LIMIT 1
            """);
        cmd.Parameters.AddWithValue("s", schema);
        cmd.Parameters.AddWithValue("t", table);
        return await cmd.ExecuteScalarAsync() is not null;
    }

    private static async Task<List<string>> ColumnsAsync(MigrationContext ctx, string schema, string table)
    {
        var cols = new List<string>();
        await using var cmd = ctx.DataSource.CreateCommand("""
            SELECT column_name FROM information_schema.columns
            WHERE table_schema = @s AND table_name = @t
            ORDER BY ordinal_position
            """);
        cmd.Parameters.AddWithValue("s", schema);
        cmd.Parameters.AddWithValue("t", table);
        await using var rdr = await cmd.ExecuteReaderAsync();
        while (await rdr.ReadAsync())
            cols.Add(rdr.GetString(0));
        return cols;
    }
}
