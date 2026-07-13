using System.Collections.Immutable;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Snowflake;

/// <summary>
/// C# replacement for PostgreSQL's permission-rebuild trigger machinery — a faithful
/// transcription of the plpgsql bodies in <c>PostgreSqlSchemaInitializer</c>
/// (<c>rebuild_user_effective_permissions()</c>, <c>rebuild_user_permissions_for(p_user_id)</c>
/// and the <c>partition_access</c> sync they both perform). Snowflake has no triggers and no
/// procedural rebuild functions, so <see cref="SnowflakeStorageAdapter"/> calls
/// <see cref="RebuildOnConnectionAsync"/> from its write/delete leaves whenever the
/// <c>access</c> satellite table or a projection-input node
/// (<c>GroupMembership</c>/<c>Role</c>/<c>PartitionAccessPolicy</c>) changes.
///
/// <para><b>Transcribed semantics</b> (diffable against the plpgsql):
/// <list type="number">
///   <item><b>Direct AccessAssignment entries</b> — every row of <c>{schema}.access</c> whose
///     content carries a non-null <c>accessObject</c> and a <c>roles</c> array yields, per role
///     entry, the role's permission names at prefix <c>COALESCE(main_node, namespace)</c> with
///     <c>is_allow = NOT denied</c>.</item>
///   <item><b>Group flattening (GroupMembership nodes)</b> — a recursive closure over
///     <c>{"member":"...","groups":[{"group":"..."}]}</c> nodes resolves each group to its LEAF
///     members (members that are not themselves groups); leaf members inherit the group's
///     access rows. Set-based closure = cycle-safe (plpgsql's <c>UNION</c> recursion).</item>
///   <item><b>Direct access_control entries</b> — rows of <c>{schema}.access_control</c> merge
///     as-is (subject → user).</item>
///   <item><b>Group flattening (group_members table)</b> — same closure over
///     <c>{schema}.group_members</c>; leaf members inherit the group's access_control rows.</item>
///   <item><b>PartitionAccessPolicy caps</b> — for every <c>_Policy</c> node, each permission
///     field (<c>read/create/update/delete/comment</c>) explicitly set to <c>false</c> FORCES
///     <c>is_allow = false</c> at the policy's namespace for EVERY user present in the
///     projection so far (plpgsql's <c>ON CONFLICT ... DO UPDATE SET is_allow = false</c>).</item>
///   <item><b>Deny-wins merge</b> — steps 1-4 merge with
///     <c>is_allow = CASE WHEN EXCLUDED.is_allow = false THEN false ELSE existing END</c>,
///     i.e. logical AND: once a key is denied it stays denied.</item>
///   <item><b><c>partition_access</c> sync</b> — users holding any allowed <c>Read</c> row get
///     a row in <c>{centralSchema}.partition_access</c>; users no longer allowed are deleted.
///     An unprovisioned central table is a silent no-op (plpgsql's
///     <c>EXCEPTION WHEN undefined_table THEN NULL</c>).</item>
/// </list></para>
///
/// <para><b>Write shape</b>: PG rebuilds into a shadow table and atomically rename-swaps; that
/// trick exists only because plpgsql cannot hold the result in memory. Here the projection is
/// computed in memory and written as DELETE + chunked multi-row INSERT inside an explicit
/// transaction (Snowflake autocommits per statement otherwise; on the LocalStack emulator,
/// where <c>BeginTransaction</c> may be unsupported, the statements degrade to plain sequential
/// execution — an acceptable, documented relaxation). The <c>partition_access</c> sync runs
/// after the commit as its own best-effort step, mirroring the plpgsql's inner
/// exception-guarded block.</para>
/// </summary>
internal static class SnowflakeAccessProjection
{
    /// <summary>
    /// The role permission bitmask → permission-name expansion table, verbatim from the plpgsql
    /// <c>unnest(CASE WHEN (r.permissions &amp; bit) &gt; 0 ...)</c> cascade. Bit 512 is
    /// deliberately unmapped — the plpgsql skips it too.
    /// </summary>
    private static readonly ImmutableArray<(int Bit, string Permission)> PermissionBits =
    [
        (1, "Read"),
        (2, "Create"),
        (4, "Update"),
        (8, "Delete"),
        (16, "Comment"),
        (32, "Execute"),
        (64, "Thread"),
        (128, "Api"),
        (256, "Export"),
        (1024, "Compile")
    ];

