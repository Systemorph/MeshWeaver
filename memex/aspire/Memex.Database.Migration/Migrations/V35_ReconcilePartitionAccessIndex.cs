using MeshWeaver.Hosting.PostgreSql;
using Microsoft.Extensions.Logging;

namespace Memex.Database.Migration.Migrations;

/// <summary>
/// Heals the denormalized <c>public.partition_access</c> index on existing databases where it
/// drifted out of sync with <c>{schema}.user_effective_permissions</c>.
///
/// <para><b>The bug.</b> <c>public.search_across_schemas</c> gates every partition behind
/// <c>EXISTS(public.partition_access[user_id, partition]) AND (public_read OR uep[Read].is_allow)</c>.
/// <c>public.partition_access</c> is a flat denormalized index maintained ONLY by the
/// permission-rebuild functions in each partition schema:
/// <list type="bullet">
///   <item><c>rebuild_user_effective_permissions()</c> — schema-level, full rebuild; syncs every
///     user with Read into <c>partition_access</c>.</item>
///   <item><c>rebuild_user_permissions_for(p_user_id)</c> — per-user, fired by the
///     <c>access_changed</c> trigger; syncs that one user's <c>partition_access</c> row.</item>
/// </list>
/// In production a user ended up with <c>user_effective_permissions[Read] = true</c> for a
/// partition but NO <c>public.partition_access</c> row → their Space was invisible in the catalog /
/// cross-schema search with no error. Two causes: (a) a STALE per-user function on a schema
/// provisioned BEFORE the <c>partition_access</c> sync was added to the function body — a
/// <c>CREATE OR REPLACE FUNCTION</c> change never re-applied to existing schemas, so the old body
/// rebuilt <c>user_effective_permissions</c> but skipped the <c>partition_access</c> sync; and
/// (b) data inserted bypassing the <c>access_changed</c> trigger.</para>
///
/// <para><b>The repair (idempotent).</b> For every existing partition schema (those with an
/// <c>access</c> table):
/// <list type="number">
///   <item>Re-apply the CURRENT per-partition DDL via <c>public.ensure_partition_schema(schema)</c>
///     — the single source of truth that <c>CREATE OR REPLACE</c>s the rebuild functions
///     (including the <c>partition_access</c> sync inside <c>rebuild_user_permissions_for</c>),
///     curing any stale function body.</item>
///   <item>Run <c>{schema}.rebuild_user_effective_permissions()</c> — the full schema-level
///     reconcile that rebuilds <c>user_effective_permissions</c> from the <c>access</c> satellite
///     AND re-syncs <c>public.partition_access</c> (upserts every user with Read, deletes the
///     revoked), healing the drift regardless of which cause produced it.</item>
/// </list>
/// The proc itself is re-installed first (idempotent <c>CREATE OR REPLACE</c>) so a DB that
/// fast-forwarded past V30 still gets the latest body.</para>
///
/// <para>Skipped on fresh DBs (no legacy partitions to reconcile; schema-init already installs
/// the corrected functions and an empty <c>partition_access</c> needs no heal).</para>
/// </summary>
public sealed class V35_ReconcilePartitionAccessIndex : IMigration
{
    public int Version => 35;

    public string Description =>
        "Re-apply per-partition permission functions and reconcile public.partition_access for every schema";

    public Task RunAsync(MigrationContext ctx)
        // Single implementation shared with the ALWAYS-RUNS phase in Program.cs — the drift
        // this migration healed once RECURRED on databases already past v35, so the reconcile
        // now also runs on every migration pass; see PartitionAccessReconcile.
        => PartitionAccessReconcile.RunAsync(
            ctx.DataSource, ctx.Options.VectorDimensions, ctx.Logger, phase: "v35");
}
