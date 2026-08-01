namespace Memex.Database.Migration.Migrations;

/// <summary>
/// Rescopes platform-admin grants from ROOT to the Admin partition, then rebuilds the
/// permissions derived from them.
///
/// <para><b>The bug.</b> The two platform-admin writers — <c>GlobalAdminSeed</c> (config-driven)
/// and <c>UserOnboardingService.GrantPlatformAdmin</c> (first-user bootstrap) — wrote their
/// <c>Admin/_Access/{user}_Access</c> grants with <c>main_node = ''</c>. The permission projection
/// (<c>rebuild_user_effective_permissions()</c>) scopes a grant by <c>main_node</c>, and <c>''</c>
/// is the MESH-WIDE prefix: every such "platform admin" was silently a data superuser holding All
/// on every partition, space and private home. <c>AccessAssignmentGuard</c> now refuses the shape
/// at the write boundary (both halves must agree: path scope <c>Admin</c> ⇒ <c>MainNode='Admin'</c>),
/// and the writers are fixed — this migration repairs the rows already stored.</para>
///
/// <para><b>Effect on existing admins.</b> They KEEP the platform gates — <c>hub.IsGlobalAdmin()</c>
/// reads <c>Permission.All</c> at scope <c>Admin</c>, which a <c>main_node='Admin'</c> grant
/// provides — and LOSE standing root data access, which is the documented model (a global admin is
/// NOT a data superuser; cross-partition change is break-glass elevation, never standing). See
/// Doc/Architecture/AccessControl.md → "The scope invariant".</para>
///
/// <para>Idempotent: only touches <c>namespace = 'Admin/_Access'</c> rows whose <c>main_node</c>
/// is still NULL/empty; re-running matches nothing. V49 deliberately left these rows alone (the
/// empty value was then believed intentional); this migration completes that repair under the
/// guard's now-enforced invariant.</para>
/// </summary>
public sealed class V50_RescopePlatformAdminGrants : IMigration
{
    public int Version => 50;
    public string Description =>
        "Rescope Admin/_Access platform-admin grants from root (main_node='') to the Admin partition (main_node='Admin') and rebuild permissions";

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

            // Only grants FILED under Admin/_Access: their path encodes scope 'Admin', so an
            // empty main_node is the guard-refused mismatch (root grant in admin clothing).
            // Deliberate root grants (namespace '_Access') are NOT touched — same reasoning
            // as V49: activating/deactivating those is an operator decision, not an upgrade
            // side effect.
            int affected;
            await using (var fix = ctx.DataSource.CreateCommand($"""
                UPDATE {quotedSchema}.access
                SET main_node = 'Admin'
                WHERE namespace = 'Admin/_Access'
                  AND (main_node IS NULL OR main_node = '')
                """))
            {
                affected = await fix.ExecuteNonQueryAsync();
            }

            if (affected == 0)
                continue;

            totalFixed += affected;
            ctx.Logger.LogInformation(
                "Repair v50: '{Schema}' — {Count} platform-admin grant(s) rescoped from root to 'Admin'",
                schema, affected);

            // The permission rows were materialized from the root prefix — re-project them.
            // Guarded like V49: a schema without the rebuild function still gets its data repaired.
            try
            {
                await using var rebuild = ctx.DataSource.CreateCommand(
                    $"SELECT {quotedSchema}.rebuild_user_effective_permissions()");
                await rebuild.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                ctx.Logger.LogWarning(ex,
                    "Repair v50: '{Schema}' — permission rebuild failed; rows are repaired and the next "
                    + "access write (or the boot-time self-heal) re-projects them", schema);
            }
        }

        ctx.Logger.LogInformation(
            "Repair v50: done — {Count} platform-admin grant(s) rescoped across {Schemas} schema(s)",
            totalFixed, schemas.Count);
    }
}