    /// <summary>
    /// Built-in role fallback bitmasks, verbatim from the plpgsql <c>CASE role_entry-&gt;&gt;'role'</c>
    /// (used only when no <c>Role</c> node with that id overrides them): Admin/PlatformAdmin
    /// 1535, Editor 1527, Viewer 161, Commenter 145, anything else 0.
    /// </summary>
    private static readonly ImmutableDictionary<string, int> BuiltInRoleBitmasks =
        ImmutableDictionary.CreateRange(StringComparer.Ordinal,
        [
            KeyValuePair.Create("Admin", 1535),
            KeyValuePair.Create("PlatformAdmin", 1535),
            KeyValuePair.Create("Editor", 1527),
            KeyValuePair.Create("Viewer", 161),
            KeyValuePair.Create("Commenter", 145)
        ]);

    /// <summary>
    /// The <c>_Policy</c> cap fields — permission name / content field pairs, verbatim from the
    /// plpgsql <c>unnest(ARRAY['Read',...]) / unnest(ARRAY['read',...])</c> zip.
    /// </summary>
    private static readonly ImmutableArray<(string Permission, string Field)> PolicyCapFields =
    [
        ("Read", "read"),
        ("Create", "create"),
        ("Update", "update"),
        ("Delete", "delete"),
        ("Comment", "comment")
    ];

    /// <summary>Rows to insert per statement in the batched projection write (4 binds per row).</summary>
    private const int InsertChunkSize = 200;

    /// <summary>One parsed role entry of an AccessAssignment's <c>roles</c> array.</summary>
    private sealed record RoleEntry(string? Role, bool Denied);

    /// <summary>One parsed row of the <c>access</c> satellite table.</summary>
    private sealed record AccessRow(string? AccessObject, string Prefix, ImmutableList<RoleEntry> Roles);

    /// <summary>One parsed <c>GroupMembership</c> node (member + the groups it belongs to).</summary>
    private sealed record MembershipNode(string Member, ImmutableList<string> Groups);

    /// <summary>One parsed <c>PartitionAccessPolicy</c> (<c>_Policy</c>) node.</summary>
    private sealed record PolicyNode(string Namespace, ImmutableList<string> DeniedPermissions);

    /// <summary>One row of the convenience <c>access_control</c> table.</summary>
    private sealed record AccessControlRow(string NodePath, string Subject, string Permission, bool IsAllow);

