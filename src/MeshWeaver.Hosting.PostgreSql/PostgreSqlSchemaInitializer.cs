using MeshWeaver.Mesh;
using Npgsql;

namespace MeshWeaver.Hosting.PostgreSql;

/// <summary>
/// Creates the PostgreSQL schema (tables, indexes, triggers) if not already present.
/// </summary>
public static class PostgreSqlSchemaInitializer
{
    /// <summary>
    /// Creates the partition_access table in the public schema.
    /// This table maps users to partition schemas they can access, populated by
    /// rebuild_user_effective_permissions() trigger in each partition schema.
    /// </summary>
    public static async Task InitializePartitionAccessTableAsync(NpgsqlDataSource dataSource, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand("""
            CREATE TABLE IF NOT EXISTS public.partition_access (
                user_id    TEXT NOT NULL,
                partition  TEXT NOT NULL,
                PRIMARY KEY (user_id, partition)
            );
            CREATE INDEX IF NOT EXISTS idx_partition_access_user ON public.partition_access (user_id);

            -- Searchable schemas: content partitions that should be included in global search.
            -- Managed by the app at startup; excludes admin, portal, kernel, and rogue schemas.
            CREATE TABLE IF NOT EXISTS public.searchable_schemas (
                schema_name TEXT PRIMARY KEY
            );

            -- Stored procedure: search across all searchable schemas in one query.
            -- Dynamically builds UNION ALL across schemas, applies per-schema access control.
            -- Returns mesh_nodes matching the WHERE clause from all content partitions.
            CREATE OR REPLACE FUNCTION public.search_across_schemas(
                p_where_clause TEXT DEFAULT '',
                p_user_id TEXT DEFAULT NULL,
                p_order_by TEXT DEFAULT 'n.last_modified DESC',
                p_limit INT DEFAULT 50
            ) RETURNS SETOF RECORD AS $$
            DECLARE
                schema_rec RECORD;
                union_sql TEXT := '';
                full_sql TEXT;
                user_list TEXT;
            BEGIN
                -- Build user list for access control
                IF p_user_id IS NOT NULL AND p_user_id != 'Anonymous' THEN
                    user_list := quote_literal(p_user_id) || ', ''Public''';
                ELSIF p_user_id IS NOT NULL THEN
                    user_list := quote_literal(p_user_id);
                END IF;

                -- Build UNION ALL across all searchable schemas
                FOR schema_rec IN SELECT schema_name FROM public.searchable_schemas ORDER BY schema_name
                LOOP
                    IF union_sql != '' THEN
                        union_sql := union_sql || ' UNION ALL ';
                    END IF;

                    union_sql := union_sql || format(
                        'SELECT n.id, n.namespace, n.name, n.node_type, n.category, n.icon, '
                        || 'n.display_order, n.last_modified, n.version, n.state, n.content, '
                        || 'n.desired_id, n.main_node '
                        || 'FROM %I.mesh_nodes n WHERE n.main_node = n.path',
                        schema_rec.schema_name);

                    -- Add access control per schema — ONLY for schemas that actually
                    -- carry the per-partition permission tables. Public content schemas
                    -- (e.g. `doc`, the mirrored documentation) ship `mesh_nodes` WITHOUT
                    -- `user_effective_permissions` / `node_type_permissions`; referencing
                    -- those missing relations made the ENTIRE union fail to plan (42P01)
                    -- for every authenticated user → empty search / empty catalog. Such
                    -- schemas are PUBLIC content (no per-user filter); access-controlled
                    -- partitions still get the full partition_access + node check.
                    IF user_list IS NOT NULL
                       AND to_regclass(format('%I.user_effective_permissions', schema_rec.schema_name)) IS NOT NULL THEN
                        union_sql := union_sql || format(
                            ' AND ('
                            || 'EXISTS (SELECT 1 FROM public.partition_access pa WHERE pa.user_id IN (%s) AND pa.partition = %L)'
                            || ' AND ('
                            || 'EXISTS (SELECT 1 FROM %I.node_type_permissions ntp WHERE ntp.node_type = n.node_type AND ntp.public_read = true)'
                            || ' OR (SELECT uep.is_allow FROM %I.user_effective_permissions uep'
                            || '  WHERE uep.user_id IN (%s) AND uep.permission = ''Read'''
                            || '  AND n.main_node LIKE uep.node_path_prefix || ''%%'''
                            || '  ORDER BY LENGTH(uep.node_path_prefix) DESC LIMIT 1) = true'
                            || '))',
                            user_list, schema_rec.schema_name,
                            schema_rec.schema_name,
                            schema_rec.schema_name,
                            user_list);
                    END IF;

                    -- Add custom WHERE clause
                    IF p_where_clause IS NOT NULL AND p_where_clause != '' THEN
                        union_sql := union_sql || ' AND ' || p_where_clause;
                    END IF;
                END LOOP;

                IF union_sql = '' THEN
                    RETURN;
                END IF;

                full_sql := 'SELECT * FROM (' || union_sql || ') combined';
                IF p_order_by IS NOT NULL AND p_order_by != '' THEN
                    -- Strip table alias prefix (n.) since outer SELECT uses bare column names
                    full_sql := full_sql || ' ORDER BY ' || REPLACE(p_order_by, 'n.', '');
                END IF;
                full_sql := full_sql || ' LIMIT ' || p_limit;

                RETURN QUERY EXECUTE full_sql;
            END;
            $$ LANGUAGE plpgsql;

            -- #16 Central top-level index — a MATERIALIZED VIEW (fast lookup) over every
            -- partition's top-level node (namespace='' → one row per partition root: the
            -- Space / User node whose id IS the partition name). Powers top-level
            -- autocomplete + the root listing from ONE small indexed relation instead of a
            -- cross-schema fan-out over full mesh_nodes (the prod connection-pool storm).
            -- Re-materialized from public.searchable_schemas whenever the partition set
            -- changes (see rebuild on SyncSearchableSchemas); the data is tiny (one row per
            -- partition) so a full DROP+CREATE re-materialize is cheap. The column list is
            -- FIXED so the matview shape stays stable across rebuilds.
            CREATE OR REPLACE FUNCTION public.rebuild_top_level_index() RETURNS void AS $$
            DECLARE
                schema_rec RECORD;
                union_sql  TEXT := '';
                -- 🚨 embedding is DELIBERATELY excluded. The matview is a top-level-node lookup
                -- (autocomplete by name/id/path — PostgreSqlCrossSchemaQueryProvider; it never reads
                -- the vector). Including it made the matview DEPEND on every partition's embedding
                -- column, so changing the embedding model/dimension (ALTER COLUMN embedding TYPE
                -- vector(N)) failed with "cannot alter type of a column used by a view or rule", and a
                -- cross-partition UNION of differing vector dims can't even rebuild. Dropping it makes
                -- embedding-dimension changes free. See memory embedding-column-resize-matview-block.
                cols       TEXT := 'id, namespace, name, node_type, description, category, icon, '
                                || 'display_order, last_modified, version, state, content, '
                                || 'desired_id, main_node, path';
            BEGIN
                FOR schema_rec IN SELECT schema_name FROM public.searchable_schemas ORDER BY schema_name
                LOOP
                    -- Skip a listed schema whose mesh_nodes table no longer exists. searchable_schemas
                    -- can LAG a concurrent partition drop (DeletePartition does DROP SCHEMA … CASCADE):
                    -- under load a just-dropped partition is still listed here, and referencing its
                    -- gone table in the UNION fails the CREATE MATERIALIZED VIEW with
                    -- `42P01 relation "<schema>.mesh_nodes" does not exist` — the flaky
                    -- RebuildTopLevelIndex / SpaceDeletion class of failures. to_regclass returns NULL
                    -- (no error) for an absent relation, so this is the race-safe existence guard.
                    CONTINUE WHEN to_regclass(format('%I.mesh_nodes', schema_rec.schema_name)) IS NULL;
                    IF union_sql != '' THEN union_sql := union_sql || ' UNION ALL '; END IF;
                    -- Exactly the PARTITION ROOT: the namespace='' node whose id matches the
                    -- schema (partition) name CASE-INSENSITIVELY. The schema is the lowercased
                    -- first path segment, but a partition root's id keeps its ORIGINAL case —
                    -- e.g. a Space "Agentic Pension" lives in schema `agenticpension` with root
                    -- id `AgenticPension`. The old `id = <schema_name>` filter matched only when
                    -- root id == lowercased schema (true for usernames, FALSE for PascalCase
                    -- space names) → those spaces silently vanished from the top-level listing.
                    -- LOWER(id) = <schema_name> pins exactly one root per partition (path = the
                    -- root id = globally unique) for any id casing. `path` is a GENERATED column
                    -- = id when namespace='' — a plain `WHERE namespace=''` would instead pull
                    -- every top-level node, colliding paths across partitions → UNIQUE(path) fails.
                    union_sql := union_sql || format(
                        'SELECT %s FROM %I.mesh_nodes WHERE namespace = '''' AND LOWER(id) = %L',
                        cols, schema_rec.schema_name, schema_rec.schema_name);
                END LOOP;

                IF union_sql = '' THEN
                    -- No partitions registered yet — emit a correctly-typed empty view
                    -- (public.mesh_nodes carries the identical column set + vector dim).
                    union_sql := format('SELECT %s FROM public.mesh_nodes WHERE false', cols);
                END IF;

                -- Drop + re-materialize. Always a MATERIALIZED VIEW (this function is its
                -- only creator), so DROP MATERIALIZED VIEW IF EXISTS is the right form —
                -- a plain `DROP VIEW IF EXISTS` would raise 42809 "is not a view" on the
                -- existing matview (IF EXISTS suppresses only "does not exist", not wrong
                -- relkind). Indexes are recreated each rebuild (they vanish with the matview).
                -- The UNIQUE key is `path`, NOT (namespace,id): id is unique only WITHIN a
                -- partition, while path embeds the partition prefix and is globally unique —
                -- so it's the only collision-free key across the UNION (and enables
                -- REFRESH ... CONCURRENTLY).
                EXECUTE 'DROP MATERIALIZED VIEW IF EXISTS public.top_level_index';
                EXECUTE 'CREATE MATERIALIZED VIEW public.top_level_index AS ' || union_sql;
                EXECUTE 'CREATE UNIQUE INDEX idx_tli_path ON public.top_level_index (path)';
                EXECUTE 'CREATE INDEX idx_tli_name_lower ON public.top_level_index (LOWER(name))';
                EXECUTE 'CREATE INDEX idx_tli_id_lower ON public.top_level_index (LOWER(id))';
                EXECUTE 'CREATE INDEX idx_tli_node_type ON public.top_level_index (node_type)';
            END;
            $$ LANGUAGE plpgsql;

            SELECT public.rebuild_top_level_index();
            """);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Ensures the vector extension exists and initializes the full schema.
    /// Creates the extension as plain SQL first, then reloads types so the
    /// UseVector() plugin can resolve the vector OID for parameterized queries.
    /// </summary>
    public static async Task InitializeAsync(NpgsqlDataSource dataSource, PostgreSqlStorageOptions options, CancellationToken ct = default)
    {
        // Step 1: Create the vector extension using plain SQL (no vector parameters).
        // Even if UseVector() can't find the type yet, plain SQL commands work fine.
        await using (var cmd = dataSource.CreateCommand("CREATE EXTENSION IF NOT EXISTS vector"))
        {
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // Step 2: Reload the type catalog so UseVector() picks up the new vector type.
        await using (var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false))
        {
            await conn.ReloadTypesAsync().ConfigureAwait(false);
        }

        // Step 2.5: Create the access-object → auth mirror trigger FUNCTION before any
        // partition DDL runs. The per-partition DDL (GetVersionedPartitionDdl) installs the
        // `mesh_node_mirror_access_objects` trigger ONLY IF this function already exists
        // (guarded `EXISTS (SELECT 1 FROM pg_proc …)`). Historically the function was created
        // only by the V27 *repair* migration — which MigrationRunner SKIPS on fresh DBs — so
        // fresh deployments never installed the trigger and `auth` stayed empty. Creating it
        // here (always-run path) makes the guard pass on every DB, fresh or not.
        await using (var cmd = dataSource.CreateCommand(GetAuthMirrorFunctionScript()))
        {
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // Step 3: Run the full schema script (tables, indexes, triggers).
        await using (var cmd = dataSource.CreateCommand(GetSchemaScript(options)))
        {
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // Step 4: (Re)create the public.ensure_partition_schema(text) stored proc —
        // the single source of truth for per-partition provisioning. CREATE OR REPLACE
        // keeps it idempotent and in sync on every init (runtime bootstrap + the test
        // fixture both run InitializeAsync against public, so both DBs get the proc).
        // Routed to by PostgreSqlPartitionStorageProvider.EnsureSchemaAsync.
        await using (var cmd = dataSource.CreateCommand(GetEnsurePartitionSchemaProcScript(options.VectorDimensions)))
        {
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // Step 5: SELF-HEAL the auth mirror. Step 2.5 heals the trigger FUNCTION on every init;
        // this heals the TRIGGERS and the DATA, so the mirror converges on every boot instead of
        // depending on a one-time migration: (a) any partition schema missing the
        // mesh_node_mirror_access_objects trigger (provisioned during a bad window) gets it,
        // (b) mirrored rows that were missed or went stale while the function/trigger was wrong
        // are reconciled into auth.mesh_nodes. One server-side pass, idempotent, fail-safe (skips
        // when auth isn't provisioned yet — e.g. a bare test fixture). This is the durable answer
        // to the 2026-07 "spaces invisible in the catalog" incident: even if the mirror breaks
        // again, the next restart repairs both trigger topology and data.
        //
        // 🚨 PUBLIC-INIT ONLY + advisory-locked. The heal sweeps ALL partition schemas (trigger
        // probes + reconcile upserts). Running that from EVERY per-schema data-source init — as
        // the first cut did — multiplies the sweep by the number of concurrently-initializing
        // schemas (parallel test hosts!) and takes cross-schema locks while other inits hold
        // their per-schema advisory locks: intermittent lock contention that surfaced as
        // NodeHubContentCollectionTest timeouts + a test-process abort on CI shard 4. The boot
        // path initializes the public schema exactly once, so gating on it keeps the heal a
        // per-boot singleton; the in-script pg_advisory_xact_lock serializes across silos.
        if (string.Equals(options.Schema, "public", StringComparison.OrdinalIgnoreCase))
        {
            await using var cmd = dataSource.CreateCommand(GetAuthMirrorSelfHealScript());
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Body of the per-partition <c>trg_access_changed()</c> trigger function — the glue between
    /// a write to the <c>access</c> satellite table and the (re)materialization of
    /// <c>user_effective_permissions</c>. Single-sourced here because THREE scripts install it:
    /// <see cref="GetMeshSchemaScript"/>, <see cref="GetVersionedPartitionDdl"/> (and through it
    /// the <c>public.ensure_partition_schema</c> proc), and the per-boot
    /// <see cref="GetAuthMirrorSelfHealScript"/>, which CREATE OR REPLACEs stale copies on every
    /// existing partition.
    ///
    /// <para>🚨 <b>Every rebuild call MUST be schema-qualified via <c>TG_TABLE_SCHEMA</c>.</b>
    /// Inside plpgsql an unqualified <c>PERFORM rebuild_user_permissions_for(…)</c> resolves
    /// through the CALLING SESSION's <c>search_path</c> — and production writes flow through the
    /// SHARED base <see cref="NpgsqlDataSource"/> (default <c>search_path</c> = <c>public</c>)
    /// with schema-qualified statements
    /// (<see cref="PostgreSqlPartitionStorageProvider.CreateAdapterForTable"/>). The unqualified
    /// call therefore silently executed PUBLIC's rebuild functions against public's empty
    /// <c>access</c> table: the partition's <c>user_effective_permissions</c> stayed EMPTY after
    /// every grant write, so partition-scoped queries failed closed (count 0) for every user
    /// until the next boot's self-heal re-materialized it — while direct reads kept working.
    /// Live incident: memex 2026-07-13, freshly recreated partitions
    /// (AgenticEngineering/DataModeling/RiskTransfer) with intact <c>_Access</c> grants and an
    /// empty permissions table; an older partition (chess) had rows only because the previous
    /// boot's self-heal had materialized them. <c>TG_TABLE_SCHEMA</c> — the schema of the
    /// <c>access</c> table that fired the trigger — IS the partition schema, and makes this body
    /// schema-agnostic (no sentinel splicing), so every install path ships one identical body.</para>
    /// </summary>
    internal const string AccessChangedTriggerFunctionBody = """
        DECLARE
            new_subject TEXT;
            old_subject TEXT;
            subject_is_group BOOLEAN;
        BEGIN
            IF TG_OP <> 'INSERT' THEN
                old_subject := OLD.content->>'accessObject';
            END IF;
            IF TG_OP <> 'DELETE' THEN
                new_subject := NEW.content->>'accessObject';
            END IF;

            -- A grant whose subject is a GROUP applies to every (leaf) MEMBER of that group.
            -- rebuild_user_permissions_for(subject) does NOT compute that — it would rebuild the
            -- group id as if it were a user — so a group grant written before OR after its members
            -- exist would leave them stale. Detect a group subject and fall back to a full rebuild,
            -- so a licensed group materializes its members regardless of add-vs-grant order.
            -- (Membership CHANGES are the mirror case, handled by the auth-mirror
            -- zzz_group_recompute_* triggers — see GroupChangedTriggerFunctionBody.)
            --
            -- Every call is schema-qualified via TG_TABLE_SCHEMA (= the partition schema whose
            -- `access` table fired the trigger): an unqualified PERFORM resolves through the WRITING
            -- SESSION's search_path, which on the shared base connection pool is public — silently
            -- rebuilding the WRONG schema's permissions and leaving this partition's
            -- user_effective_permissions empty (memex prod incident 2026-07-13).
            -- Groups mirror to the GLOBAL auth schema, so a grant to a group DEFINED IN ANOTHER
            -- PARTITION is still detected here (cross-partition licensing) — read auth, not the
            -- local schema. Default false so an (impossible) missing auth schema falls back to the
            -- per-user path rather than erroring the originating grant write.
            subject_is_group := false;
            IF to_regclass('"auth".mesh_nodes') IS NOT NULL THEN
                EXECUTE
                    'SELECT EXISTS (SELECT 1 FROM "auth".mesh_nodes WHERE node_type = ''Group'''
                    || ' AND CASE WHEN namespace = '''' THEN id ELSE namespace || ''/'' || id END'
                    || ' IN ($1, $2))'
                    INTO subject_is_group USING new_subject, old_subject;
            END IF;

            IF subject_is_group OR (new_subject IS NULL AND old_subject IS NULL) THEN
                EXECUTE format('SELECT %I.rebuild_user_effective_permissions()', TG_TABLE_SCHEMA);
            ELSE
                IF new_subject IS NOT NULL THEN
                    EXECUTE format('SELECT %I.rebuild_user_permissions_for($1)', TG_TABLE_SCHEMA)
                        USING new_subject;
                END IF;
                IF old_subject IS NOT NULL AND old_subject IS DISTINCT FROM new_subject THEN
                    EXECUTE format('SELECT %I.rebuild_user_permissions_for($1)', TG_TABLE_SCHEMA)
                        USING old_subject;
                END IF;
            END IF;

            RETURN NULL;
        END;
        """;

    /// <summary>
    /// Body of the <c>trg_group_changed()</c> trigger function — recomputes
    /// <c>user_effective_permissions</c> whenever a <c>Group</c> or <c>GroupMembership</c> node
    /// changes. Group memberships feed the group-expansion INSIDE the rebuild, but — unlike an
    /// <c>AccessAssignment</c> — they live in <c>mesh_nodes</c>, not the <c>access</c> satellite
    /// that <see cref="AccessChangedTriggerFunctionBody"/> watches. So without this trigger,
    /// adding or removing a member (or renaming/deleting a group) left every affected member's
    /// permissions STALE until an unrelated grant write or the next boot's self-heal
    /// re-materialized them: a group licensed on a course granted its new members nothing until
    /// something else touched the projection. Installed ONCE, on the global auth mirror — the two
    /// <c>zzz_group_recompute_*</c> triggers on <c>auth.mesh_nodes</c> that <see cref="GetAuthMirrorSelfHealScript"/>
    /// creates (this body becomes <c>public.trg_group_changed()</c>).
    ///
    /// <para><b>Cross-partition fan-out.</b> Group access is resolved GLOBALLY: memberships mirror
    /// into <c>auth.mesh_nodes</c> and every schema's rebuild reads them from there, so a group can
    /// be defined in one partition and licensed on a course in another. A membership change in ANY
    /// partition therefore mirrors into <c>auth</c> and can change effective permissions in ANY
    /// schema that grants the affected group(s) — so this body loops the partition schemas and
    /// full-rebuilds every one that has at least one GROUP grant (a superset of the truly-affected
    /// schemas: correct, and bounded to the partitions that actually license a group). A full
    /// rebuild per schema (not per-user) is the right call — a membership can list several groups,
    /// its member can itself be a group (nested groups expand recursively to leaf users), and a group
    /// can be renamed/deleted, so the affected end-users are not knowable from the single changed
    /// row. Group changes are infrequent admin operations, and the triggers' <c>WHEN</c> clause keeps
    /// this off every non-group write.</para>
    ///
    /// <para><b>Why on auth, not per partition.</b> The membership converges in <c>auth</c> (the
    /// global mirror), so ONE trigger there covers every partition — no per-partition copies to
    /// double-fire, and the changed row is already in <c>auth</c> when the trigger fires (the mirror
    /// wrote it), so no firing-order hack is needed. Every rebuild call is schema-qualified
    /// (<c>%I</c>) so it materializes the right schema.</para>
    /// </summary>
    internal const string GroupChangedTriggerFunctionBody = """
        DECLARE
            sch text;
            has_group_grant boolean;
        BEGIN
            -- A Group/GroupMembership changed. Because group access is resolved GLOBALLY (auth-
            -- mirrored memberships; each schema's rebuild reads them), the affected users may live
            -- in — and be granted in — ANY partition. Rebuild every schema that grants a group.
            FOR sch IN
                SELECT n.nspname
                FROM pg_namespace n
                WHERE n.nspname NOT IN ('information_schema','pg_catalog','pg_toast','auth')
                  AND n.nspname NOT LIKE 'pg\_%'
                  AND n.nspname NOT LIKE '%\_versions'
                  AND to_regclass(format('%I.access', n.nspname)) IS NOT NULL
                  AND to_regclass(format('%I.user_effective_permissions', n.nspname)) IS NOT NULL
            LOOP
                -- Skip schemas with no group grant — nothing there depends on group membership.
                EXECUTE format(
                    'SELECT EXISTS (SELECT 1 FROM %I.access a'
                    || ' JOIN "auth".mesh_nodes g ON g.node_type = ''Group'''
                    || ' AND g.namespace || ''/'' || g.id = a.content->>''accessObject'')', sch)
                    INTO has_group_grant;
                IF has_group_grant THEN
                    EXECUTE format('SELECT %I.rebuild_user_effective_permissions()', sch);
                END IF;
            END LOOP;
            RETURN NULL;
        END;
        """;

    /// <summary>
    /// One-shot server-side reconciliation of the auth mirror AND the permission projection:
    /// per partition schema it (a) installs the <c>mesh_node_mirror_access_objects</c> trigger
    /// where missing, (a2) CREATE OR REPLACEs the schema's <c>trg_access_changed()</c> with the
    /// current <see cref="AccessChangedTriggerFunctionBody"/> (and (a3) re-installs the
    /// <c>access_changed</c> trigger where missing) so grant writes materialize
    /// <c>user_effective_permissions</c> into the RIGHT schema — partitions deployed with the
    /// pre-2026-07-13 body resolved the rebuild through the writing session's
    /// <c>search_path</c> and silently rebuilt <c>public</c> instead; it also installs (once, before
    /// the per-schema loop) the global <c>public.trg_group_changed()</c> + the two
    /// <c>zzz_group_recompute_*</c> triggers on <c>auth.mesh_nodes</c>
    /// (<see cref="GroupChangedTriggerFunctionBody"/>) so a Group/GroupMembership change — which
    /// mirrors into <c>auth</c> — recomputes every schema that grants the affected group
    /// (cross-partition group licensing), (b) upserts every mirrored
    /// node type (<c>User/Group/Role/VUser/ApiToken/Space/GroupMembership</c>) into <c>auth.mesh_nodes</c>, and
    /// (c) re-runs the schema's <c>rebuild_user_effective_permissions()</c> so <c>_Access</c>
    /// grants and <c>_Policy</c> rows are projected into <c>user_effective_permissions</c> +
    /// <c>public.partition_access</c> — the tables every partition-scoped query and the
    /// cross-partition fan-out filter on. (c) is both the durable fix for the 2026-07
    /// empty-projection incidents AND the data backfill for (a2): partitions whose permission
    /// tables stayed empty under the broken trigger converge on the next boot. No matview
    /// rebuild here — the schema script (<see cref="InitializeAsync"/> step 3) already rebuilds
    /// <c>public.top_level_index</c> on the same init, and a second DROP+CREATE under
    /// ACCESS EXCLUSIVE just adds lock contention. No-op when <c>auth.mesh_nodes</c> doesn't
    /// exist. Runs once per boot (public-schema init only, serialized across silos by an advisory
    /// xact lock) — everything converges on restart rather than relying on one-time migrations.
    /// </summary>
    public static string GetAuthMirrorSelfHealScript() => $$"""
        DO $auth_mirror_heal$
        DECLARE
            s text;
            -- The CURRENT trg_access_changed body (single-sourced from the C# constant the
            -- partition DDL also embeds). __mw_schema__ is replace()d per schema below;
            -- plain replace, NOT format() — the body carries its own %I/%L format specs.
            access_trg_fn text := $acfix$
        CREATE OR REPLACE FUNCTION __mw_schema__.trg_access_changed() RETURNS TRIGGER AS $trg_access$
        {{AccessChangedTriggerFunctionBody}}
        $trg_access$ LANGUAGE plpgsql
        $acfix$;
        BEGIN
            IF to_regclass('"auth".mesh_nodes') IS NULL THEN
                RETURN;
            END IF;

            -- Cross-partition group recompute lives HERE, on the global auth mirror — the single
            -- convergence point for memberships. A Group/GroupMembership change in ANY partition
            -- mirrors into auth.mesh_nodes; this trigger then recomputes every schema that grants
            -- the affected group(s) (trg_group_changed fans out over the partition schemas). One
            -- trigger, not one per partition: no double-fire, and the row is already in auth when
            -- it fires (the mirror wrote it), so no firing-order hack is needed. Idempotent.
            CREATE OR REPLACE FUNCTION public.trg_group_changed() RETURNS TRIGGER AS $trg_group$
        {{GroupChangedTriggerFunctionBody}}
            $trg_group$ LANGUAGE plpgsql;
            DROP TRIGGER IF EXISTS zzz_group_recompute_ins ON "auth".mesh_nodes;
            CREATE TRIGGER zzz_group_recompute_ins
                AFTER INSERT OR UPDATE ON "auth".mesh_nodes
                FOR EACH ROW WHEN (NEW.node_type IN ('GroupMembership','Group'))
                EXECUTE FUNCTION public.trg_group_changed();
            DROP TRIGGER IF EXISTS zzz_group_recompute_del ON "auth".mesh_nodes;
            CREATE TRIGGER zzz_group_recompute_del
                AFTER DELETE ON "auth".mesh_nodes
                FOR EACH ROW WHEN (OLD.node_type IN ('GroupMembership','Group'))
                EXECUTE FUNCTION public.trg_group_changed();

            -- One heal at a time across the whole mesh (HA silos boot concurrently); the lock
            -- releases with this transaction.
            PERFORM pg_advisory_xact_lock(hashtext('auth_mirror_self_heal'));

            FOR s IN
                SELECT t.table_schema
                FROM information_schema.tables t
                WHERE t.table_name = 'mesh_nodes'
                  AND t.table_schema NOT IN
                      ('information_schema','pg_catalog','pg_toast','public','admin','auth')
                  AND t.table_schema NOT LIKE '%\_versions'
            LOOP
                -- (a) Trigger present on every partition table (auth itself excluded above —
                --     it is the mirror target, mirroring into itself would loop).
                IF NOT EXISTS (
                    SELECT 1 FROM pg_trigger tg
                    JOIN pg_class c ON c.oid = tg.tgrelid
                    JOIN pg_namespace n ON n.oid = c.relnamespace
                    WHERE tg.tgname = 'mesh_node_mirror_access_objects'
                      AND c.relname = 'mesh_nodes' AND n.nspname = s)
                THEN
                    EXECUTE format(
                        'CREATE TRIGGER mesh_node_mirror_access_objects '
                        || 'AFTER INSERT OR UPDATE OR DELETE ON %I.mesh_nodes '
                        || 'FOR EACH ROW EXECUTE FUNCTION public.mirror_access_object_to_auth_schema()', s);
                END IF;

                -- (a2) Heal the access→permissions trigger FUNCTION. Partitions deployed with
                --     the pre-2026-07-13 body called the rebuild functions UNQUALIFIED, so a
                --     grant written through the shared base pool (search_path = public)
                --     silently rebuilt public's permissions and left this partition's
                --     user_effective_permissions EMPTY — every partition-scoped query failed
                --     closed for every user (memex 2026-07-13: AgenticEngineering/
                --     DataModeling/RiskTransfer). CREATE OR REPLACE keeps the function OID, so
                --     the existing trigger picks the fixed body up immediately. Guarded on the
                --     access table + rebuild function existing (very old schemas may predate them).
                IF to_regclass(format('%I.access', s)) IS NOT NULL
                   AND EXISTS (
                       SELECT 1 FROM pg_proc p
                       JOIN pg_namespace pn ON pn.oid = p.pronamespace
                       WHERE p.proname = 'rebuild_user_permissions_for' AND pn.nspname = s)
                THEN
                    EXECUTE replace(access_trg_fn, '__mw_schema__', quote_ident(s));

                    -- (a3) …and the TRIGGER itself, for partitions provisioned in a window
                    --     where it was missing.
                    IF NOT EXISTS (
                        SELECT 1 FROM pg_trigger tg
                        JOIN pg_class c ON c.oid = tg.tgrelid
                        JOIN pg_namespace n ON n.oid = c.relnamespace
                        WHERE tg.tgname = 'access_changed'
                          AND c.relname = 'access' AND n.nspname = s)
                    THEN
                        EXECUTE format(
                            'CREATE TRIGGER access_changed '
                            || 'AFTER INSERT OR UPDATE OR DELETE ON %I.access '
                            || 'FOR EACH ROW EXECUTE FUNCTION %I.trg_access_changed()', s, s);
                    END IF;
                END IF;

                -- (b) Reconcile mirrored rows: insert what is missing, refresh what is stale.
                --     The WHERE guard keeps the pass write-free when everything already matches.
                EXECUTE format($reconcile$
                    INSERT INTO "auth".mesh_nodes
                        (namespace, id, name, node_type, category, icon, display_order,
                         last_modified, version, state, content, desired_id, main_node)
                    SELECT namespace, id, name, node_type, category, icon, display_order,
                           last_modified, version, state, content, desired_id, main_node
                      FROM %I.mesh_nodes
                     WHERE node_type IN ('User','Group','Role','VUser','ApiToken','Space','GroupMembership')
                    ON CONFLICT (namespace, id) DO UPDATE SET
                        name = EXCLUDED.name,
                        node_type = EXCLUDED.node_type,
                        category = EXCLUDED.category,
                        icon = EXCLUDED.icon,
                        display_order = EXCLUDED.display_order,
                        last_modified = EXCLUDED.last_modified,
                        version = EXCLUDED.version,
                        state = EXCLUDED.state,
                        content = EXCLUDED.content,
                        desired_id = EXCLUDED.desired_id,
                        main_node = EXCLUDED.main_node
                    WHERE "auth".mesh_nodes.version < EXCLUDED.version
                       OR "auth".mesh_nodes.node_type IS DISTINCT FROM EXCLUDED.node_type
                       OR "auth".mesh_nodes.last_modified < EXCLUDED.last_modified
                $reconcile$, s);

                -- (c) Re-project the partition's _Access grants + _Policy into
                --     user_effective_permissions + public.partition_access. Partitions
                --     provisioned while the projection machinery was broken (memex,
                --     ~2026-07-06..11 partition_access; ~2026-07-13 the (a2) wrong-schema
                --     trigger) had EMPTY projections — partition-scoped queries and the
                --     cross-partition fan-out dropped every row, so their Spaces were
                --     invisible and their content unreadable even though grants and content
                --     were intact. This is also the DATA BACKFILL for (a2): tables left empty
                --     by the broken trigger converge here on the next boot.
                --     Guarded: very old schemas may predate the rebuild function.
                BEGIN
                    EXECUTE format('SELECT %I.rebuild_user_effective_permissions()', s);
                EXCEPTION WHEN undefined_function THEN
                    NULL;
                END;
            END LOOP;
            -- No matview rebuild here: the schema script (init step 3) rebuilds
            -- public.top_level_index on the same boot; doubling the DROP+CREATE
            -- (ACCESS EXCLUSIVE) only adds lock contention.
        END
        $auth_mirror_heal$;
        """;

    /// <summary>
    /// Builds the <c>CREATE OR REPLACE FUNCTION public.ensure_partition_schema(partition_name text)</c>
    /// DDL. The proc idempotently creates the partition's schema + the full versioned
    /// table set (<c>{partition}.mesh_nodes</c> + every satellite table from
    /// <see cref="SatelliteTableMapping"/>) + the permission-rebuild
    /// functions and notify/mirror/history triggers.
    ///
    /// <para><b>Byte-faithful to the C# DDL.</b> The proc body embeds the exact strings
    /// produced by <see cref="GetVersionedPartitionDdl"/> and
    /// <see cref="GetSatelliteTableScript"/> — the same definitions
    /// <see cref="InitializeAsync"/> / <see cref="CreateSatelliteTablesAsync"/> run for the
    /// public schema and per-schema data sources. Same columns, types, PKs, indexes,
    /// triggers, satellite tables. The only runtime difference is the partition name,
    /// substituted from <see cref="PartitionNameSentinel"/> via <c>replace()</c>.</para>
    ///
    /// <para><b>Idempotent + safe.</b> <c>CREATE SCHEMA IF NOT EXISTS</c> + every inner
    /// statement is <c>CREATE TABLE/INDEX/TRIGGER … IF NOT EXISTS</c> (or
    /// <c>CREATE OR REPLACE FUNCTION</c> / guarded <c>DO</c> blocks). Schema + table names
    /// are interpolated through <c>format(%I)</c> so the identifier is always correctly
    /// quoted. Setting <c>search_path</c> to the target schema makes the unqualified DDL
    /// land in the partition exactly as the per-schema NpgsqlDataSource does at runtime.</para>
    /// </summary>
    public static string GetEnsurePartitionSchemaProcScript(int dim)
    {
        // Versioned-partition DDL with the schema self-reference left as the quoted
        // sentinel; the proc replace()s it with the real partition name at runtime.
        var partitionDdl = GetVersionedPartitionDdl(dim, $"'{PartitionNameSentinel}'");

        // Satellite tables: same set the runtime provider + SchemaInitialization create.
        // These are schema-agnostic (no schema self-reference), so no sentinel needed.
        var satelliteDdl = string.Join("\n\n",
            PartitionDefinition.DefaultSegmentTableMappings().Values.Distinct()
                .Select(t => GetSatelliteTableScript(t, dim)));

        // The DDL bodies contain $$ / $migrate$ dollar-quoted blocks, so the proc body
        // uses a distinct $ensure_partition_schema$ tag. Inner SQL string literals
        // (the DDL) use their own dollar-quote tags so we don't have to escape '.
        return $"""
            CREATE OR REPLACE FUNCTION public.ensure_partition_schema(partition_name text)
            RETURNS void AS $ensure_partition_schema$
            DECLARE
                versioned_ddl text;
                satellite_ddl text;
            BEGIN
                -- 1. Create the schema (idempotent, identifier-safe via %I).
                EXECUTE format('CREATE SCHEMA IF NOT EXISTS %I', partition_name);

                -- 2. Route all subsequent unqualified DDL into the partition schema —
                --    mirrors the per-schema NpgsqlDataSource(SearchPath=partition,public)
                --    the runtime provider builds. public stays on the path for the
                --    shared notify/mirror functions + public.partition_access.
                EXECUTE format('SET LOCAL search_path TO %I, public', partition_name);

                -- 3. Bind the partition name into the versioned DDL's schema
                --    self-references (rebuild_*_permissions search_path + partition_access
                --    sync) via plain replace() — NOT format() — so the %I/%L inside the
                --    DDL's own format() calls survive untouched.
                versioned_ddl := replace($versioned${partitionDdl}$versioned$,
                                         '{PartitionNameSentinel}', partition_name);
                EXECUTE versioned_ddl;

                -- 4. Satellite tables (schema-agnostic; land in the partition via search_path).
                satellite_ddl := $satellite${satelliteDdl}$satellite$;
                EXECUTE satellite_ddl;
            END;
            $ensure_partition_schema$ LANGUAGE plpgsql;
            """;
    }

    /// <summary>
    /// DDL for <c>public.mirror_access_object_to_auth_schema()</c> — the AFTER
    /// INSERT/UPDATE/DELETE trigger function that mirrors every access-object node
    /// (<c>User</c>, <c>Group</c>, <c>Role</c>, <c>VUser</c>, <c>ApiToken</c>, <c>Space</c>)
    /// from a partition's <c>mesh_nodes</c> into the central <c>auth.mesh_nodes</c> lookup mirror.
    /// The per-partition DDL (<see cref="GetVersionedPartitionDdl"/>) installs the trigger
    /// that calls this function — but only when this function already exists, so it must be
    /// created on the always-run init path (and by the V32 repair for legacy partitions).
    ///
    /// <para><b>Fail-safe.</b> If the <c>auth</c> mirror table isn't provisioned yet
    /// (<c>to_regclass('"auth".mesh_nodes') IS NULL</c>) the function is a no-op — a missing
    /// mirror must NEVER fail the originating write on every partition. (V27's original body
    /// lacked this guard and relied on <c>auth</c> always existing.)</para>
    ///
    /// <para><b>Single-sourced.</b> <see cref="InitializeAsync"/> runs this, and the
    /// <c>V32_RepairAuthMirrorTriggerAndBackfill</c> migration calls it for legacy DBs whose
    /// partitions predate the trigger. Idempotent (<c>CREATE OR REPLACE</c>).</para>
    ///
    /// <para>🚨 <b>The node-type list below is THE live list — keep it complete.</b> Because this
    /// script re-runs on every startup/schema-init (and V32 replays it), it silently OVERWRITES any
    /// list extension a later migration made to the deployed function. That is exactly how V28's
    /// <c>'Space'</c> extension was lost in production: V28 patched the function inline, this
    /// constant re-created it without <c>'Space'</c> on the next restart, and every Space created
    /// since then never reached <c>auth.mesh_nodes</c> — invisible in the Spaces catalog while its
    /// partition, grants and content all worked (RolePlay/X/Chess, 2026-07). When a new node type
    /// must mirror, extend THIS list (both branches) and add a backfill migration
    /// (<c>V42_ReapplySpaceAuthMirrorAndBackfill</c> is the model); never patch the deployed
    /// function from a migration alone.</para>
    /// </summary>
    public static string GetAuthMirrorFunctionScript() => """
        CREATE OR REPLACE FUNCTION public.mirror_access_object_to_auth_schema()
        RETURNS TRIGGER AS $auth_mirror$
        BEGIN
            -- Fail-safe: never break the originating write if the auth mirror
            -- table isn't provisioned yet.
            IF to_regclass('"auth".mesh_nodes') IS NULL THEN
                RETURN COALESCE(NEW, OLD);
            END IF;

            IF TG_OP = 'DELETE' THEN
                IF OLD.node_type IN ('User','Group','Role','VUser','ApiToken','Space','GroupMembership') THEN
                    DELETE FROM "auth".mesh_nodes
                     WHERE namespace = OLD.namespace AND id = OLD.id;
                END IF;
                RETURN OLD;
            END IF;

            IF NEW.node_type IN ('User','Group','Role','VUser','ApiToken','Space','GroupMembership') THEN
                INSERT INTO "auth".mesh_nodes
                    (namespace, id, name, node_type, category, icon, display_order,
                     last_modified, version, state, content, desired_id, main_node)
                VALUES (NEW.namespace, NEW.id, NEW.name, NEW.node_type, NEW.category, NEW.icon, NEW.display_order,
                        NEW.last_modified, NEW.version, NEW.state, NEW.content, NEW.desired_id, NEW.main_node)
                ON CONFLICT (namespace, id) DO UPDATE SET
                    name = EXCLUDED.name,
                    node_type = EXCLUDED.node_type,
                    category = EXCLUDED.category,
                    icon = EXCLUDED.icon,
                    display_order = EXCLUDED.display_order,
                    last_modified = EXCLUDED.last_modified,
                    version = EXCLUDED.version,
                    state = EXCLUDED.state,
                    content = EXCLUDED.content,
                    desired_id = EXCLUDED.desired_id,
                    main_node = EXCLUDED.main_node;
            END IF;
            RETURN NEW;
        END;
        $auth_mirror$ LANGUAGE plpgsql;
        """;

    /// <summary>
    /// Acquires a Postgres session-level advisory lock keyed by
    /// <paramref name="schema"/> so the caller can run schema-init DDL on the
    /// locked connection, then release the lock. The returned awaitable
    /// disposable is intended for <c>await using</c> at the caller; disposing
    /// releases the lock and the underlying connection.
    /// <para>
    /// Without cross-silo serialisation, two silos (HA pair, multiple
    /// Memex.Portal.Distributed replicas, …) racing the schema-init DDL on
    /// the same partition collide on the Postgres system catalog and
    /// surface as <c>XX000: tuple concurrently updated</c> in
    /// <c>simple_heap_update</c>. The cascade — schema init throws →
    /// <c>RoutingPersistenceServiceCore.InitializeAsync</c> → MessageHub
    /// initialise gate never opens → SubscribeRequest hangs at the timeout
    /// → GUI sees Blazor SignalR session stuck — is exactly the prod
    /// symptom surfaced in Grafana/Loki. The advisory lock keyed per schema
    /// lets distinct schemas init in parallel while same-schema init
    /// across silos serialises.
    /// </para>
    /// <para>
    /// Key: stable FNV-1a hash of the schema name. <c>string.GetHashCode</c>
    /// is randomised per process — different silos would compute different
    /// keys for the same schema, defeating the purpose.
    /// </para>
    /// </summary>
    public static async Task<IAsyncDisposable> AcquireSchemaInitLockAsync(
        NpgsqlDataSource dataSource, string schema, CancellationToken ct = default)
    {
        var lockKey = ComputeAdvisoryLockKey(schema);
        var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            await using var lockCmd = conn.CreateCommand();
            lockCmd.CommandText = "SELECT pg_advisory_lock(@key)";
            lockCmd.Parameters.AddWithValue("key", lockKey);
            await lockCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await conn.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return new SchemaInitLock(conn, lockKey);
    }

    private sealed class SchemaInitLock(NpgsqlConnection conn, long key) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                // Best-effort unlock; if the connection is being torn down
                // anyway the lock releases at session end. Use CancellationToken.None
                // so a caller-cancelled ct can't prevent the unlock SQL from running.
                await using var unlockCmd = conn.CreateCommand();
                unlockCmd.CommandText = "SELECT pg_advisory_unlock(@key)";
                unlockCmd.Parameters.AddWithValue("key", key);
                await unlockCmd.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // ignore — lock will release at session end
            }
            await conn.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static long ComputeAdvisoryLockKey(string schema)
    {
        const ulong fnvOffsetBasis = 14695981039346656037UL;
        const ulong fnvPrime = 1099511628211UL;
        var hash = fnvOffsetBasis;
        foreach (var c in schema)
        {
            hash ^= c;
            hash *= fnvPrime;
        }
        return unchecked((long)hash);
    }

    /// <summary>
    /// Initializes mesh tables only (no history/versioning). Used for unversioned partitions (Portal, Kernel).
    /// </summary>
    public static async Task InitializeMeshTablesAsync(
        NpgsqlDataSource schemaDataSource, PostgreSqlStorageOptions options, CancellationToken ct = default)
    {
        await using (var conn = await schemaDataSource.OpenConnectionAsync(ct).ConfigureAwait(false))
        {
            await conn.ReloadTypesAsync().ConfigureAwait(false);
        }

        await using (var cmd = schemaDataSource.CreateCommand(GetUnversionedSchemaScript(options)))
        {
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Initializes mesh tables in the org schema and mesh_node_history in a separate versions schema.
    /// The trigger on mesh_nodes writes cross-schema to {versionsSchema}.mesh_node_history.
    /// </summary>
    public static async Task InitializeWithVersionsSchemaAsync(
        NpgsqlDataSource baseDataSource,
        NpgsqlDataSource schemaDataSource,
        NpgsqlDataSource versionsDataSource,
        PostgreSqlStorageOptions options,
        string versionsSchema,
        CancellationToken ct = default)
    {
        // Step 1: Create the vector extension using plain SQL
        await using (var cmd = baseDataSource.CreateCommand("CREATE EXTENSION IF NOT EXISTS vector"))
        {
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // Step 2: Reload types on both data sources
        await using (var conn = await schemaDataSource.OpenConnectionAsync(ct).ConfigureAwait(false))
        {
            await conn.ReloadTypesAsync().ConfigureAwait(false);
        }

        // Step 3: Create versions schema tables (mesh_node_history)
        await using (var cmd = versionsDataSource.CreateCommand(GetVersionsSchemaScript()))
        {
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // Step 4: Create mesh schema tables + cross-schema trigger
        await using (var cmd = schemaDataSource.CreateCommand(GetMeshSchemaScript(options, versionsSchema)))
        {
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Creates satellite tables (same structure as mesh_nodes) for satellite node types.
    /// Each table name comes from the PartitionDefinition.TableMappings values.
    /// </summary>
    public static async Task CreateSatelliteTablesAsync(
        NpgsqlDataSource schemaDataSource,
        PostgreSqlStorageOptions options,
        IEnumerable<string> tableNames,
        CancellationToken ct = default)
    {
        var dim = options.VectorDimensions;
        foreach (var tableName in tableNames.Distinct())
        {
            await using var cmd = schemaDataSource.CreateCommand(GetSatelliteTableScript(tableName, dim));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// DDL for one satellite table (same structure as mesh_nodes) + its indexes
    /// and pg_notify trigger. Unqualified — resolves against the connection's
    /// search_path. Shared by <see cref="CreateSatelliteTablesAsync"/> (per-schema
    /// data source) AND <see cref="GetEnsurePartitionSchemaProcScript"/> (the
    /// <c>public.ensure_partition_schema</c> stored proc, which SETs search_path
    /// then EXECUTEs this verbatim) so the two paths are byte-faithful.
    /// </summary>
    internal static string GetSatelliteTableScript(string tableName, int dim) => $$"""
        CREATE TABLE IF NOT EXISTS "{{tableName}}" (
            namespace       TEXT        NOT NULL DEFAULT '',
            id              TEXT        NOT NULL,
            path            TEXT        GENERATED ALWAYS AS (
                                CASE WHEN namespace = '' THEN id ELSE namespace || '/' || id END
                            ) STORED,
            name            TEXT,
            node_type       TEXT,
            description     TEXT,
            category        TEXT,
            icon            TEXT,
            display_order   INTEGER,
            last_modified   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
            version         BIGINT      NOT NULL DEFAULT 0,
            state           SMALLINT    NOT NULL DEFAULT 0,
            content         JSONB,
            desired_id      TEXT,
            main_node       TEXT,
            embedding       vector({{dim}}),
            PRIMARY KEY (namespace, id)
        );

        CREATE INDEX IF NOT EXISTS "idx_{{tableName}}_path" ON "{{tableName}}" (path);
        CREATE INDEX IF NOT EXISTS "idx_{{tableName}}_main_node" ON "{{tableName}}" (main_node);
        CREATE INDEX IF NOT EXISTS "idx_{{tableName}}_node_type" ON "{{tableName}}" (node_type);
        CREATE INDEX IF NOT EXISTS "idx_{{tableName}}_last_modified" ON "{{tableName}}" (last_modified DESC);
        -- Functional LOWER() indexes — SQL generator case-folds every
        -- text equality (LOWER(n.namespace) = $1 etc.); without these
        -- the plain indexes above don't match and Postgres falls back
        -- to sequential scan on satellite tables.
        CREATE INDEX IF NOT EXISTS "idx_{{tableName}}_namespace_lower" ON "{{tableName}}" (LOWER(namespace));
        CREATE INDEX IF NOT EXISTS "idx_{{tableName}}_node_type_lower" ON "{{tableName}}" (LOWER(node_type));
        CREATE INDEX IF NOT EXISTS "idx_{{tableName}}_main_node_lower" ON "{{tableName}}" (LOWER(main_node));

        -- pg_notify trigger on the satellite table — mirrors the one
        -- installed on mesh_nodes by GetSchemaScript. Without this,
        -- writes to satellite tables (AccessAssignment / Thread /
        -- Activity / Comment / etc.) never fire pg_notify and synced
        -- queries scoped to satellite namespaces (`namespace:X/_Access`,
        -- `namespace:X/_Thread`, …) stay frozen at their Initial state.
        DROP TRIGGER IF EXISTS "{{tableName}}_notify" ON "{{tableName}}";
        CREATE TRIGGER "{{tableName}}_notify"
            AFTER INSERT OR UPDATE OR DELETE ON "{{tableName}}"
            FOR EACH ROW EXECUTE FUNCTION notify_mesh_node_changes();
        """;

    /// <summary>
    /// Returns SQL for the versions schema: mesh_node_history table + indexes.
    /// </summary>
    internal static string GetVersionsSchemaScript()
    {
        return """
            -- mesh_node_history: versioned copies of mesh_nodes (PK includes version)
            CREATE TABLE IF NOT EXISTS mesh_node_history (
                namespace       TEXT        NOT NULL DEFAULT '',
                id              TEXT        NOT NULL,
                path            TEXT        GENERATED ALWAYS AS (
                                    CASE WHEN namespace = '' THEN id ELSE namespace || '/' || id END
                                ) STORED,
                name            TEXT,
                node_type       TEXT,
                description     TEXT,
                category        TEXT,
                icon            TEXT,
                display_order   INTEGER,
                last_modified   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                version         BIGINT      NOT NULL DEFAULT 0,
                state           SMALLINT    NOT NULL DEFAULT 0,
                content         JSONB,
                desired_id      TEXT,
                changed_by      TEXT,
                main_node       TEXT,
                PRIMARY KEY (namespace, id, version)
            );

            CREATE INDEX IF NOT EXISTS idx_mnh_path ON mesh_node_history (path);
            CREATE INDEX IF NOT EXISTS idx_mnh_path_version ON mesh_node_history (path, version DESC);
            """;
    }

    /// <summary>
    /// Returns SQL for the mesh schema: all tables except mesh_node_history,
    /// with a trigger that writes cross-schema to {versionsSchema}.mesh_node_history.
    /// </summary>
    internal static string GetMeshSchemaScript(PostgreSqlStorageOptions options, string versionsSchema)
    {
        var dim = options.VectorDimensions;
        var schemaName = options.Schema ?? "public";
        return $$"""
            CREATE EXTENSION IF NOT EXISTS vector;

            -- mesh_nodes
            CREATE TABLE IF NOT EXISTS mesh_nodes (
                namespace       TEXT        NOT NULL DEFAULT '',
                id              TEXT        NOT NULL,
                path            TEXT        GENERATED ALWAYS AS (
                                    CASE WHEN namespace = '' THEN id ELSE namespace || '/' || id END
                                ) STORED,
                name            TEXT,
                node_type       TEXT,
                description     TEXT,
                category        TEXT,
                icon            TEXT,
                display_order   INTEGER,
                last_modified   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                version         BIGINT      NOT NULL DEFAULT 0,
                state           SMALLINT    NOT NULL DEFAULT 0,
                -- node-level static-repo sync claim (0=Include, 1=ExcludeThisOnly,
                -- 2=ExcludeThisAndChildren). Persists the "Not synced" decouple so an
                -- admin-claimed node/partition survives restart + the next import.
                sync_behavior   SMALLINT    NOT NULL DEFAULT 0,
                content         JSONB,
                desired_id      TEXT,
                main_node       TEXT,
                embedding       vector({{dim}}),
                PRIMARY KEY (namespace, id)
            );

            CREATE INDEX IF NOT EXISTS idx_mn_path ON mesh_nodes (path);
            CREATE INDEX IF NOT EXISTS idx_mn_path_prefix ON mesh_nodes (path text_pattern_ops);
            CREATE INDEX IF NOT EXISTS idx_mn_namespace ON mesh_nodes (namespace);
            CREATE INDEX IF NOT EXISTS idx_mn_node_type ON mesh_nodes (node_type);
            -- Functional indexes for case-insensitive equality: the SQL generator
            -- emits `LOWER(n.namespace) = $1` / `LOWER(n.node_type) = $1` for every
            -- text-field equality (PostgreSqlSqlGenerator.GenerateComparisonClause
            -- case-folds via ToLowerInvariant). Without the LOWER() expression
            -- indexes, Postgres falls back to sequential scan because the plain
            -- (namespace) / (node_type) indexes don't match the function
            -- expression. Add them alongside (not in place of) so any future
            -- case-sensitive query path still has support.
            CREATE INDEX IF NOT EXISTS idx_mn_namespace_lower ON mesh_nodes (LOWER(namespace));
            CREATE INDEX IF NOT EXISTS idx_mn_node_type_lower ON mesh_nodes (LOWER(node_type));
            CREATE INDEX IF NOT EXISTS idx_mn_path_lower ON mesh_nodes (LOWER(path));
            CREATE INDEX IF NOT EXISTS idx_mn_content ON mesh_nodes USING gin (content jsonb_path_ops);
            CREATE INDEX IF NOT EXISTS idx_mn_text_search ON mesh_nodes USING gin (
                to_tsvector('english', COALESCE(name,'') || ' ' || COALESCE(description,'') || ' ' || COALESCE(node_type,''))
            );
            CREATE INDEX IF NOT EXISTS idx_mn_embedding ON mesh_nodes USING hnsw (embedding vector_cosine_ops);

            -- Migrate embedding column if dimensions changed
            DO $migrate$
            DECLARE cur_dim INT;
            BEGIN
                SELECT atttypmod INTO cur_dim FROM pg_attribute
                WHERE attrelid = 'mesh_nodes'::regclass AND attname = 'embedding' AND atttypmod > 0;
                IF cur_dim IS NOT NULL AND cur_dim != {{dim}} THEN
                    DROP INDEX IF EXISTS idx_mn_embedding;
                    ALTER TABLE mesh_nodes ALTER COLUMN embedding TYPE vector({{dim}}) USING NULL;
                    CREATE INDEX idx_mn_embedding ON mesh_nodes USING hnsw (embedding vector_cosine_ops);
                END IF;
            END $migrate$;

            -- partition_objects
            CREATE TABLE IF NOT EXISTS partition_objects (
                id              TEXT        NOT NULL,
                partition_key   TEXT        NOT NULL,
                type_name       TEXT,
                data            JSONB       NOT NULL,
                last_modified   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                PRIMARY KEY (partition_key, id)
            );

            -- user_activity
            CREATE TABLE IF NOT EXISTS user_activity (
                user_id         TEXT        NOT NULL,
                node_path       TEXT        NOT NULL,
                activity_type   SMALLINT    NOT NULL DEFAULT 0,
                first_accessed  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                last_accessed   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                access_count    INTEGER     NOT NULL DEFAULT 1,
                node_name       TEXT,
                node_type       TEXT,
                namespace       TEXT,
                PRIMARY KEY (user_id, node_path)
            );
            CREATE INDEX IF NOT EXISTS idx_ua_user_last ON user_activity (user_id, last_accessed DESC);
            CREATE INDEX IF NOT EXISTS idx_ua_node_type ON user_activity (user_id, node_type);

            -- change_logs (bundled activity logs)
            CREATE TABLE IF NOT EXISTS change_logs (
                id              TEXT        NOT NULL PRIMARY KEY,
                hub_path        TEXT        NOT NULL,
                changed_by      TEXT,
                category        TEXT        NOT NULL,
                start_time      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                end_time        TIMESTAMPTZ,
                change_count    INTEGER     NOT NULL DEFAULT 0,
                status          SMALLINT    NOT NULL DEFAULT 1,
                messages        JSONB
            );
            CREATE INDEX IF NOT EXISTS idx_cl_hub ON change_logs (hub_path, start_time DESC);
            CREATE INDEX IF NOT EXISTS idx_cl_user ON change_logs (changed_by, start_time DESC);

            -- user_effective_permissions (denormalized)
            CREATE TABLE IF NOT EXISTS user_effective_permissions (
                user_id          TEXT     NOT NULL,
                node_path_prefix TEXT     NOT NULL,
                permission       TEXT     NOT NULL,
                is_allow         BOOLEAN  NOT NULL,
                PRIMARY KEY (user_id, node_path_prefix, permission)
            );
            CREATE INDEX IF NOT EXISTS idx_uep_user_perm ON user_effective_permissions (user_id, permission);
            CREATE INDEX IF NOT EXISTS idx_uep_path ON user_effective_permissions (node_path_prefix text_pattern_ops);

            -- Shadow table for atomic rebuild
            CREATE TABLE IF NOT EXISTS user_effective_permissions_shadow (LIKE user_effective_permissions INCLUDING ALL);

            -- Rebuild function: reads AccessAssignment from access satellite table, GroupMembership from mesh_nodes
            -- AccessAssignment content: {"accessObject":"...","roles":[{"role":"...","denied":true},...]}
            CREATE OR REPLACE FUNCTION rebuild_user_effective_permissions() RETURNS void AS $$
            BEGIN
                -- Set search_path so unqualified table names resolve to this schema
                EXECUTE format('SET LOCAL search_path TO %I, public', '{{schemaName}}');
                TRUNCATE user_effective_permissions_shadow;

                -- Direct entries from AccessAssignment nodes (access satellite table)
                INSERT INTO user_effective_permissions_shadow (user_id, node_path_prefix, permission, is_allow)
                SELECT
                    aa.content->>'accessObject' AS user_id,
                    COALESCE(aa.main_node, aa.namespace) AS node_path_prefix,
                    perm.permission,
                    NOT COALESCE((role_entry->>'denied')::boolean, false) AS is_allow
                FROM access aa
                CROSS JOIN LATERAL jsonb_array_elements(aa.content->'roles') AS role_entry
                CROSS JOIN LATERAL (
                    SELECT COALESCE(
                        (SELECT (role_node.content->>'permissions')::int
                         FROM mesh_nodes role_node
                         WHERE role_node.node_type = 'Role'
                           AND role_node.id = role_entry->>'role'
                         LIMIT 1),
                        CASE role_entry->>'role'
                            WHEN 'Admin' THEN 1535
                            WHEN 'PlatformAdmin' THEN 1535
                            WHEN 'Editor' THEN 1527
                            WHEN 'Viewer' THEN 161
                            WHEN 'Commenter' THEN 145
                            ELSE 0
                        END
                    ) AS permissions
                ) r
                CROSS JOIN LATERAL (
                    SELECT unnest(
                        CASE WHEN (r.permissions & 1) > 0 THEN ARRAY['Read'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 2) > 0 THEN ARRAY['Create'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 4) > 0 THEN ARRAY['Update'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 8) > 0 THEN ARRAY['Delete'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 16) > 0 THEN ARRAY['Comment'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 32) > 0 THEN ARRAY['Execute'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 64) > 0 THEN ARRAY['Thread'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 128) > 0 THEN ARRAY['Api'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 256) > 0 THEN ARRAY['Export'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 1024) > 0 THEN ARRAY['Compile'] ELSE ARRAY[]::text[] END
                    ) AS permission
                ) perm
                WHERE aa.content->>'accessObject' IS NOT NULL
                  AND aa.content->'roles' IS NOT NULL
                ON CONFLICT (user_id, node_path_prefix, permission) DO UPDATE
                    SET is_allow = CASE WHEN EXCLUDED.is_allow = false THEN false ELSE user_effective_permissions_shadow.is_allow END;

                -- Group expansion: read GroupMembership MeshNodes
                INSERT INTO user_effective_permissions_shadow (user_id, node_path_prefix, permission, is_allow)
                WITH RECURSIVE all_members AS (
                    SELECT group_entry->>'group' AS group_path, gm.content->>'member' AS member_id
                    FROM "auth".mesh_nodes gm
                    CROSS JOIN LATERAL jsonb_array_elements(gm.content->'groups') AS group_entry
                    WHERE gm.node_type = 'GroupMembership'
                    UNION
                    SELECT am.group_path, gm.content->>'member'
                    FROM all_members am
                    JOIN "auth".mesh_nodes gm ON gm.node_type = 'GroupMembership'
                    WHERE EXISTS (
                        SELECT 1 FROM jsonb_array_elements(gm.content->'groups') g
                        WHERE g->>'group' = am.member_id
                    )
                ),
                leaf_members AS (
                    SELECT group_path, member_id FROM all_members
                    WHERE NOT EXISTS (
                        SELECT 1 FROM "auth".mesh_nodes gm2
                        CROSS JOIN LATERAL jsonb_array_elements(gm2.content->'groups') AS g2
                        WHERE gm2.node_type = 'GroupMembership'
                          AND g2->>'group' = all_members.member_id
                    )
                )
                SELECT lm.member_id, COALESCE(aa.main_node, aa.namespace), perm.permission,
                       NOT COALESCE((role_entry->>'denied')::boolean, false) AS is_allow
                FROM access aa
                JOIN leaf_members lm ON aa.content->>'accessObject' = lm.group_path
                CROSS JOIN LATERAL jsonb_array_elements(aa.content->'roles') AS role_entry
                CROSS JOIN LATERAL (
                    SELECT COALESCE(
                        (SELECT (role_node.content->>'permissions')::int
                         FROM mesh_nodes role_node
                         WHERE role_node.node_type = 'Role'
                           AND role_node.id = role_entry->>'role'
                         LIMIT 1),
                        CASE role_entry->>'role'
                            WHEN 'Admin' THEN 1535
                            WHEN 'PlatformAdmin' THEN 1535
                            WHEN 'Editor' THEN 1527
                            WHEN 'Viewer' THEN 161
                            WHEN 'Commenter' THEN 145
                            ELSE 0
                        END
                    ) AS permissions
                ) r
                CROSS JOIN LATERAL (
                    SELECT unnest(
                        CASE WHEN (r.permissions & 1) > 0 THEN ARRAY['Read'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 2) > 0 THEN ARRAY['Create'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 4) > 0 THEN ARRAY['Update'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 8) > 0 THEN ARRAY['Delete'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 16) > 0 THEN ARRAY['Comment'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 32) > 0 THEN ARRAY['Execute'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 64) > 0 THEN ARRAY['Thread'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 128) > 0 THEN ARRAY['Api'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 256) > 0 THEN ARRAY['Export'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 1024) > 0 THEN ARRAY['Compile'] ELSE ARRAY[]::text[] END
                    ) AS permission
                ) perm
                WHERE aa.content->'roles' IS NOT NULL
                ON CONFLICT (user_id, node_path_prefix, permission) DO UPDATE
                    SET is_allow = CASE WHEN EXCLUDED.is_allow = false THEN false ELSE user_effective_permissions_shadow.is_allow END;

                -- Direct entries from access_control table (convenience methods)
                INSERT INTO user_effective_permissions_shadow (user_id, node_path_prefix, permission, is_allow)
                SELECT subject, node_path, permission, is_allow
                FROM access_control
                ON CONFLICT (user_id, node_path_prefix, permission) DO UPDATE
                    SET is_allow = CASE WHEN EXCLUDED.is_allow = false THEN false ELSE user_effective_permissions_shadow.is_allow END;

                -- Group expansion from group_members + access_control
                INSERT INTO user_effective_permissions_shadow (user_id, node_path_prefix, permission, is_allow)
                WITH RECURSIVE all_members AS (
                    SELECT group_name, member_id FROM group_members
                    UNION
                    SELECT am.group_name, gm.member_id
                    FROM all_members am
                    JOIN group_members gm ON gm.group_name = am.member_id
                ),
                leaf_members AS (
                    SELECT group_name, member_id FROM all_members
                    WHERE member_id NOT IN (SELECT DISTINCT group_name FROM group_members)
                )
                SELECT lm.member_id, ac.node_path, ac.permission, ac.is_allow
                FROM access_control ac
                JOIN leaf_members lm ON lm.group_name = ac.subject
                ON CONFLICT (user_id, node_path_prefix, permission) DO UPDATE
                    SET is_allow = CASE WHEN EXCLUDED.is_allow = false THEN false ELSE user_effective_permissions_shadow.is_allow END;

                -- Apply PartitionAccessPolicy caps
                INSERT INTO user_effective_permissions_shadow (user_id, node_path_prefix, permission, is_allow)
                SELECT DISTINCT
                    uep.user_id,
                    policy.namespace AS node_path_prefix,
                    perm.permission,
                    false
                FROM mesh_nodes policy
                CROSS JOIN (SELECT DISTINCT user_id FROM user_effective_permissions_shadow) uep
                CROSS JOIN (
                    SELECT unnest(ARRAY['Read','Create','Update','Delete','Comment']) AS permission,
                           unnest(ARRAY['read','create','update','delete','comment']) AS field
                ) perm
                WHERE policy.node_type = 'PartitionAccessPolicy'
                  AND policy.id = '_Policy'
                  AND (policy.content->>perm.field)::boolean = false
                ON CONFLICT (user_id, node_path_prefix, permission) DO UPDATE SET is_allow = false;

                -- Atomic swap
                ALTER TABLE user_effective_permissions RENAME TO user_effective_permissions_old;
                ALTER TABLE user_effective_permissions_shadow RENAME TO user_effective_permissions;
                ALTER TABLE user_effective_permissions_old RENAME TO user_effective_permissions_shadow;

                -- Sync partition_access: upsert users with Read permission, remove revoked
                BEGIN
                    INSERT INTO public.partition_access (user_id, partition)
                    SELECT DISTINCT user_id, '{{schemaName}}'
                    FROM user_effective_permissions
                    WHERE permission = 'Read' AND is_allow = true
                    ON CONFLICT (user_id, partition) DO NOTHING;

                    DELETE FROM public.partition_access
                    WHERE partition = '{{schemaName}}'
                      AND user_id NOT IN (
                        SELECT user_id FROM user_effective_permissions
                        WHERE permission = 'Read' AND is_allow = true
                      );
                EXCEPTION WHEN undefined_table THEN
                    -- partition_access table may not exist yet (first migration)
                    NULL;
                END;
            END;
            $$ LANGUAGE plpgsql;

            -- Access satellite table (must exist before trigger creation)
            CREATE TABLE IF NOT EXISTS access (
                namespace       TEXT        NOT NULL DEFAULT '',
                id              TEXT        NOT NULL,
                path            TEXT        GENERATED ALWAYS AS (
                                    CASE WHEN namespace = '' THEN id ELSE namespace || '/' || id END
                                ) STORED,
                name            TEXT,
                node_type       TEXT,
                description     TEXT,
                category        TEXT,
                icon            TEXT,
                display_order   INTEGER,
                last_modified   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                version         BIGINT      NOT NULL DEFAULT 0,
                state           SMALLINT    NOT NULL DEFAULT 0,
                -- node-level static-repo sync claim (0=Include, 1=ExcludeThisOnly,
                -- 2=ExcludeThisAndChildren). Persists the "Not synced" decouple so an
                -- admin-claimed node/partition survives restart + the next import.
                sync_behavior   SMALLINT    NOT NULL DEFAULT 0,
                content         JSONB,
                desired_id      TEXT,
                main_node       TEXT,
                embedding       vector({{dim}}),
                PRIMARY KEY (namespace, id)
            );
            CREATE INDEX IF NOT EXISTS idx_access_path ON access (path);
            CREATE INDEX IF NOT EXISTS idx_access_main_node ON access (main_node);
            CREATE INDEX IF NOT EXISTS idx_access_node_type ON access (node_type);
            CREATE INDEX IF NOT EXISTS idx_access_last_modified ON access (last_modified DESC);
            -- Functional LOWER() indexes — SQL generator case-folds text equality.
            CREATE INDEX IF NOT EXISTS idx_access_namespace_lower ON access (LOWER(namespace));
            CREATE INDEX IF NOT EXISTS idx_access_node_type_lower ON access (LOWER(node_type));
            CREATE INDEX IF NOT EXISTS idx_access_main_node_lower ON access (LOWER(main_node));

            -- Per-user permission rebuild: concurrent-safe, only touches one user's rows.
            CREATE OR REPLACE FUNCTION rebuild_user_permissions_for(p_user_id TEXT) RETURNS void AS $$
            BEGIN
                EXECUTE format('SET LOCAL search_path TO %I, public', '{{schemaName}}');
                DELETE FROM user_effective_permissions WHERE user_id = p_user_id;

                -- Direct entries from AccessAssignment nodes for this user
                INSERT INTO user_effective_permissions (user_id, node_path_prefix, permission, is_allow)
                SELECT
                    p_user_id,
                    COALESCE(aa.main_node, aa.namespace),
                    perm.permission,
                    NOT COALESCE((role_entry->>'denied')::boolean, false)
                FROM access aa
                CROSS JOIN LATERAL jsonb_array_elements(aa.content->'roles') AS role_entry
                CROSS JOIN LATERAL (
                    SELECT COALESCE(
                        (SELECT (rn.content->>'permissions')::int FROM mesh_nodes rn
                         WHERE rn.node_type = 'Role' AND rn.id = role_entry->>'role' LIMIT 1),
                        CASE role_entry->>'role'
                            WHEN 'Admin' THEN 1535 WHEN 'PlatformAdmin' THEN 1535
                            WHEN 'Editor' THEN 1527 WHEN 'Viewer' THEN 161
                            WHEN 'Commenter' THEN 145 ELSE 0
                        END
                    ) AS permissions
                ) r
                CROSS JOIN LATERAL (
                    SELECT unnest(
                        CASE WHEN (r.permissions & 1) > 0 THEN ARRAY['Read'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 2) > 0 THEN ARRAY['Create'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 4) > 0 THEN ARRAY['Update'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 8) > 0 THEN ARRAY['Delete'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 16) > 0 THEN ARRAY['Comment'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 32) > 0 THEN ARRAY['Execute'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 64) > 0 THEN ARRAY['Thread'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 128) > 0 THEN ARRAY['Api'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 256) > 0 THEN ARRAY['Export'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 1024) > 0 THEN ARRAY['Compile'] ELSE ARRAY[]::text[] END
                    ) AS permission
                ) perm
                WHERE aa.content->>'accessObject' = p_user_id
                  AND aa.content->'roles' IS NOT NULL
                ON CONFLICT (user_id, node_path_prefix, permission) DO UPDATE
                    SET is_allow = CASE WHEN EXCLUDED.is_allow = false THEN false ELSE user_effective_permissions.is_allow END;

                -- Group expansion for this user
                INSERT INTO user_effective_permissions (user_id, node_path_prefix, permission, is_allow)
                WITH RECURSIVE all_members AS (
                    SELECT group_entry->>'group' AS group_path, gm.content->>'member' AS member_id
                    FROM "auth".mesh_nodes gm
                    CROSS JOIN LATERAL jsonb_array_elements(gm.content->'groups') AS group_entry
                    WHERE gm.node_type = 'GroupMembership'
                    UNION
                    SELECT am.group_path, gm.content->>'member'
                    FROM all_members am
                    JOIN "auth".mesh_nodes gm ON gm.node_type = 'GroupMembership'
                    WHERE EXISTS (SELECT 1 FROM jsonb_array_elements(gm.content->'groups') g WHERE g->>'group' = am.member_id)
                ),
                user_groups AS (
                    SELECT DISTINCT group_path FROM all_members WHERE member_id = p_user_id
                )
                SELECT p_user_id, COALESCE(aa.main_node, aa.namespace), perm.permission,
                       NOT COALESCE((role_entry->>'denied')::boolean, false)
                FROM access aa
                JOIN user_groups ug ON aa.content->>'accessObject' = ug.group_path
                CROSS JOIN LATERAL jsonb_array_elements(aa.content->'roles') AS role_entry
                CROSS JOIN LATERAL (
                    SELECT COALESCE(
                        (SELECT (rn.content->>'permissions')::int FROM mesh_nodes rn
                         WHERE rn.node_type = 'Role' AND rn.id = role_entry->>'role' LIMIT 1),
                        CASE role_entry->>'role'
                            WHEN 'Admin' THEN 1535 WHEN 'PlatformAdmin' THEN 1535
                            WHEN 'Editor' THEN 1527 WHEN 'Viewer' THEN 161
                            WHEN 'Commenter' THEN 145 ELSE 0
                        END
                    ) AS permissions
                ) r
                CROSS JOIN LATERAL (
                    SELECT unnest(
                        CASE WHEN (r.permissions & 1) > 0 THEN ARRAY['Read'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 2) > 0 THEN ARRAY['Create'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 4) > 0 THEN ARRAY['Update'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 8) > 0 THEN ARRAY['Delete'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 16) > 0 THEN ARRAY['Comment'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 32) > 0 THEN ARRAY['Execute'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 64) > 0 THEN ARRAY['Thread'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 128) > 0 THEN ARRAY['Api'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 256) > 0 THEN ARRAY['Export'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 1024) > 0 THEN ARRAY['Compile'] ELSE ARRAY[]::text[] END
                    ) AS permission
                ) perm
                WHERE aa.content->'roles' IS NOT NULL
                ON CONFLICT (user_id, node_path_prefix, permission) DO UPDATE
                    SET is_allow = CASE WHEN EXCLUDED.is_allow = false THEN false ELSE user_effective_permissions.is_allow END;

                -- access_control entries for this user
                INSERT INTO user_effective_permissions (user_id, node_path_prefix, permission, is_allow)
                SELECT subject, node_path, permission, is_allow FROM access_control WHERE subject = p_user_id
                ON CONFLICT (user_id, node_path_prefix, permission) DO UPDATE
                    SET is_allow = CASE WHEN EXCLUDED.is_allow = false THEN false ELSE user_effective_permissions.is_allow END;

                -- PartitionAccessPolicy caps for this user
                INSERT INTO user_effective_permissions (user_id, node_path_prefix, permission, is_allow)
                SELECT DISTINCT p_user_id, policy.namespace, perm.permission, false
                FROM mesh_nodes policy
                CROSS JOIN (
                    SELECT unnest(ARRAY['Read','Create','Update','Delete','Comment']) AS permission,
                           unnest(ARRAY['read','create','update','delete','comment']) AS field
                ) perm
                WHERE policy.node_type = 'PartitionAccessPolicy' AND policy.id = '_Policy'
                  AND (policy.content->>perm.field)::boolean = false
                ON CONFLICT (user_id, node_path_prefix, permission) DO UPDATE SET is_allow = false;

                -- Sync partition_access for this user
                BEGIN
                    IF EXISTS (SELECT 1 FROM user_effective_permissions
                               WHERE user_id = p_user_id AND permission = 'Read' AND is_allow = true) THEN
                        INSERT INTO public.partition_access (user_id, partition)
                        VALUES (p_user_id, '{{schemaName}}') ON CONFLICT DO NOTHING;
                    ELSE
                        DELETE FROM public.partition_access
                        WHERE user_id = p_user_id AND partition = '{{schemaName}}';
                    END IF;
                EXCEPTION WHEN undefined_table THEN NULL;
                END;
            END;
            $$ LANGUAGE plpgsql;

            -- Trigger function: per-row, rebuilds only the affected user's permissions.
            -- Body single-sourced from AccessChangedTriggerFunctionBody: every rebuild call is
            -- schema-qualified via TG_TABLE_SCHEMA — see the constant's doc for the 2026-07-13
            -- wrong-schema incident an unqualified PERFORM causes on the shared base pool.
            CREATE OR REPLACE FUNCTION trg_access_changed() RETURNS TRIGGER AS $trg_access$
            {{AccessChangedTriggerFunctionBody}}
            $trg_access$ LANGUAGE plpgsql;

            -- Drop old trigger on mesh_nodes if it exists
            DROP TRIGGER IF EXISTS mesh_node_access_changed ON mesh_nodes;

            -- Trigger on access table (per-row for concurrent safety)
            DROP TRIGGER IF EXISTS access_changed ON access;
            CREATE TRIGGER access_changed
                AFTER INSERT OR UPDATE OR DELETE ON access
                FOR EACH ROW EXECUTE FUNCTION trg_access_changed();

            -- Group/GroupMembership recompute is NOT installed per-partition: it is driven from the
            -- global auth mirror (see GetAuthMirrorSelfHealScript / trg_group_changed) so a group
            -- defined in one partition can be licensed on a course in another. A per-partition copy
            -- would double-fire (the change mirrors to auth) and couldn't see cross-partition members.

            -- Simple access_control and group_members tables used by convenience methods
            CREATE TABLE IF NOT EXISTS access_control (
                node_path   TEXT    NOT NULL,
                subject     TEXT    NOT NULL,
                permission  TEXT    NOT NULL,
                is_allow    BOOLEAN NOT NULL,
                PRIMARY KEY (node_path, subject, permission)
            );

            CREATE TABLE IF NOT EXISTS group_members (
                group_name  TEXT    NOT NULL,
                member_id   TEXT    NOT NULL,
                PRIMARY KEY (group_name, member_id)
            );

            -- Node type permission flags (populated from DI-registered NodeTypePermission records)
            CREATE TABLE IF NOT EXISTS node_type_permissions (
                node_type   TEXT    NOT NULL PRIMARY KEY,
                public_read BOOLEAN NOT NULL DEFAULT false
            );

            -- Drop legacy triggers if they exist (tables are now reused)
            DROP TRIGGER IF EXISTS access_control_changed ON access_control;
            DROP TRIGGER IF EXISTS group_members_changed ON group_members;
            DROP FUNCTION IF EXISTS trg_access_control_changed();

            -- Notify function for change notifications
            -- pg_notify dedup: suppress UPDATE that doesn't change any reactive
            -- consumer-visible field. Without this, idempotent writes (e.g. a
            -- workspace.Update lambda that returns the same node, or a same-
            -- value upsert on a write-heavy code path) fire NOTIFY → every
            -- synced-query subscriber wakes up → every subscriber re-reads →
            -- amplification feedback loop. The check uses IS NOT DISTINCT FROM
            -- so NULL is value-equal to NULL.  Version equality alone isn't
            -- enough: an upsert with the same Version but a fresher
            -- last_modified shouldn't fire either (no observable change).
            -- Prod incident 2026-05-20.
            CREATE OR REPLACE FUNCTION notify_mesh_node_changes() RETURNS TRIGGER AS $$
            BEGIN
                IF TG_OP = 'DELETE' THEN
                    PERFORM pg_notify('mesh_node_changes',
                        json_build_object('path', CASE WHEN OLD.namespace = '' THEN OLD.id ELSE OLD.namespace || '/' || OLD.id END, 'op', 'DELETE')::text);
                    RETURN OLD;
                ELSIF TG_OP = 'UPDATE'
                      AND OLD.content IS NOT DISTINCT FROM NEW.content
                      AND OLD.name IS NOT DISTINCT FROM NEW.name
                      AND OLD.node_type IS NOT DISTINCT FROM NEW.node_type
                      AND OLD.state IS NOT DISTINCT FROM NEW.state
                      AND OLD.version IS NOT DISTINCT FROM NEW.version
                      AND OLD.desired_id IS NOT DISTINCT FROM NEW.desired_id
                      AND OLD.main_node IS NOT DISTINCT FROM NEW.main_node THEN
                    -- No consumer-visible change. Skip pg_notify; the row write
                    -- still happens (we RETURN NEW so the UPDATE commits), but
                    -- no subscriber is woken.
                    RETURN NEW;
                ELSE
                    PERFORM pg_notify('mesh_node_changes',
                        json_build_object('path', CASE WHEN NEW.namespace = '' THEN NEW.id ELSE NEW.namespace || '/' || NEW.id END, 'op', TG_OP)::text);
                    RETURN NEW;
                END IF;
            END;
            $$ LANGUAGE plpgsql;

            DO $$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'mesh_node_notify') THEN
                    CREATE TRIGGER mesh_node_notify
                        AFTER INSERT OR UPDATE OR DELETE ON mesh_nodes
                        FOR EACH ROW EXECUTE FUNCTION notify_mesh_node_changes();
                END IF;
            END;
            $$;

            -- Mirror access objects (User / Group / Role / VUser / ApiToken)
            -- into the global "auth" schema so consumers (UserIdentityCache,
            -- group resolution, role lookup, token validation) can do a
            -- single-schema lookup rather than fan a synced query across
            -- every per-user partition. Function is installed by
            -- V27_RenameUserSchemaToAuthAndMirrorApiTokens in public;
            -- this trigger wires the per-partition mesh_nodes table to it.
            -- We skip the trigger entirely if the function doesn't exist yet
            -- (fresh-DB ordering -- migration runs after init) and also skip
            -- on the "auth" schema itself (it's the mirror target).
            DO $$
            BEGIN
                -- Schema-scoped install: DROP IF EXISTS (the CURRENT schema's mesh_nodes
                -- only, resolved via search_path) then CREATE. The previous global guard
                -- `NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = ...)` was wrong: once
                -- ANY schema had the trigger, every later partition's install was skipped, so
                -- only the first-initialised schema ever mirrored. Skip only when the mirror
                -- function is absent (fresh-DB ordering) or on the "auth" schema itself
                -- (it's the mirror target, not a source).
                IF EXISTS (SELECT 1 FROM pg_proc WHERE proname = 'mirror_access_object_to_auth_schema')
                   AND current_schema() <> 'auth' THEN
                    DROP TRIGGER IF EXISTS mesh_node_mirror_access_objects ON mesh_nodes;
                    CREATE TRIGGER mesh_node_mirror_access_objects
                        AFTER INSERT OR UPDATE OR DELETE ON mesh_nodes
                        FOR EACH ROW EXECUTE FUNCTION public.mirror_access_object_to_auth_schema();
                END IF;
            END;
            $$;

            -- Trigger to copy all rows to history in the versions schema
            CREATE OR REPLACE FUNCTION trg_mesh_node_to_history() RETURNS TRIGGER AS $$
            BEGIN
                BEGIN
                    EXECUTE format(
                        'INSERT INTO %I.mesh_node_history (
                            namespace, id, name, node_type, description, category, icon,
                            display_order, last_modified, version, state, content, desired_id, main_node
                        ) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14)',
                        '{{versionsSchema}}'
                    ) USING
                        NEW.namespace, NEW.id, NEW.name, NEW.node_type, NEW.description,
                        NEW.category, NEW.icon, NEW.display_order, NEW.last_modified,
                        NEW.version, NEW.state, NEW.content, NEW.desired_id, NEW.main_node;
                EXCEPTION WHEN unique_violation THEN
                    -- Already exists, skip
                END;
                RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;

            DO $$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'mesh_node_copy_to_history') THEN
                    CREATE TRIGGER mesh_node_copy_to_history
                        AFTER INSERT OR UPDATE ON mesh_nodes
                        FOR EACH ROW EXECUTE FUNCTION trg_mesh_node_to_history();
                END IF;
            END;
            $$;
            """;
    }

    private static string GetSchemaScript(PostgreSqlStorageOptions options)
    {
        var dim = options.VectorDimensions;
        var schemaName = options.Schema ?? "public";
        // Existing callers (public bootstrap + per-schema NpgsqlDataSource init) hardcode
        // the schema name as a SQL string literal — the schema is known at C# build time.
        return GetVersionedPartitionDdl(dim, $"'{schemaName}'");
    }

    /// <summary>
    /// Sentinel that <see cref="GetEnsurePartitionSchemaProcScript"/> substitutes for the
    /// real partition name at proc-runtime via plain <c>replace()</c> (NOT <c>format()</c> —
    /// the DDL body contains <c>%I</c>/<c>%L</c> inside its own <c>format()</c> calls that
    /// must survive untouched). Never appears in a real schema name.
    /// </summary>
    internal const string PartitionNameSentinel = "__mw_partition__";

    /// <summary>
    /// The full versioned-partition DDL (mesh_nodes + support tables + the access
    /// satellite + permission-rebuild functions + notify/mirror/history triggers).
    /// Unqualified — resolves against the connection's <c>search_path</c>.
    ///
    /// <para><paramref name="schemaRef"/> is the SQL <i>literal/expression</i> spliced in
    /// where the rebuild functions <c>SET LOCAL search_path</c> and the
    /// <c>public.partition_access</c> sync reference the owning schema. Two callers:
    /// <list type="bullet">
    ///   <item><see cref="GetSchemaScript"/> passes a quoted literal
    ///         (<c>'rbuergi'</c>) — the schema is fixed at C# build time.</item>
    ///   <item><see cref="GetEnsurePartitionSchemaProcScript"/> passes the quoted
    ///         <see cref="PartitionNameSentinel"/> (<c>'__mw_partition__'</c>); the proc
    ///         <c>replace()</c>s the sentinel with the real partition name before
    ///         <c>EXECUTE</c>, yielding the same <c>'rbuergi'</c> literal at runtime.</item>
    /// </list>
    /// Both produce byte-identical table/index/PK/trigger DDL; only the schema
    /// self-reference differs.</para>
    /// </summary>
    internal static string GetVersionedPartitionDdl(int dim, string schemaRef)
    {
        return $$"""
            CREATE EXTENSION IF NOT EXISTS vector;

            -- mesh_nodes
            CREATE TABLE IF NOT EXISTS mesh_nodes (
                namespace       TEXT        NOT NULL DEFAULT '',
                id              TEXT        NOT NULL,
                path            TEXT        GENERATED ALWAYS AS (
                                    CASE WHEN namespace = '' THEN id ELSE namespace || '/' || id END
                                ) STORED,
                name            TEXT,
                node_type       TEXT,
                description     TEXT,
                category        TEXT,
                icon            TEXT,
                display_order   INTEGER,
                last_modified   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                version         BIGINT      NOT NULL DEFAULT 0,
                state           SMALLINT    NOT NULL DEFAULT 0,
                -- node-level static-repo sync claim (0=Include, 1=ExcludeThisOnly,
                -- 2=ExcludeThisAndChildren). Persists the "Not synced" decouple so an
                -- admin-claimed node/partition survives restart + the next import.
                sync_behavior   SMALLINT    NOT NULL DEFAULT 0,
                content         JSONB,
                desired_id      TEXT,
                main_node       TEXT,
                embedding       vector({{dim}}),
                PRIMARY KEY (namespace, id)
            );

            CREATE INDEX IF NOT EXISTS idx_mn_path ON mesh_nodes (path);
            CREATE INDEX IF NOT EXISTS idx_mn_path_prefix ON mesh_nodes (path text_pattern_ops);
            CREATE INDEX IF NOT EXISTS idx_mn_namespace ON mesh_nodes (namespace);
            CREATE INDEX IF NOT EXISTS idx_mn_node_type ON mesh_nodes (node_type);
            -- Functional indexes for case-insensitive equality: the SQL generator
            -- emits `LOWER(n.namespace) = $1` / `LOWER(n.node_type) = $1` for every
            -- text-field equality (PostgreSqlSqlGenerator.GenerateComparisonClause
            -- case-folds via ToLowerInvariant). Without the LOWER() expression
            -- indexes, Postgres falls back to sequential scan because the plain
            -- (namespace) / (node_type) indexes don't match the function
            -- expression. Add them alongside (not in place of) so any future
            -- case-sensitive query path still has support.
            CREATE INDEX IF NOT EXISTS idx_mn_namespace_lower ON mesh_nodes (LOWER(namespace));
            CREATE INDEX IF NOT EXISTS idx_mn_node_type_lower ON mesh_nodes (LOWER(node_type));
            CREATE INDEX IF NOT EXISTS idx_mn_path_lower ON mesh_nodes (LOWER(path));
            CREATE INDEX IF NOT EXISTS idx_mn_content ON mesh_nodes USING gin (content jsonb_path_ops);
            CREATE INDEX IF NOT EXISTS idx_mn_text_search ON mesh_nodes USING gin (
                to_tsvector('english', COALESCE(name,'') || ' ' || COALESCE(description,'') || ' ' || COALESCE(node_type,''))
            );
            CREATE INDEX IF NOT EXISTS idx_mn_embedding ON mesh_nodes USING hnsw (embedding vector_cosine_ops);

            -- Migrate embedding column if dimensions changed
            DO $migrate$
            DECLARE cur_dim INT;
            BEGIN
                SELECT atttypmod INTO cur_dim FROM pg_attribute
                WHERE attrelid = 'mesh_nodes'::regclass AND attname = 'embedding' AND atttypmod > 0;
                IF cur_dim IS NOT NULL AND cur_dim != {{dim}} THEN
                    DROP INDEX IF EXISTS idx_mn_embedding;
                    ALTER TABLE mesh_nodes ALTER COLUMN embedding TYPE vector({{dim}}) USING NULL;
                    CREATE INDEX idx_mn_embedding ON mesh_nodes USING hnsw (embedding vector_cosine_ops);
                END IF;
            END $migrate$;

            -- partition_objects
            CREATE TABLE IF NOT EXISTS partition_objects (
                id              TEXT        NOT NULL,
                partition_key   TEXT        NOT NULL,
                type_name       TEXT,
                data            JSONB       NOT NULL,
                last_modified   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                PRIMARY KEY (partition_key, id)
            );

            -- user_activity
            CREATE TABLE IF NOT EXISTS user_activity (
                user_id         TEXT        NOT NULL,
                node_path       TEXT        NOT NULL,
                activity_type   SMALLINT    NOT NULL DEFAULT 0,
                first_accessed  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                last_accessed   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                access_count    INTEGER     NOT NULL DEFAULT 1,
                node_name       TEXT,
                node_type       TEXT,
                namespace       TEXT,
                PRIMARY KEY (user_id, node_path)
            );
            CREATE INDEX IF NOT EXISTS idx_ua_user_last ON user_activity (user_id, last_accessed DESC);
            CREATE INDEX IF NOT EXISTS idx_ua_node_type ON user_activity (user_id, node_type);

            -- change_logs (bundled activity logs)
            CREATE TABLE IF NOT EXISTS change_logs (
                id              TEXT        NOT NULL PRIMARY KEY,
                hub_path        TEXT        NOT NULL,
                changed_by      TEXT,
                category        TEXT        NOT NULL,
                start_time      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                end_time        TIMESTAMPTZ,
                change_count    INTEGER     NOT NULL DEFAULT 0,
                status          SMALLINT    NOT NULL DEFAULT 1,
                messages        JSONB
            );
            CREATE INDEX IF NOT EXISTS idx_cl_hub ON change_logs (hub_path, start_time DESC);
            CREATE INDEX IF NOT EXISTS idx_cl_user ON change_logs (changed_by, start_time DESC);

            -- user_effective_permissions (denormalized)
            CREATE TABLE IF NOT EXISTS user_effective_permissions (
                user_id          TEXT     NOT NULL,
                node_path_prefix TEXT     NOT NULL,
                permission       TEXT     NOT NULL,
                is_allow         BOOLEAN  NOT NULL,
                PRIMARY KEY (user_id, node_path_prefix, permission)
            );
            CREATE INDEX IF NOT EXISTS idx_uep_user_perm ON user_effective_permissions (user_id, permission);
            CREATE INDEX IF NOT EXISTS idx_uep_path ON user_effective_permissions (node_path_prefix text_pattern_ops);

            -- Shadow table for atomic rebuild
            CREATE TABLE IF NOT EXISTS user_effective_permissions_shadow (LIKE user_effective_permissions INCLUDING ALL);

            -- Rebuild function: reads AccessAssignment from access satellite table, GroupMembership from mesh_nodes
            -- AccessAssignment content: {"accessObject":"...","roles":[{"role":"...","denied":true},...]}
            CREATE OR REPLACE FUNCTION rebuild_user_effective_permissions() RETURNS void AS $$
            BEGIN
                -- Set search_path so unqualified table names resolve to this schema
                EXECUTE format('SET LOCAL search_path TO %I, public', {{schemaRef}});
                TRUNCATE user_effective_permissions_shadow;

                -- Direct entries from AccessAssignment nodes (access satellite table)
                -- Unnest the roles[] array to get each role assignment
                INSERT INTO user_effective_permissions_shadow (user_id, node_path_prefix, permission, is_allow)
                SELECT
                    aa.content->>'accessObject' AS user_id,
                    COALESCE(aa.main_node, aa.namespace) AS node_path_prefix,
                    perm.permission,
                    NOT COALESCE((role_entry->>'denied')::boolean, false) AS is_allow
                FROM access aa
                CROSS JOIN LATERAL jsonb_array_elements(aa.content->'roles') AS role_entry
                CROSS JOIN LATERAL (
                    SELECT COALESCE(
                        (SELECT (role_node.content->>'permissions')::int
                         FROM mesh_nodes role_node
                         WHERE role_node.node_type = 'Role'
                           AND role_node.id = role_entry->>'role'
                         LIMIT 1),
                        -- Fallback: built-in role lookup
                        CASE role_entry->>'role'
                            WHEN 'Admin' THEN 1535
                            WHEN 'PlatformAdmin' THEN 1535
                            WHEN 'Editor' THEN 1527
                            WHEN 'Viewer' THEN 161
                            WHEN 'Commenter' THEN 145
                            ELSE 0
                        END
                    ) AS permissions
                ) r
                CROSS JOIN LATERAL (
                    SELECT unnest(
                        CASE WHEN (r.permissions & 1) > 0 THEN ARRAY['Read'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 2) > 0 THEN ARRAY['Create'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 4) > 0 THEN ARRAY['Update'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 8) > 0 THEN ARRAY['Delete'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 16) > 0 THEN ARRAY['Comment'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 32) > 0 THEN ARRAY['Execute'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 64) > 0 THEN ARRAY['Thread'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 128) > 0 THEN ARRAY['Api'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 256) > 0 THEN ARRAY['Export'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 1024) > 0 THEN ARRAY['Compile'] ELSE ARRAY[]::text[] END
                    ) AS permission
                ) perm
                WHERE aa.content->>'accessObject' IS NOT NULL
                  AND aa.content->'roles' IS NOT NULL
                ON CONFLICT (user_id, node_path_prefix, permission) DO UPDATE
                    SET is_allow = CASE WHEN EXCLUDED.is_allow = false THEN false ELSE user_effective_permissions_shadow.is_allow END;

                -- Group expansion: read GroupMembership MeshNodes
                -- New model: content has "member" + "groups" array of {"group":"..."}
                INSERT INTO user_effective_permissions_shadow (user_id, node_path_prefix, permission, is_allow)
                WITH RECURSIVE all_members AS (
                    SELECT group_entry->>'group' AS group_path, gm.content->>'member' AS member_id
                    FROM "auth".mesh_nodes gm
                    CROSS JOIN LATERAL jsonb_array_elements(gm.content->'groups') AS group_entry
                    WHERE gm.node_type = 'GroupMembership'
                    UNION
                    SELECT am.group_path, gm.content->>'member'
                    FROM all_members am
                    JOIN "auth".mesh_nodes gm ON gm.node_type = 'GroupMembership'
                    WHERE EXISTS (
                        SELECT 1 FROM jsonb_array_elements(gm.content->'groups') g
                        WHERE g->>'group' = am.member_id
                    )
                ),
                leaf_members AS (
                    SELECT group_path, member_id FROM all_members
                    WHERE NOT EXISTS (
                        SELECT 1 FROM "auth".mesh_nodes gm2
                        CROSS JOIN LATERAL jsonb_array_elements(gm2.content->'groups') AS g2
                        WHERE gm2.node_type = 'GroupMembership'
                          AND g2->>'group' = all_members.member_id
                    )
                )
                SELECT lm.member_id, COALESCE(aa.main_node, aa.namespace), perm.permission,
                       NOT COALESCE((role_entry->>'denied')::boolean, false) AS is_allow
                FROM access aa
                JOIN leaf_members lm ON aa.content->>'accessObject' = lm.group_path
                CROSS JOIN LATERAL jsonb_array_elements(aa.content->'roles') AS role_entry
                CROSS JOIN LATERAL (
                    SELECT COALESCE(
                        (SELECT (role_node.content->>'permissions')::int
                         FROM mesh_nodes role_node
                         WHERE role_node.node_type = 'Role'
                           AND role_node.id = role_entry->>'role'
                         LIMIT 1),
                        CASE role_entry->>'role'
                            WHEN 'Admin' THEN 1535
                            WHEN 'PlatformAdmin' THEN 1535
                            WHEN 'Editor' THEN 1527
                            WHEN 'Viewer' THEN 161
                            WHEN 'Commenter' THEN 145
                            ELSE 0
                        END
                    ) AS permissions
                ) r
                CROSS JOIN LATERAL (
                    SELECT unnest(
                        CASE WHEN (r.permissions & 1) > 0 THEN ARRAY['Read'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 2) > 0 THEN ARRAY['Create'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 4) > 0 THEN ARRAY['Update'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 8) > 0 THEN ARRAY['Delete'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 16) > 0 THEN ARRAY['Comment'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 32) > 0 THEN ARRAY['Execute'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 64) > 0 THEN ARRAY['Thread'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 128) > 0 THEN ARRAY['Api'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 256) > 0 THEN ARRAY['Export'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 1024) > 0 THEN ARRAY['Compile'] ELSE ARRAY[]::text[] END
                    ) AS permission
                ) perm
                WHERE aa.content->'roles' IS NOT NULL
                ON CONFLICT (user_id, node_path_prefix, permission) DO UPDATE
                    SET is_allow = CASE WHEN EXCLUDED.is_allow = false THEN false ELSE user_effective_permissions_shadow.is_allow END;

                -- Direct entries from access_control table (convenience methods)
                INSERT INTO user_effective_permissions_shadow (user_id, node_path_prefix, permission, is_allow)
                SELECT subject, node_path, permission, is_allow
                FROM access_control
                ON CONFLICT (user_id, node_path_prefix, permission) DO UPDATE
                    SET is_allow = CASE WHEN EXCLUDED.is_allow = false THEN false ELSE user_effective_permissions_shadow.is_allow END;

                -- Group expansion from group_members + access_control
                INSERT INTO user_effective_permissions_shadow (user_id, node_path_prefix, permission, is_allow)
                WITH RECURSIVE all_members AS (
                    SELECT group_name, member_id FROM group_members
                    UNION
                    SELECT am.group_name, gm.member_id
                    FROM all_members am
                    JOIN group_members gm ON gm.group_name = am.member_id
                ),
                leaf_members AS (
                    SELECT group_name, member_id FROM all_members
                    WHERE member_id NOT IN (SELECT DISTINCT group_name FROM group_members)
                )
                SELECT lm.member_id, ac.node_path, ac.permission, ac.is_allow
                FROM access_control ac
                JOIN leaf_members lm ON lm.group_name = ac.subject
                ON CONFLICT (user_id, node_path_prefix, permission) DO UPDATE
                    SET is_allow = CASE WHEN EXCLUDED.is_allow = false THEN false ELSE user_effective_permissions_shadow.is_allow END;

                -- Apply PartitionAccessPolicy caps: deny permissions set to false
                -- For each policy, insert deny rows at the policy namespace for ALL users
                -- for permissions that the policy explicitly denies (field value = false).
                -- The most-specific-prefix-wins query logic then correctly denies those permissions.
                INSERT INTO user_effective_permissions_shadow (user_id, node_path_prefix, permission, is_allow)
                SELECT DISTINCT
                    uep.user_id,
                    policy.namespace AS node_path_prefix,
                    perm.permission,
                    false
                FROM mesh_nodes policy
                CROSS JOIN (SELECT DISTINCT user_id FROM user_effective_permissions_shadow) uep
                CROSS JOIN (
                    SELECT unnest(ARRAY['Read','Create','Update','Delete','Comment']) AS permission,
                           unnest(ARRAY['read','create','update','delete','comment']) AS field
                ) perm
                WHERE policy.node_type = 'PartitionAccessPolicy'
                  AND policy.id = '_Policy'
                  AND (policy.content->>perm.field)::boolean = false
                ON CONFLICT (user_id, node_path_prefix, permission) DO UPDATE SET is_allow = false;

                -- Atomic swap
                ALTER TABLE user_effective_permissions RENAME TO user_effective_permissions_old;
                ALTER TABLE user_effective_permissions_shadow RENAME TO user_effective_permissions;
                ALTER TABLE user_effective_permissions_old RENAME TO user_effective_permissions_shadow;

                -- Sync partition_access: upsert users with Read permission, remove revoked
                BEGIN
                    INSERT INTO public.partition_access (user_id, partition)
                    SELECT DISTINCT user_id, {{schemaRef}}
                    FROM user_effective_permissions
                    WHERE permission = 'Read' AND is_allow = true
                    ON CONFLICT (user_id, partition) DO NOTHING;

                    DELETE FROM public.partition_access
                    WHERE partition = {{schemaRef}}
                      AND user_id NOT IN (
                        SELECT user_id FROM user_effective_permissions
                        WHERE permission = 'Read' AND is_allow = true
                      );
                EXCEPTION WHEN undefined_table THEN
                    -- partition_access table may not exist yet (first migration)
                    NULL;
                END;
            END;
            $$ LANGUAGE plpgsql;

            -- Access satellite table (must exist before trigger creation)
            CREATE TABLE IF NOT EXISTS access (
                namespace       TEXT        NOT NULL DEFAULT '',
                id              TEXT        NOT NULL,
                path            TEXT        GENERATED ALWAYS AS (
                                    CASE WHEN namespace = '' THEN id ELSE namespace || '/' || id END
                                ) STORED,
                name            TEXT,
                node_type       TEXT,
                description     TEXT,
                category        TEXT,
                icon            TEXT,
                display_order   INTEGER,
                last_modified   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                version         BIGINT      NOT NULL DEFAULT 0,
                state           SMALLINT    NOT NULL DEFAULT 0,
                -- node-level static-repo sync claim (0=Include, 1=ExcludeThisOnly,
                -- 2=ExcludeThisAndChildren). Persists the "Not synced" decouple so an
                -- admin-claimed node/partition survives restart + the next import.
                sync_behavior   SMALLINT    NOT NULL DEFAULT 0,
                content         JSONB,
                desired_id      TEXT,
                main_node       TEXT,
                embedding       vector({{dim}}),
                PRIMARY KEY (namespace, id)
            );
            CREATE INDEX IF NOT EXISTS idx_access_path ON access (path);
            CREATE INDEX IF NOT EXISTS idx_access_main_node ON access (main_node);
            CREATE INDEX IF NOT EXISTS idx_access_node_type ON access (node_type);
            CREATE INDEX IF NOT EXISTS idx_access_last_modified ON access (last_modified DESC);
            -- Functional LOWER() indexes — SQL generator case-folds text equality.
            CREATE INDEX IF NOT EXISTS idx_access_namespace_lower ON access (LOWER(namespace));
            CREATE INDEX IF NOT EXISTS idx_access_node_type_lower ON access (LOWER(node_type));
            CREATE INDEX IF NOT EXISTS idx_access_main_node_lower ON access (LOWER(main_node));

            -- Per-user permission rebuild: concurrent-safe, only touches one user's rows.
            CREATE OR REPLACE FUNCTION rebuild_user_permissions_for(p_user_id TEXT) RETURNS void AS $$
            BEGIN
                EXECUTE format('SET LOCAL search_path TO %I, public', {{schemaRef}});
                DELETE FROM user_effective_permissions WHERE user_id = p_user_id;

                -- Direct entries from AccessAssignment nodes for this user
                INSERT INTO user_effective_permissions (user_id, node_path_prefix, permission, is_allow)
                SELECT
                    p_user_id,
                    COALESCE(aa.main_node, aa.namespace),
                    perm.permission,
                    NOT COALESCE((role_entry->>'denied')::boolean, false)
                FROM access aa
                CROSS JOIN LATERAL jsonb_array_elements(aa.content->'roles') AS role_entry
                CROSS JOIN LATERAL (
                    SELECT COALESCE(
                        (SELECT (rn.content->>'permissions')::int FROM mesh_nodes rn
                         WHERE rn.node_type = 'Role' AND rn.id = role_entry->>'role' LIMIT 1),
                        CASE role_entry->>'role'
                            WHEN 'Admin' THEN 1535 WHEN 'PlatformAdmin' THEN 1535
                            WHEN 'Editor' THEN 1527 WHEN 'Viewer' THEN 161
                            WHEN 'Commenter' THEN 145 ELSE 0
                        END
                    ) AS permissions
                ) r
                CROSS JOIN LATERAL (
                    SELECT unnest(
                        CASE WHEN (r.permissions & 1) > 0 THEN ARRAY['Read'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 2) > 0 THEN ARRAY['Create'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 4) > 0 THEN ARRAY['Update'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 8) > 0 THEN ARRAY['Delete'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 16) > 0 THEN ARRAY['Comment'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 32) > 0 THEN ARRAY['Execute'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 64) > 0 THEN ARRAY['Thread'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 128) > 0 THEN ARRAY['Api'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 256) > 0 THEN ARRAY['Export'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 1024) > 0 THEN ARRAY['Compile'] ELSE ARRAY[]::text[] END
                    ) AS permission
                ) perm
                WHERE aa.content->>'accessObject' = p_user_id
                  AND aa.content->'roles' IS NOT NULL
                ON CONFLICT (user_id, node_path_prefix, permission) DO UPDATE
                    SET is_allow = CASE WHEN EXCLUDED.is_allow = false THEN false ELSE user_effective_permissions.is_allow END;

                -- Group expansion for this user
                INSERT INTO user_effective_permissions (user_id, node_path_prefix, permission, is_allow)
                WITH RECURSIVE all_members AS (
                    SELECT group_entry->>'group' AS group_path, gm.content->>'member' AS member_id
                    FROM "auth".mesh_nodes gm
                    CROSS JOIN LATERAL jsonb_array_elements(gm.content->'groups') AS group_entry
                    WHERE gm.node_type = 'GroupMembership'
                    UNION
                    SELECT am.group_path, gm.content->>'member'
                    FROM all_members am
                    JOIN "auth".mesh_nodes gm ON gm.node_type = 'GroupMembership'
                    WHERE EXISTS (SELECT 1 FROM jsonb_array_elements(gm.content->'groups') g WHERE g->>'group' = am.member_id)
                ),
                user_groups AS (
                    SELECT DISTINCT group_path FROM all_members WHERE member_id = p_user_id
                )
                SELECT p_user_id, COALESCE(aa.main_node, aa.namespace), perm.permission,
                       NOT COALESCE((role_entry->>'denied')::boolean, false)
                FROM access aa
                JOIN user_groups ug ON aa.content->>'accessObject' = ug.group_path
                CROSS JOIN LATERAL jsonb_array_elements(aa.content->'roles') AS role_entry
                CROSS JOIN LATERAL (
                    SELECT COALESCE(
                        (SELECT (rn.content->>'permissions')::int FROM mesh_nodes rn
                         WHERE rn.node_type = 'Role' AND rn.id = role_entry->>'role' LIMIT 1),
                        CASE role_entry->>'role'
                            WHEN 'Admin' THEN 1535 WHEN 'PlatformAdmin' THEN 1535
                            WHEN 'Editor' THEN 1527 WHEN 'Viewer' THEN 161
                            WHEN 'Commenter' THEN 145 ELSE 0
                        END
                    ) AS permissions
                ) r
                CROSS JOIN LATERAL (
                    SELECT unnest(
                        CASE WHEN (r.permissions & 1) > 0 THEN ARRAY['Read'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 2) > 0 THEN ARRAY['Create'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 4) > 0 THEN ARRAY['Update'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 8) > 0 THEN ARRAY['Delete'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 16) > 0 THEN ARRAY['Comment'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 32) > 0 THEN ARRAY['Execute'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 64) > 0 THEN ARRAY['Thread'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 128) > 0 THEN ARRAY['Api'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 256) > 0 THEN ARRAY['Export'] ELSE ARRAY[]::text[] END
                        || CASE WHEN (r.permissions & 1024) > 0 THEN ARRAY['Compile'] ELSE ARRAY[]::text[] END
                    ) AS permission
                ) perm
                WHERE aa.content->'roles' IS NOT NULL
                ON CONFLICT (user_id, node_path_prefix, permission) DO UPDATE
                    SET is_allow = CASE WHEN EXCLUDED.is_allow = false THEN false ELSE user_effective_permissions.is_allow END;

                -- access_control entries for this user
                INSERT INTO user_effective_permissions (user_id, node_path_prefix, permission, is_allow)
                SELECT subject, node_path, permission, is_allow FROM access_control WHERE subject = p_user_id
                ON CONFLICT (user_id, node_path_prefix, permission) DO UPDATE
                    SET is_allow = CASE WHEN EXCLUDED.is_allow = false THEN false ELSE user_effective_permissions.is_allow END;

                -- PartitionAccessPolicy caps for this user
                INSERT INTO user_effective_permissions (user_id, node_path_prefix, permission, is_allow)
                SELECT DISTINCT p_user_id, policy.namespace, perm.permission, false
                FROM mesh_nodes policy
                CROSS JOIN (
                    SELECT unnest(ARRAY['Read','Create','Update','Delete','Comment']) AS permission,
                           unnest(ARRAY['read','create','update','delete','comment']) AS field
                ) perm
                WHERE policy.node_type = 'PartitionAccessPolicy' AND policy.id = '_Policy'
                  AND (policy.content->>perm.field)::boolean = false
                ON CONFLICT (user_id, node_path_prefix, permission) DO UPDATE SET is_allow = false;

                -- Sync partition_access for this user
                BEGIN
                    IF EXISTS (SELECT 1 FROM user_effective_permissions
                               WHERE user_id = p_user_id AND permission = 'Read' AND is_allow = true) THEN
                        INSERT INTO public.partition_access (user_id, partition)
                        VALUES (p_user_id, {{schemaRef}}) ON CONFLICT DO NOTHING;
                    ELSE
                        DELETE FROM public.partition_access
                        WHERE user_id = p_user_id AND partition = {{schemaRef}};
                    END IF;
                EXCEPTION WHEN undefined_table THEN NULL;
                END;
            END;
            $$ LANGUAGE plpgsql;

            -- Trigger function: per-row, rebuilds only the affected user's permissions.
            -- Body single-sourced from AccessChangedTriggerFunctionBody: every rebuild call is
            -- schema-qualified via TG_TABLE_SCHEMA — see the constant's doc for the 2026-07-13
            -- wrong-schema incident an unqualified PERFORM causes on the shared base pool.
            CREATE OR REPLACE FUNCTION trg_access_changed() RETURNS TRIGGER AS $trg_access$
            {{AccessChangedTriggerFunctionBody}}
            $trg_access$ LANGUAGE plpgsql;

            -- Drop old trigger on mesh_nodes if it exists
            DROP TRIGGER IF EXISTS mesh_node_access_changed ON mesh_nodes;

            -- Trigger on access table (per-row for concurrent safety)
            DROP TRIGGER IF EXISTS access_changed ON access;
            CREATE TRIGGER access_changed
                AFTER INSERT OR UPDATE OR DELETE ON access
                FOR EACH ROW EXECUTE FUNCTION trg_access_changed();

            -- Group/GroupMembership recompute is NOT installed per-partition: it is driven from the
            -- global auth mirror (see GetAuthMirrorSelfHealScript / trg_group_changed) so a group
            -- defined in one partition can be licensed on a course in another. A per-partition copy
            -- would double-fire (the change mirrors to auth) and couldn't see cross-partition members.

            -- Simple access_control and group_members tables used by convenience methods
            CREATE TABLE IF NOT EXISTS access_control (
                node_path   TEXT    NOT NULL,
                subject     TEXT    NOT NULL,
                permission  TEXT    NOT NULL,
                is_allow    BOOLEAN NOT NULL,
                PRIMARY KEY (node_path, subject, permission)
            );

            CREATE TABLE IF NOT EXISTS group_members (
                group_name  TEXT    NOT NULL,
                member_id   TEXT    NOT NULL,
                PRIMARY KEY (group_name, member_id)
            );

            -- Node type permission flags (populated from DI-registered NodeTypePermission records)
            CREATE TABLE IF NOT EXISTS node_type_permissions (
                node_type   TEXT    NOT NULL PRIMARY KEY,
                public_read BOOLEAN NOT NULL DEFAULT false
            );

            -- Drop legacy triggers if they exist (tables are now reused)
            DROP TRIGGER IF EXISTS access_control_changed ON access_control;
            DROP TRIGGER IF EXISTS group_members_changed ON group_members;
            DROP FUNCTION IF EXISTS trg_access_control_changed();

            -- Notify function for change notifications
            -- pg_notify dedup: suppress UPDATE that doesn't change any reactive
            -- consumer-visible field. Without this, idempotent writes (e.g. a
            -- workspace.Update lambda that returns the same node, or a same-
            -- value upsert on a write-heavy code path) fire NOTIFY → every
            -- synced-query subscriber wakes up → every subscriber re-reads →
            -- amplification feedback loop. The check uses IS NOT DISTINCT FROM
            -- so NULL is value-equal to NULL.  Version equality alone isn't
            -- enough: an upsert with the same Version but a fresher
            -- last_modified shouldn't fire either (no observable change).
            -- Prod incident 2026-05-20.
            CREATE OR REPLACE FUNCTION notify_mesh_node_changes() RETURNS TRIGGER AS $$
            BEGIN
                IF TG_OP = 'DELETE' THEN
                    PERFORM pg_notify('mesh_node_changes',
                        json_build_object('path', CASE WHEN OLD.namespace = '' THEN OLD.id ELSE OLD.namespace || '/' || OLD.id END, 'op', 'DELETE')::text);
                    RETURN OLD;
                ELSIF TG_OP = 'UPDATE'
                      AND OLD.content IS NOT DISTINCT FROM NEW.content
                      AND OLD.name IS NOT DISTINCT FROM NEW.name
                      AND OLD.node_type IS NOT DISTINCT FROM NEW.node_type
                      AND OLD.state IS NOT DISTINCT FROM NEW.state
                      AND OLD.version IS NOT DISTINCT FROM NEW.version
                      AND OLD.desired_id IS NOT DISTINCT FROM NEW.desired_id
                      AND OLD.main_node IS NOT DISTINCT FROM NEW.main_node THEN
                    -- No consumer-visible change. Skip pg_notify; the row write
                    -- still happens (we RETURN NEW so the UPDATE commits), but
                    -- no subscriber is woken.
                    RETURN NEW;
                ELSE
                    PERFORM pg_notify('mesh_node_changes',
                        json_build_object('path', CASE WHEN NEW.namespace = '' THEN NEW.id ELSE NEW.namespace || '/' || NEW.id END, 'op', TG_OP)::text);
                    RETURN NEW;
                END IF;
            END;
            $$ LANGUAGE plpgsql;

            DO $$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'mesh_node_notify') THEN
                    CREATE TRIGGER mesh_node_notify
                        AFTER INSERT OR UPDATE OR DELETE ON mesh_nodes
                        FOR EACH ROW EXECUTE FUNCTION notify_mesh_node_changes();
                END IF;
            END;
            $$;

            -- Mirror access objects (User / Group / Role / VUser / ApiToken)
            -- into the global "auth" schema so consumers (UserIdentityCache,
            -- group resolution, role lookup, token validation) can do a
            -- single-schema lookup rather than fan a synced query across
            -- every per-user partition. Function is installed by
            -- V27_RenameUserSchemaToAuthAndMirrorApiTokens in public;
            -- this trigger wires the per-partition mesh_nodes table to it.
            -- We skip the trigger entirely if the function doesn't exist yet
            -- (fresh-DB ordering -- migration runs after init) and also skip
            -- on the "auth" schema itself (it's the mirror target).
            DO $$
            BEGIN
                -- Schema-scoped install: DROP IF EXISTS (the CURRENT schema's mesh_nodes
                -- only, resolved via search_path) then CREATE. The previous global guard
                -- `NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = ...)` was wrong: once
                -- ANY schema had the trigger, every later partition's install was skipped, so
                -- only the first-initialised schema ever mirrored. Skip only when the mirror
                -- function is absent (fresh-DB ordering) or on the "auth" schema itself
                -- (it's the mirror target, not a source).
                IF EXISTS (SELECT 1 FROM pg_proc WHERE proname = 'mirror_access_object_to_auth_schema')
                   AND current_schema() <> 'auth' THEN
                    DROP TRIGGER IF EXISTS mesh_node_mirror_access_objects ON mesh_nodes;
                    CREATE TRIGGER mesh_node_mirror_access_objects
                        AFTER INSERT OR UPDATE OR DELETE ON mesh_nodes
                        FOR EACH ROW EXECUTE FUNCTION public.mirror_access_object_to_auth_schema();
                END IF;
            END;
            $$;

            -- mesh_node_history: versioned copies of mesh_nodes
            CREATE TABLE IF NOT EXISTS mesh_node_history (
                namespace       TEXT        NOT NULL DEFAULT '',
                id              TEXT        NOT NULL,
                path            TEXT        GENERATED ALWAYS AS (
                                    CASE WHEN namespace = '' THEN id ELSE namespace || '/' || id END
                                ) STORED,
                name            TEXT,
                node_type       TEXT,
                description     TEXT,
                category        TEXT,
                icon            TEXT,
                display_order   INTEGER,
                last_modified   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                version         BIGINT      NOT NULL DEFAULT 0,
                state           SMALLINT    NOT NULL DEFAULT 0,
                content         JSONB,
                desired_id      TEXT,
                changed_by      TEXT,
                main_node       TEXT,
                PRIMARY KEY (namespace, id, version)
            );

            CREATE INDEX IF NOT EXISTS idx_mnh_path ON mesh_node_history (path);
            CREATE INDEX IF NOT EXISTS idx_mnh_path_version ON mesh_node_history (path, version DESC);

            -- Trigger to copy all rows to history on every insert/update. The INSERT is
            -- schema-qualified via TG_TABLE_SCHEMA so the snapshot lands in the SAME schema
            -- as the mesh_nodes row that fired it — correct no matter what search_path the
            -- writing session has (writes may arrive through the base data source, whose
            -- search_path is `public`, not the partition schema). An unqualified
            -- `INSERT INTO mesh_node_history` would silently land in public (or 42P01).
            CREATE OR REPLACE FUNCTION trg_mesh_node_to_history() RETURNS TRIGGER AS $$
            BEGIN
                EXECUTE format(
                    'INSERT INTO %I.mesh_node_history (
                        namespace, id, name, node_type, description, category, icon,
                        display_order, last_modified, version, state, content, desired_id, main_node
                    ) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14)
                    ON CONFLICT (namespace, id, version) DO NOTHING',
                    TG_TABLE_SCHEMA
                ) USING
                    NEW.namespace, NEW.id, NEW.name, NEW.node_type, NEW.description,
                    NEW.category, NEW.icon, NEW.display_order, NEW.last_modified,
                    NEW.version, NEW.state, NEW.content, NEW.desired_id, NEW.main_node;
                RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;

            -- CREATE OR REPLACE TRIGGER (PG14+) installs the trigger on THIS schema's
            -- mesh_nodes. The prior `DO $$ IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE
            -- tgname='mesh_node_copy_to_history') $$` guard was GLOBALLY scoped — trigger
            -- names are unique per table, not per database — so once `public` had the
            -- trigger every subsequently-provisioned partition skipped it and recorded NO
            -- history at all. That is the "Version History shows nothing" root cause; the
            -- reader (PostgreSqlPartitionedVersionQuery) had no rows to return.
            CREATE OR REPLACE TRIGGER mesh_node_copy_to_history
                AFTER INSERT OR UPDATE ON mesh_nodes
                FOR EACH ROW EXECUTE FUNCTION trg_mesh_node_to_history();
            """;
    }

    /// <summary>
    /// Returns SQL for unversioned partitions: core tables only, no history table or triggers.
    /// </summary>
    private static string GetUnversionedSchemaScript(PostgreSqlStorageOptions options)
    {
        var dim = options.VectorDimensions;
        var schemaName = options.Schema ?? "public";
        return $$"""
            CREATE EXTENSION IF NOT EXISTS vector;

            CREATE TABLE IF NOT EXISTS mesh_nodes (
                namespace       TEXT        NOT NULL DEFAULT '',
                id              TEXT        NOT NULL,
                path            TEXT        GENERATED ALWAYS AS (
                                    CASE WHEN namespace = '' THEN id ELSE namespace || '/' || id END
                                ) STORED,
                name            TEXT,
                node_type       TEXT,
                description     TEXT,
                category        TEXT,
                icon            TEXT,
                display_order   INTEGER,
                last_modified   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                version         BIGINT      NOT NULL DEFAULT 0,
                state           SMALLINT    NOT NULL DEFAULT 0,
                -- node-level static-repo sync claim (0=Include, 1=ExcludeThisOnly,
                -- 2=ExcludeThisAndChildren). Persists the "Not synced" decouple so an
                -- admin-claimed node/partition survives restart + the next import.
                sync_behavior   SMALLINT    NOT NULL DEFAULT 0,
                content         JSONB,
                desired_id      TEXT,
                main_node       TEXT,
                embedding       vector({{dim}}),
                PRIMARY KEY (namespace, id)
            );

            CREATE INDEX IF NOT EXISTS idx_mn_path ON mesh_nodes (path);
            CREATE INDEX IF NOT EXISTS idx_mn_path_prefix ON mesh_nodes (path text_pattern_ops);
            CREATE INDEX IF NOT EXISTS idx_mn_namespace ON mesh_nodes (namespace);
            CREATE INDEX IF NOT EXISTS idx_mn_node_type ON mesh_nodes (node_type);
            CREATE INDEX IF NOT EXISTS idx_mn_content ON mesh_nodes USING gin (content jsonb_path_ops);

            -- Notify function for change notifications
            -- pg_notify dedup: suppress UPDATE that doesn't change any reactive
            -- consumer-visible field. Without this, idempotent writes (e.g. a
            -- workspace.Update lambda that returns the same node, or a same-
            -- value upsert on a write-heavy code path) fire NOTIFY → every
            -- synced-query subscriber wakes up → every subscriber re-reads →
            -- amplification feedback loop. The check uses IS NOT DISTINCT FROM
            -- so NULL is value-equal to NULL.  Version equality alone isn't
            -- enough: an upsert with the same Version but a fresher
            -- last_modified shouldn't fire either (no observable change).
            -- Prod incident 2026-05-20.
            CREATE OR REPLACE FUNCTION notify_mesh_node_changes() RETURNS TRIGGER AS $$
            BEGIN
                IF TG_OP = 'DELETE' THEN
                    PERFORM pg_notify('mesh_node_changes',
                        json_build_object('path', CASE WHEN OLD.namespace = '' THEN OLD.id ELSE OLD.namespace || '/' || OLD.id END, 'op', 'DELETE')::text);
                    RETURN OLD;
                ELSIF TG_OP = 'UPDATE'
                      AND OLD.content IS NOT DISTINCT FROM NEW.content
                      AND OLD.name IS NOT DISTINCT FROM NEW.name
                      AND OLD.node_type IS NOT DISTINCT FROM NEW.node_type
                      AND OLD.state IS NOT DISTINCT FROM NEW.state
                      AND OLD.version IS NOT DISTINCT FROM NEW.version
                      AND OLD.desired_id IS NOT DISTINCT FROM NEW.desired_id
                      AND OLD.main_node IS NOT DISTINCT FROM NEW.main_node THEN
                    -- No consumer-visible change. Skip pg_notify; the row write
                    -- still happens (we RETURN NEW so the UPDATE commits), but
                    -- no subscriber is woken.
                    RETURN NEW;
                ELSE
                    PERFORM pg_notify('mesh_node_changes',
                        json_build_object('path', CASE WHEN NEW.namespace = '' THEN NEW.id ELSE NEW.namespace || '/' || NEW.id END, 'op', TG_OP)::text);
                    RETURN NEW;
                END IF;
            END;
            $$ LANGUAGE plpgsql;

            DO $$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'mesh_node_notify') THEN
                    CREATE TRIGGER mesh_node_notify
                        AFTER INSERT OR UPDATE OR DELETE ON mesh_nodes
                        FOR EACH ROW EXECUTE FUNCTION notify_mesh_node_changes();
                END IF;
            END;
            $$;

            -- Mirror access objects (User / Group / Role / VUser / ApiToken)
            -- into the global "auth" schema so consumers (UserIdentityCache,
            -- group resolution, role lookup, token validation) can do a
            -- single-schema lookup rather than fan a synced query across
            -- every per-user partition. Function is installed by
            -- V27_RenameUserSchemaToAuthAndMirrorApiTokens in public;
            -- this trigger wires the per-partition mesh_nodes table to it.
            -- We skip the trigger entirely if the function doesn't exist yet
            -- (fresh-DB ordering -- migration runs after init) and also skip
            -- on the "auth" schema itself (it's the mirror target).
            DO $$
            BEGIN
                -- Schema-scoped install: DROP IF EXISTS (the CURRENT schema's mesh_nodes
                -- only, resolved via search_path) then CREATE. The previous global guard
                -- `NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = ...)` was wrong: once
                -- ANY schema had the trigger, every later partition's install was skipped, so
                -- only the first-initialised schema ever mirrored. Skip only when the mirror
                -- function is absent (fresh-DB ordering) or on the "auth" schema itself
                -- (it's the mirror target, not a source).
                IF EXISTS (SELECT 1 FROM pg_proc WHERE proname = 'mirror_access_object_to_auth_schema')
                   AND current_schema() <> 'auth' THEN
                    DROP TRIGGER IF EXISTS mesh_node_mirror_access_objects ON mesh_nodes;
                    CREATE TRIGGER mesh_node_mirror_access_objects
                        AFTER INSERT OR UPDATE OR DELETE ON mesh_nodes
                        FOR EACH ROW EXECUTE FUNCTION public.mirror_access_object_to_auth_schema();
                END IF;
            END;
            $$;

            -- Simple access_control and group_members tables used by convenience methods
            CREATE TABLE IF NOT EXISTS access_control (
                node_path   TEXT    NOT NULL,
                subject     TEXT    NOT NULL,
                permission  TEXT    NOT NULL,
                is_allow    BOOLEAN NOT NULL,
                PRIMARY KEY (node_path, subject, permission)
            );

            CREATE TABLE IF NOT EXISTS group_members (
                group_name  TEXT    NOT NULL,
                member_id   TEXT    NOT NULL,
                PRIMARY KEY (group_name, member_id)
            );

            -- Node type permission flags (populated from DI-registered NodeTypePermission records)
            CREATE TABLE IF NOT EXISTS node_type_permissions (
                node_type   TEXT    NOT NULL PRIMARY KEY,
                public_read BOOLEAN NOT NULL DEFAULT false
            );
            """;
    }
}