    /// <summary>
    /// Rebuilds <c>{schema}.user_effective_permissions</c> from the projection inputs and syncs
    /// <c>{centralSchema}.partition_access</c>, all on the CALLER's open connection so the work
    /// stays inside the caller's I/O-pool slot (the storage adapter invokes this from its cap-1
    /// write pool; <see cref="SnowflakeAccessControl.RebuildDenormalizedTableAsync"/> from its
    /// own leaf). Async I/O leaf — never call from a hub scheduler.
    /// </summary>
    /// <param name="connection">Open connection to run every statement on.</param>
    /// <param name="schema">The partition schema whose projection is rebuilt (already lowercased by the router).</param>
    /// <param name="centralSchema">The central schema holding <c>partition_access</c> (default <c>public</c>).</param>
    /// <param name="logger">Optional diagnostics logger.</param>
    /// <param name="ct">Cancellation token.</param>
    internal static async Task RebuildOnConnectionAsync(
        DbConnection connection,
        string schema,
        string centralSchema,
        ILogger? logger,
        CancellationToken ct)
    {
        // ── 1. READ the projection inputs (same relations the plpgsql reads) ─────────────
        var accessRows = await ReadAccessRowsAsync(connection, schema, logger, ct).ConfigureAwait(false);
        var (roleBitmasks, memberships, policies) =
            await ReadMeshNodeInputsAsync(connection, schema, logger, ct).ConfigureAwait(false);
        var accessControlRows = await ReadAccessControlAsync(connection, schema, logger, ct).ConfigureAwait(false);
        var groupMemberRows = await ReadGroupMembersAsync(connection, schema, logger, ct).ConfigureAwait(false);

        // ── 2. COMPUTE in memory ──────────────────────────────────────────────────────────
        var projection = ComputeProjection(
            accessRows, roleBitmasks, memberships, policies, accessControlRows, groupMemberRows);

        // ── 3. WRITE the projection (transactional delete + batched insert) ──────────────
        await WriteProjectionAsync(connection, schema, projection, logger, ct).ConfigureAwait(false);

        // ── 4. SYNC {centralSchema}.partition_access (best-effort, like the plpgsql's
        //       undefined_table-guarded block) ─────────────────────────────────────────────
        var allowedReaders = projection
            .Where(kv => kv.Key.Permission == "Read" && kv.Value)
            .Select(kv => kv.Key.User)
            .Distinct(StringComparer.Ordinal)
            .ToImmutableHashSet(StringComparer.Ordinal);
        await SyncPartitionAccessAsync(connection, schema, centralSchema, allowedReaders, logger, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The pure computation: steps 1-5 of the plpgsql spec folded into one deny-wins map keyed
    /// by <c>(user_id, node_path_prefix, permission)</c>. Kept side-effect free so the
    /// transcription can be unit-tested against the plpgsql semantics directly.
    /// </summary>
    private static Dictionary<(string User, string Prefix, string Permission), bool> ComputeProjection(
        IReadOnlyList<AccessRow> accessRows,
        IReadOnlyDictionary<string, int> roleBitmasks,
        IReadOnlyList<MembershipNode> memberships,
        IReadOnlyList<PolicyNode> policies,
        IReadOnlyList<AccessControlRow> accessControlRows,
        IReadOnlyList<(string GroupName, string MemberId)> groupMemberRows)
    {
        var map = new Dictionary<(string User, string Prefix, string Permission), bool>();

        // Deny-wins merge — the plpgsql ON CONFLICT:
        //   SET is_allow = CASE WHEN EXCLUDED.is_allow = false THEN false ELSE existing END
        // which is exactly logical AND of the incoming and existing values.
        void Merge(string user, string prefix, string permission, bool isAllow)
        {
            var key = (user, prefix, permission);
            map[key] = map.TryGetValue(key, out var existing) ? existing && isAllow : isAllow;
        }

        // Role resolution: a Role node's content.permissions overrides; otherwise the built-in
        // fallback table; unknown roles resolve to 0 (no permissions) — verbatim plpgsql.
        int ResolveRoleBitmask(string? role)
            => role is null
                ? 0
                : roleBitmasks.TryGetValue(role, out var custom)
                    ? custom
                    : BuiltInRoleBitmasks.GetValueOrDefault(role, 0);

        IEnumerable<string> Expand(int bitmask)
            => PermissionBits.Where(b => (bitmask & b.Bit) > 0).Select(b => b.Permission);

        // Step 1 — direct AccessAssignment entries (accessObject + roles required).
        foreach (var row in accessRows)
        {
            if (row.AccessObject is null)
                continue;
            foreach (var entry in row.Roles)
                foreach (var permission in Expand(ResolveRoleBitmask(entry.Role)))
                    Merge(row.AccessObject, row.Prefix, permission, !entry.Denied);
        }

        // Step 2 — GroupMembership flattening. all_members closure: base pairs
        // (group, member) from every node's groups array; recursion adds (group, m.Member)
        // whenever a membership node m lists an existing pair's member as one of ITS groups
        // (a member that is itself a group contributes its own members). HashSet semantics =
        // the plpgsql UNION's dedup = cycle safety.
        var allPairs = new HashSet<(string Group, string Member)>();
        foreach (var m in memberships)
            foreach (var g in m.Groups)
                allPairs.Add((g, m.Member));
        var added = true;
        while (added)
        {
            added = false;
            foreach (var (group, member) in allPairs.ToList())
                foreach (var m in memberships)
                    if (m.Groups.Contains(member, StringComparer.Ordinal) && allPairs.Add((group, m.Member)))
                        added = true;
        }
        // leaf_members: pairs whose member is not itself a group (no membership node lists it
        // among its groups).
        var nodeGroupIds = memberships
            .SelectMany(m => m.Groups)
            .ToImmutableHashSet(StringComparer.Ordinal);
        var leafPairs = allPairs.Where(p => !nodeGroupIds.Contains(p.Member)).ToList();

        foreach (var row in accessRows)
        {
            if (row.AccessObject is null)
                continue;
            foreach (var (group, member) in leafPairs)
            {
                if (!string.Equals(group, row.AccessObject, StringComparison.Ordinal))
                    continue;
                foreach (var entry in row.Roles)
                    foreach (var permission in Expand(ResolveRoleBitmask(entry.Role)))
                        Merge(member, row.Prefix, permission, !entry.Denied);
            }
        }

        // Step 3 — direct access_control entries.
        foreach (var row in accessControlRows)
            Merge(row.Subject, row.NodePath, row.Permission, row.IsAllow);

        // Step 4 — group_members flattening + access_control (same closure over the plain
        // (group_name, member_id) table).
        var gmPairs = new HashSet<(string Group, string Member)>(groupMemberRows);
        added = true;
        while (added)
        {
            added = false;
            foreach (var (group, member) in gmPairs.ToList())
                foreach (var (gn, mid) in groupMemberRows)
                    if (string.Equals(gn, member, StringComparison.Ordinal) && gmPairs.Add((group, mid)))
                        added = true;
        }
        var gmGroupNames = groupMemberRows.Select(r => r.GroupName).ToImmutableHashSet(StringComparer.Ordinal);
        var gmLeafPairs = gmPairs.Where(p => !gmGroupNames.Contains(p.Member)).ToList();
        foreach (var ac in accessControlRows)
            foreach (var (group, member) in gmLeafPairs)
                if (string.Equals(group, ac.Subject, StringComparison.Ordinal))
                    Merge(member, ac.NodePath, ac.Permission, ac.IsAllow);

        // Step 5 — PartitionAccessPolicy caps: FORCE false at the policy namespace for every
        // user present so far (the plpgsql snapshots `SELECT DISTINCT user_id FROM shadow` at
        // exactly this point) — an unconditional overwrite, not a deny-wins merge.
        var usersSoFar = map.Keys.Select(k => k.User).Distinct(StringComparer.Ordinal).ToList();
        foreach (var policy in policies)
            foreach (var permission in policy.DeniedPermissions)
                foreach (var user in usersSoFar)
                    map[(user, policy.Namespace, permission)] = false;

        return map;
    }

    /// <summary>
    /// Reads every row of <c>{schema}.access</c> (content JSON: <c>accessObject</c> +
    /// <c>roles</c>), with prefix <c>COALESCE(main_node, namespace)</c> resolved in SQL like
    /// the plpgsql. Absent table → empty (unprovisioned partition). A poisoned content payload
    /// skips THAT row with a warning instead of faulting the rebuild.
    /// </summary>
    private static async Task<IReadOnlyList<AccessRow>> ReadAccessRowsAsync(
        DbConnection connection, string schema, ILogger? logger, CancellationToken ct)
    {
        var rows = new List<AccessRow>();
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText =
                $"SELECT \"content\", COALESCE(\"main_node\", \"namespace\") " +
                $"FROM {SnowflakeIdentifiers.Qualify(schema, "access")} WHERE \"content\" IS NOT NULL";
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var json = reader.GetString(0);
                var prefix = reader.IsDBNull(1) ? "" : reader.GetString(1);
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    var accessObject = GetStringProperty(root, "accessObject");
                    if (!root.TryGetProperty("roles", out var roles) || roles.ValueKind != JsonValueKind.Array)
                        continue; // plpgsql: content->'roles' IS NOT NULL required
                    var entries = ImmutableList.CreateBuilder<RoleEntry>();
                    foreach (var entry in roles.EnumerateArray())
                    {
                        if (entry.ValueKind != JsonValueKind.Object)
                            continue;
                        entries.Add(new RoleEntry(
                            GetStringProperty(entry, "role"),
                            GetBoolProperty(entry, "denied") ?? false));
                    }
                    rows.Add(new AccessRow(accessObject, prefix, entries.ToImmutable()));
                }
                catch (JsonException ex)
                {
                    logger?.LogWarning(ex,
                        "[Snowflake] Skipping poisoned access row (prefix {Prefix}) during permission rebuild.",
                        prefix);
                }
            }
        }
        catch (Exception ex) when (SnowflakeStorageAdapter.IsUndefinedObject(ex))
        {
            logger?.LogDebug(ex, "Permission rebuild: {Schema}.access not provisioned; no assignments.", schema);
        }
        return rows;
    }

    /// <summary>
    /// Reads the <c>mesh_nodes</c> projection inputs in one query: <c>Role</c> nodes (id →
    /// content.permissions bitmask; first row wins, mirroring the plpgsql's <c>LIMIT 1</c>),
    /// <c>GroupMembership</c> nodes (member + groups) and <c>PartitionAccessPolicy</c>
    /// <c>_Policy</c> nodes (namespace + explicitly-false permission fields).
    /// </summary>
    private static async Task<(
        IReadOnlyDictionary<string, int> RoleBitmasks,
        IReadOnlyList<MembershipNode> Memberships,
        IReadOnlyList<PolicyNode> Policies)> ReadMeshNodeInputsAsync(
        DbConnection connection, string schema, ILogger? logger, CancellationToken ct)
    {
        var roleBitmasks = new Dictionary<string, int>(StringComparer.Ordinal);
        var memberships = new List<MembershipNode>();
        var policies = new List<PolicyNode>();
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText =
                $"SELECT \"id\", \"namespace\", \"node_type\", \"content\" " +
                $"FROM {SnowflakeIdentifiers.Qualify(schema, "mesh_nodes")} " +
                "WHERE \"node_type\" IN ('Role', 'GroupMembership', 'PartitionAccessPolicy') " +
                "AND \"content\" IS NOT NULL";
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var id = reader.GetString(0);
                var ns = reader.IsDBNull(1) ? "" : reader.GetString(1);
                var nodeType = reader.GetString(2);
                var json = reader.GetString(3);
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    switch (nodeType)
                    {
                        case "Role":
                            // plpgsql: (role_node.content->>'permissions')::int ... LIMIT 1
                            if (GetIntProperty(root, "permissions") is { } bitmask
                                && !roleBitmasks.ContainsKey(id))
                                roleBitmasks[id] = bitmask;
                            break;

                        case "GroupMembership":
                            // {"member":"...","groups":[{"group":"..."}]}
                            var member = GetStringProperty(root, "member");
                            if (member is null
                                || !root.TryGetProperty("groups", out var groups)
                                || groups.ValueKind != JsonValueKind.Array)
                                break;
                            var groupList = ImmutableList.CreateBuilder<string>();
                            foreach (var entry in groups.EnumerateArray())
                                if (entry.ValueKind == JsonValueKind.Object
                                    && GetStringProperty(entry, "group") is { } group)
                                    groupList.Add(group);
                            memberships.Add(new MembershipNode(member, groupList.ToImmutable()));
                            break;

                        case "PartitionAccessPolicy":
                            // plpgsql: node_type = 'PartitionAccessPolicy' AND id = '_Policy'
                            // AND (content->>field)::boolean = false
                            if (id != "_Policy")
                                break;
                            var denied = PolicyCapFields
                                .Where(f => GetBoolProperty(root, f.Field) == false)
                                .Select(f => f.Permission)
                                .ToImmutableList();
                            if (!denied.IsEmpty)
                                policies.Add(new PolicyNode(ns, denied));
                            break;
                    }
                }
                catch (JsonException ex)
                {
                    logger?.LogWarning(ex,
                        "[Snowflake] Skipping poisoned {NodeType} node '{Id}' during permission rebuild.",
                        nodeType, id);
                }
            }
        }
        catch (Exception ex) when (SnowflakeStorageAdapter.IsUndefinedObject(ex))
        {
            logger?.LogDebug(ex, "Permission rebuild: {Schema}.mesh_nodes not provisioned; no node inputs.", schema);
        }
        return (roleBitmasks, memberships, policies);
    }

    /// <summary>Reads every row of the convenience <c>{schema}.access_control</c> table; absent table → empty.</summary>
    private static async Task<IReadOnlyList<AccessControlRow>> ReadAccessControlAsync(
        DbConnection connection, string schema, ILogger? logger, CancellationToken ct)
    {
        var rows = new List<AccessControlRow>();
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText =
                $"SELECT \"node_path\", \"subject\", \"permission\", \"is_allow\" " +
                $"FROM {SnowflakeIdentifiers.Qualify(schema, "access_control")}";
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                rows.Add(new AccessControlRow(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    Convert.ToBoolean(reader.GetValue(3), CultureInfo.InvariantCulture)));
        }
        catch (Exception ex) when (SnowflakeStorageAdapter.IsUndefinedObject(ex))
        {
            logger?.LogDebug(ex, "Permission rebuild: {Schema}.access_control not provisioned.", schema);
        }
        return rows;
    }

    /// <summary>Reads every row of the convenience <c>{schema}.group_members</c> table; absent table → empty.</summary>
    private static async Task<IReadOnlyList<(string GroupName, string MemberId)>> ReadGroupMembersAsync(
        DbConnection connection, string schema, ILogger? logger, CancellationToken ct)
    {
        var rows = new List<(string, string)>();
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText =
                $"SELECT \"group_name\", \"member_id\" FROM {SnowflakeIdentifiers.Qualify(schema, "group_members")}";
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                rows.Add((reader.GetString(0), reader.GetString(1)));
        }
        catch (Exception ex) when (SnowflakeStorageAdapter.IsUndefinedObject(ex))
        {
            logger?.LogDebug(ex, "Permission rebuild: {Schema}.group_members not provisioned.", schema);
        }
        return rows;
    }

    /// <summary>
    /// Writes the computed projection: <c>DELETE FROM {schema}.user_effective_permissions</c>
    /// followed by chunked multi-row <c>INSERT ... VALUES</c>, inside one explicit transaction
    /// when the endpoint supports it. Snowflake autocommits each statement outside an explicit
    /// transaction; readers between the delete and the last insert could then observe a partial
    /// projection — on real Snowflake the transaction prevents that (the equivalent of PG's
    /// shadow-table rename-swap), on the emulator the brief window is accepted.
    /// </summary>
    private static async Task WriteProjectionAsync(
        DbConnection connection,
        string schema,
        Dictionary<(string User, string Prefix, string Permission), bool> projection,
        ILogger? logger,
        CancellationToken ct)
    {
        var table = SnowflakeIdentifiers.Qualify(schema, "user_effective_permissions");

        DbTransaction? transaction = null;
        try
        {
            transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Emulator without transaction support: proceed with sequential autocommit
            // statements (documented relaxation — see the type doc).
            logger?.LogDebug(ex,
                "Permission rebuild: explicit transaction unavailable; writing {Schema} projection sequentially.",
                schema);
        }

        try
        {
            await using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = $"DELETE FROM {table}";
                await delete.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            foreach (var chunk in projection.ToList().Chunk(InsertChunkSize))
            {
                await using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                var valueRows = new List<string>(chunk.Length);
                for (var i = 0; i < chunk.Length; i++)
                {
                    valueRows.Add($"(:u{i}, :p{i}, :m{i}, :a{i})");
                    SnowflakeConnectionSource.AddParam(insert, $"u{i}", chunk[i].Key.User, DbType.String);
                    SnowflakeConnectionSource.AddParam(insert, $"p{i}", chunk[i].Key.Prefix, DbType.String);
                    SnowflakeConnectionSource.AddParam(insert, $"m{i}", chunk[i].Key.Permission, DbType.String);
                    SnowflakeConnectionSource.AddParam(insert, $"a{i}", chunk[i].Value, DbType.Boolean);
                }
                insert.CommandText =
                    $"INSERT INTO {table} (\"user_id\", \"node_path_prefix\", \"permission\", \"is_allow\") " +
                    $"VALUES {string.Join(", ", valueRows)}";
                await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            if (transaction is not null)
                await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            if (transaction is not null)
            {
                // CancellationToken.None: a caller-cancelled token must not prevent the rollback.
                try { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception rollbackEx)
                {
                    logger?.LogWarning(rollbackEx,
                        "Permission rebuild: rollback failed for {Schema}; session teardown will discard the transaction.",
                        schema);
                }
            }
            throw;
        }
        finally
        {
            if (transaction is not null)
                await transaction.DisposeAsync().ConfigureAwait(false);
        }

        logger?.LogDebug(
            "Permission projection rebuilt for {Schema}: {RowCount} effective-permission rows.",
            schema, projection.Count);
    }

    /// <summary>
    /// Syncs <c>{centralSchema}.partition_access</c> to the rebuilt projection — the C# twin of
    /// the sync blocks in BOTH plpgsql functions, generalized: every user holding an allowed
    /// <c>Read</c> row gets an insert-if-absent <c>(user_id, partition = schema)</c> row
    /// (Snowflake enforces no PK, so the guard is <c>WHERE NOT EXISTS</c> rather than
    /// <c>ON CONFLICT DO NOTHING</c>); users present in the table but no longer allowed get
    /// targeted DELETEs (batched IN-lists), mirroring <c>rebuild_user_permissions_for</c>'s
    /// per-user branch. An unprovisioned central table is a silent no-op (plpgsql:
    /// <c>EXCEPTION WHEN undefined_table THEN NULL</c>).
    /// </summary>
    private static async Task SyncPartitionAccessAsync(
        DbConnection connection,
        string schema,
        string centralSchema,
        ImmutableHashSet<string> allowedReaders,
        ILogger? logger,
        CancellationToken ct)
    {
        var table = SnowflakeIdentifiers.Qualify(centralSchema, "partition_access");
        try
        {
            var existing = new HashSet<string>(StringComparer.Ordinal);
            await using (var select = connection.CreateCommand())
            {
                select.CommandText = $"SELECT \"user_id\" FROM {table} WHERE \"partition\" = :partition";
                SnowflakeConnectionSource.AddParam(select, "partition", schema, DbType.String);
                await using var reader = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    existing.Add(reader.GetString(0));
            }

            foreach (var user in allowedReaders.Where(u => !existing.Contains(u)))
            {
                await using var insert = connection.CreateCommand();
                insert.CommandText = $"""
                    INSERT INTO {table} ("user_id", "partition")
                    SELECT :user_id, :partition
                    FROM (SELECT 1 AS "x")
                    WHERE NOT EXISTS (
                        SELECT 1 FROM {table} WHERE "user_id" = :user_id AND "partition" = :partition)
                    """;
                SnowflakeConnectionSource.AddParam(insert, "user_id", user, DbType.String);
                SnowflakeConnectionSource.AddParam(insert, "partition", schema, DbType.String);
                await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            var revoked = existing.Where(u => !allowedReaders.Contains(u)).ToList();
            foreach (var chunk in revoked.Chunk(InsertChunkSize))
            {
                await using var delete = connection.CreateCommand();
                var placeholders = new List<string>(chunk.Length);
                for (var i = 0; i < chunk.Length; i++)
                {
                    placeholders.Add($":u{i}");
                    SnowflakeConnectionSource.AddParam(delete, $"u{i}", chunk[i], DbType.String);
                }
                delete.CommandText =
                    $"DELETE FROM {table} WHERE \"partition\" = :partition " +
                    $"AND \"user_id\" IN ({string.Join(", ", placeholders)})";
                SnowflakeConnectionSource.AddParam(delete, "partition", schema, DbType.String);
                await delete.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (SnowflakeStorageAdapter.IsUndefinedObject(ex))
        {
            // partition_access may not exist yet (first boot ordering) — best-effort no-op.
            logger?.LogDebug(ex,
                "Permission rebuild: {Central}.partition_access not provisioned; sync skipped for {Schema}.",
                centralSchema, schema);
        }
    }

    /// <summary>Reads a string property; missing / non-string / JSON null → <c>null</c> (plpgsql <c>-&gt;&gt;</c>).</summary>
    private static string? GetStringProperty(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// Reads a boolean property tolerating both JSON booleans and text
    /// (plpgsql's <c>(x-&gt;&gt;field)::boolean</c> casts <c>"true"</c>/<c>"false"</c> text too);
    /// missing / unparseable → <c>null</c>.
    /// </summary>
    private static bool? GetBoolProperty(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    /// <summary>
    /// Reads an integer property tolerating both JSON numbers and numeric text
    /// (plpgsql's <c>(x-&gt;&gt;field)::int</c> casts text too); missing / unparseable → <c>null</c>.
    /// </summary>
    private static int? GetIntProperty(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(
                value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }
}
