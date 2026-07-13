using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;

namespace MeshWeaver.Hosting.Snowflake;

/// <summary>
/// Generates Snowflake SQL queries from the parsed query AST — the Snowflake port of
/// <c>PostgreSqlSqlGenerator</c>, mirroring its public shape member-for-member so the storage
/// adapter / cross-schema provider call sites stay drop-in compatible.
/// <para><b>Dialect decisions</b> (vs the PostgreSQL generator):</para>
/// <list type="bullet">
/// <item><description><b>Identifiers</b>: every schema/table/column reference is double-quoted via
/// <see cref="SnowflakeIdentifiers.Quote"/>/<see cref="SnowflakeIdentifiers.Qualify"/> — Snowflake
/// uppercases unquoted identifiers while the mesh's tables are created with quoted lowercase names.
/// Column ALIASES are quoted too so readers can look ordinals up by the exact lowercase name.</description></item>
/// <item><description><b>Parameters</b>: SQL references bound parameters as <c>:name</c> (the
/// Snowflake.Data named-binding convention, see <see cref="SnowflakeConnectionSource.AddParam"/>);
/// the returned parameter maps use BARE names (no marker prefix) as keys. The marker convention is
/// sealed in the one-line <see cref="Marker"/> helper.</description></item>
/// <item><description><b>JSON content</b>: JSONB accessors <c>content-&gt;&gt;'X'</c> become variant
/// path accessors <c>n."content":"X"::string</c> (nested: <c>n."content":"A"."B"::string</c>).</description></item>
/// <item><description><b>LIKE escaping</b>: Snowflake has NO default LIKE escape character (PG
/// defaults to backslash), so wherever the bound pattern was escaped by
/// <see cref="EscapeLikePattern"/> an explicit <c>ESCAPE '\\'</c> is appended. Patterns PG
/// deliberately does NOT escape (prefix matches built from stored path prefixes, user wildcard
/// patterns, the relevance ladders) keep no ESCAPE — bug-for-bug parity.</description></item>
/// <item><description><b>Numeric casts on variant fields</b>: <c>CAST(x AS numeric)</c> becomes
/// <c>TRY_CAST(x AS NUMBER)</c> — a deliberate difference: garbage content values null out of the
/// comparison instead of failing the whole statement.</description></item>
/// <item><description><b>Vector search</b>: pgvector's <c>embedding &lt;=&gt; @queryVector</c> becomes
/// <c>(1 - VECTOR_COSINE_SIMILARITY("embedding", [..]::VECTOR(FLOAT, N)))</c> with the vector INLINED
/// as an invariant-culture literal (driver-side vector binding is unverified). Numerically identical
/// to <c>&lt;=&gt;</c> cosine distance, so ordering is unchanged.</description></item>
/// <item><description><b>Best-chunk-per-file dedup</b>: PG's <c>SELECT DISTINCT ON (a, b) … ORDER BY
/// a, b, dist</c> becomes <c>QUALIFY ROW_NUMBER() OVER (PARTITION BY a, b ORDER BY …) = 1</c> (or a
/// derived table + <c>WHERE "_rn" = 1</c> when the endpoint lacks QUALIFY).</description></item>
/// <item><description><b>Access control</b>: PG's correlated longest-prefix <c>ORDER BY … LIMIT 1</c>
/// subquery becomes a <c>MAX_BY</c> aggregate (score = <c>LENGTH(prefix) * 2 + IFF(own-row, 1, 0)</c>
/// — longest prefix wins, the caller's own row breaks ties), with an EXISTS(allow) AND NOT
/// EXISTS(stronger deny) pair as the no-MAX_BY fallback.</description></item>
/// <item><description><b>Full-text</b>: the PG stored proc's tsvector constructs are not ported —
/// term matching is ILIKE-based throughout (the PG C# generator path already was); ILIKE itself
/// degrades to <c>LOWER(x) LIKE LOWER(:p)</c> when the endpoint lacks ILIKE.</description></item>
/// <item><description><b>Timestamps</b> are <c>TIMESTAMP_NTZ</c> storing UTC; parsed
/// <see cref="DateTimeOffset"/> filter values are bound by the adapter as UTC instants.</description></item>
/// </list>
/// Feature availability is injected as <see cref="SnowflakeCapabilities"/> (probed once per endpoint
/// by <see cref="SnowflakeCapabilityProbe"/>); every capability-dependent construct switches to its
/// fallback shape when the endpoint (e.g. the LocalStack emulator) lacks the feature.
/// </summary>
public class SnowflakeSqlGenerator
{
    /// <summary>
    /// Creates a generator for the given endpoint feature profile.
    /// </summary>
    /// <param name="capabilities">SQL features the connected endpoint supports; defaults to
    /// <see cref="SnowflakeCapabilities.AllOn"/> (the real-Snowflake profile).</param>
    public SnowflakeSqlGenerator(SnowflakeCapabilities? capabilities = null)
        => Capabilities = capabilities ?? SnowflakeCapabilities.AllOn;

    /// <summary>
    /// SQL features of the connected endpoint. Drives the ILIKE / MAX_BY / QUALIFY / LIKE-ESCAPE
    /// fallback shapes; <see cref="GenerateVectorSearchQuery"/> requires
    /// <see cref="SnowflakeCapabilities.SupportsVector"/>.
    /// </summary>
    public SnowflakeCapabilities Capabilities { get; }

    private int _paramIndex;
    private readonly Dictionary<string, object> _parameters = new();

    /// <summary>
    /// Snowflake schema name for qualifying access control tables.
    /// When set, tables like user_effective_permissions become "schema"."table".
    /// </summary>
    public string? SchemaName { get; init; }

    /// <summary>
    /// The named-parameter marker convention — Snowflake.Data binds named parameters with
    /// <c>:name</c> placeholders (registered under the bare name, see
    /// <see cref="SnowflakeConnectionSource.AddParam"/>). Sealed in this one helper so the
    /// convention can be switched in one line if the driver disagrees.
    /// </summary>
    private static string Marker(string name) => ":" + name;

    /// <summary>The central partition-access table — same hardcoded central location as the PG generator.</summary>
    private const string PartitionAccessTable = "\"public\".\"partition_access\"";

    /// <summary>
    /// Quotes a table reference: pre-qualified references (already containing a quote) pass through
    /// verbatim; bare names are qualified with <see cref="SchemaName"/> when set, else just quoted.
    /// Unlike the PG generator (which interpolates bare names raw — safe there because PG folds
    /// unquoted identifiers to lowercase), Snowflake would uppercase a bare name, so quoting is
    /// mandatory everywhere.
    /// </summary>
    private string QualifyTable(string table)
        => table.Contains('"')
            ? table
            : string.IsNullOrEmpty(SchemaName)
                ? SnowflakeIdentifiers.Quote(table)
                : SnowflakeIdentifiers.Qualify(SchemaName, table);

    private static readonly FrozenDictionary<string, string> PropertyMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = "n.\"name\"",
            ["nodeType"] = "n.\"node_type\"",
            ["node_type"] = "n.\"node_type\"",
            ["description"] = "n.\"description\"",
            ["category"] = "n.\"category\"",
            ["icon"] = "n.\"icon\"",
            ["order"] = "n.\"display_order\"",
            ["display_order"] = "n.\"display_order\"",
            ["lastModified"] = "n.\"last_modified\"",
            ["last_modified"] = "n.\"last_modified\"",
            ["version"] = "n.\"version\"",
            ["state"] = "n.\"state\"",
            ["id"] = "n.\"id\"",
            ["namespace"] = "n.\"namespace\"",
            ["path"] = "n.\"path\"",
            ["mainNode"] = "n.\"main_node\"",
            ["main_node"] = "n.\"main_node\""
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Non-text columns where case-insensitive comparison should NOT be applied.
    /// Everything else (text columns + variant content fields extracted via <c>::string</c>) is treated as text.
    /// </summary>
    private static readonly FrozenSet<string> NonTextColumns =
        new[] { "order", "display_order", "version", "state", "lastModified", "last_modified" }
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static bool IsTextField(string selector) =>
        !NonTextColumns.Contains(selector);

    /// <summary>Quotes a JSON path key inside a variant accessor, escaping embedded quotes.</summary>
    private static string JsonKey(string key) => "\"" + key.Replace("\"", "\"\"") + "\"";

    /// <summary>
    /// Builds the variant path accessor for the content column:
    /// <c>n."content":"A"."B"::string</c> — the Snowflake counterpart of
    /// <c>n.content-&gt;'A'-&gt;&gt;'B'</c>. Path keys are quoted so casing and special
    /// characters are preserved exactly.
    /// </summary>
    private static string ContentAccessor(IReadOnlyList<string> pathKeys)
    {
        var sb = new StringBuilder("n.\"content\":");
        sb.Append(JsonKey(pathKeys[0]));
        for (var i = 1; i < pathKeys.Count; i++)
            sb.Append('.').Append(JsonKey(pathKeys[i]));
        sb.Append("::string");
        return sb.ToString();
    }

    /// <summary>
    /// Maps a selector from the query AST to a Snowflake column expression.
    /// </summary>
    public static string MapSelector(string selector)
    {
        if (PropertyMap.TryGetValue(selector, out var mapped))
            return mapped;

        // content.X.Y → n."content":"X"."Y"::string
        if (selector.StartsWith("content.", StringComparison.OrdinalIgnoreCase))
        {
            var parts = selector.Split('.');
            return ContentAccessor(parts[1..]);
        }

        // Unknown selectors fall back to variant content
        return ContentAccessor([selector]);
    }

    /// <summary>
    /// Maps a selector for ORDER BY (returns typed column or variant extraction).
    /// <para>
    /// Supports SQL-function call syntax — e.g. <c>length(path)</c>,
    /// <c>lower(name)</c>. The function name is allow-listed (no arbitrary SQL
    /// injection); the inner selector is mapped through the same column map as
    /// bare selectors. The canonical use is the routing-layer
    /// "longest-matching-prefix" lookup: <c>sort:length(path)-desc</c> picks
    /// the deepest match in a single round-trip.
    /// </para>
    /// </summary>
    private static string MapOrderBySelector(string selector)
    {
        // Detect `func(arg)` syntax. Allow-listed functions only.
        var openParen = selector.IndexOf('(');
        if (openParen > 0 && selector.EndsWith(")"))
        {
            var funcName = selector[..openParen].Trim();
            var argName = selector[(openParen + 1)..^1].Trim();
            if (AllowedSqlFunctions.Contains(funcName))
            {
                var mappedArg = MapOrderBySelector(argName);
                return $"{funcName.ToLowerInvariant()}({mappedArg})";
            }
        }

        return MapSelector(selector);
    }

    /// <summary>
    /// Allow-listed SQL functions usable in <c>sort:func(field)-desc</c>.
    /// Tight allow-list — no arbitrary SQL in the sort selector.
    /// </summary>
    private static readonly FrozenSet<string> AllowedSqlFunctions =
        new[] { "length", "lower", "upper" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The explicit LIKE escape declaration appended after every pattern that was escaped by
    /// <see cref="EscapeLikePattern"/>. Snowflake has NO default escape character (PG defaults to
    /// backslash), so an escaped pattern silently mis-matches without it. Empty when the endpoint
    /// does not support <c>LIKE … ESCAPE</c> — in that case callers bind the UNESCAPED pattern
    /// (degraded: LIKE metacharacters in user input act as wildcards).
    /// </summary>
    private string LikeEscapeSuffix => Capabilities.SupportsLikeEscape ? @" ESCAPE '\\'" : "";

    /// <summary>
    /// Case-insensitive LIKE: native <c>ILIKE</c> when supported, else
    /// <c>LOWER(x) LIKE LOWER(p)</c>.
    /// </summary>
    private string IlikeClause(string expression, string patternSql) =>
        Capabilities.SupportsIlike
            ? $"{expression} ILIKE {patternSql}"
            : $"LOWER({expression}) LIKE LOWER({patternSql})";

    /// <summary>
    /// Builds the <c>WHERE</c> clause (with bound parameters) for a parsed query, combining
    /// filter, text search, access control, main-node, excluded-node-type, and active-state predicates.
    /// </summary>
    /// <param name="query">The parsed query whose filter/text/scope predicates are translated.</param>
    /// <param name="userId">Optional user id used to add the access-control predicate; omit for system/unfiltered access.</param>
    /// <param name="excludedNodeTypes">Optional node types to exclude (e.g. hidden from a given context).</param>
    /// <returns>The <c>WHERE</c> clause text (empty when no predicates) and the bound parameter map (bare-name keys, SQL references them as <c>:name</c>).</returns>
    public (string WhereClause, Dictionary<string, object> Parameters) GenerateWhereClause(
        ParsedQuery query, string? userId = null, IReadOnlyCollection<string>? excludedNodeTypes = null)
    {
        _paramIndex = 0;
        _parameters.Clear();

        var clauses = new List<string>();

        if (query.Filter != null)
        {
            var filterClause = GenerateNodeClause(query.Filter);
            if (!string.IsNullOrEmpty(filterClause))
                clauses.Add(filterClause);
        }

        if (!string.IsNullOrEmpty(query.TextSearch))
        {
            var textClause = GenerateTextSearchClause(query.TextSearch);
            if (!string.IsNullOrEmpty(textClause))
                clauses.Add(textClause);
        }

        if (!string.IsNullOrEmpty(userId))
        {
            var acClause = GenerateAccessControlClause(userId);
            clauses.Add(acClause);
        }

        if (query.IsMain == true)
        {
            clauses.Add("n.\"main_node\" = n.\"path\"");
        }

        // Context-based exclusion: exclude node types that are configured to be
        // hidden from the given context (e.g., Role, User excluded from "search").
        if (excludedNodeTypes is { Count: > 0 })
        {
            var paramNames = new List<string>();
            foreach (var nt in excludedNodeTypes)
            {
                var p = $"p{_paramIndex++}";
                _parameters[p] = nt;
                paramNames.Add(Marker(p));
            }
            clauses.Add($"(n.\"node_type\" IS NULL OR n.\"node_type\" NOT IN ({string.Join(", ", paramNames)}))");
        }

        // Only return Active nodes (state=2) — excludes Transient and Deleted
        clauses.Add("n.\"state\" = 2");

        var whereClause = clauses.Count > 0
            ? "WHERE " + string.Join(" AND ", clauses)
            : "";

        return (whereClause, new Dictionary<string, object>(_parameters));
    }

    /// <summary>
    /// Projection for the node-level static-repo sync claim: the real <c>sync_behavior</c> column
    /// when selecting from <c>mesh_nodes</c> (the only decouplable table), else the Include (0)
    /// default so satellite-table selects keep a uniform result shape (mirrors the PG adapter's
    /// <c>SyncBehaviorCol</c>).
    /// </summary>
    private static string SyncBehaviorColumn(string tableName) =>
        tableName.Contains("mesh_nodes", StringComparison.OrdinalIgnoreCase)
            ? "n.\"sync_behavior\""
            : "0::smallint AS \"sync_behavior\"";

    /// <summary>The uniform mesh-node projection column list (quoted, alias <c>n</c>).</summary>
    private const string NodeColumnsPrefix =
        "n.\"id\", n.\"namespace\", n.\"name\", n.\"node_type\", n.\"description\", " +
        "n.\"category\", n.\"icon\", n.\"display_order\"";

    /// <summary>
    /// Builds a complete <c>SELECT</c> statement (columns, optional activity/user-activity joins,
    /// scope, order, and limit) for a parsed query, with bound parameters.
    /// </summary>
    /// <param name="query">The parsed query to translate.</param>
    /// <param name="userId">Optional user id for the access-control predicate.</param>
    /// <param name="activityUserId">Optional user id used to join the user-activity satellite for <c>source:accessed</c> queries.</param>
    /// <param name="tableName">Primary table to select from; defaults to <c>mesh_nodes</c>. A pre-qualified quoted reference passes through verbatim; bare names are quoted (and schema-qualified when <see cref="SchemaName"/> is set).</param>
    /// <param name="activityTable">Activity satellite table joined for <c>source:activity</c> queries.</param>
    /// <param name="userActivityTable">User-activity satellite table joined for <c>source:accessed</c> queries.</param>
    /// <param name="excludedNodeTypes">Optional node types to exclude from results.</param>
    /// <param name="includeContent">When false, projects <c>NULL::variant</c> for content to avoid fetching large blobs.</param>
    /// <returns>The SQL statement text and the bound parameter map (bare-name keys).</returns>
    public (string Sql, Dictionary<string, object> Parameters) GenerateSelectQuery(
        ParsedQuery query, string? userId = null, string? activityUserId = null,
        string tableName = "mesh_nodes",
        string activityTable = "activities",
        string userActivityTable = "user_activities",
        IReadOnlyCollection<string>? excludedNodeTypes = null,
        bool includeContent = true)
    {
        var (whereClause, parameters) = GenerateWhereClause(query, userId, excludedNodeTypes);

        var isAccessedQuery = query.Source == QuerySource.Accessed && !string.IsNullOrEmpty(activityUserId);
        var isActivityQuery = query.Source == QuerySource.Activity;

        // n."content" is a variant column that can be many KB. When the caller projects via
        // `select:` and didn't ask for "content", emit NULL::variant AS "content" so the
        // result-set shape is unchanged (ReadMeshNode still finds the column) but the
        // engine avoids materializing large blobs.
        var contentColumn = includeContent ? "n.\"content\"" : "NULL::variant AS \"content\"";

        // sync_behavior (the static-repo "Not synced" decouple claim) lives only on mesh_nodes —
        // the sole decouplable table. Project the real column for mesh_nodes and the Include
        // default for satellites so the result shape stays uniform (see the PG generator's
        // history: omitting it silently re-synced decoupled partition roots).
        var syncBehaviorColumn = SyncBehaviorColumn(tableName);

        var sql = new StringBuilder($"SELECT {NodeColumnsPrefix}, n.\"last_modified\", n.\"version\", " +
            $"n.\"state\", {contentColumn}, " +
            $"n.\"desired_id\", n.\"main_node\", {syncBehaviorColumn} FROM {QualifyTable(tableName)} n");

        if (isAccessedQuery)
        {
            // JOIN with UserActivity nodes stored in the user_activities
            // satellite table — they live under {userId}/_UserActivity per the
            // post-v10 per-user partition layout.
            parameters["actUserNs"] = $"{activityUserId}/_UserActivity";
            sql.Append($" INNER JOIN {QualifyTable(userActivityTable)} ua ON ua.\"namespace\" = {Marker("actUserNs")}" +
                        " AND ua.\"node_type\" = 'UserActivity'" +
                        " AND REPLACE(n.\"path\", '/', '_') = ua.\"id\"");
        }
        else if (isActivityQuery)
        {
            // JOIN with Activity satellites stored in the activities satellite table
            sql.Append($" INNER JOIN {QualifyTable(activityTable)} act ON act.\"main_node\" = n.\"path\"" +
                        " AND act.\"node_type\" = 'Activity'");
        }

        // Both source queries restrict to main content nodes only (main_node = path)
        if (isActivityQuery || isAccessedQuery)
        {
            var mainNodeFilter = "n.\"main_node\" = n.\"path\"";
            whereClause = string.IsNullOrEmpty(whereClause)
                ? $"WHERE {mainNodeFilter}"
                : $"{whereClause} AND {mainNodeFilter}";
        }

        if (!string.IsNullOrEmpty(whereClause))
            sql.Append($" {whereClause}");

        if (isAccessedQuery)
        {
            sql.Append(" ORDER BY ua.\"last_modified\" DESC NULLS LAST");
        }
        else if (isActivityQuery && query.OrderBy == null)
        {
            // Default ordering: most recent activity first
            sql.Append(" ORDER BY act.\"content\":\"Start\"::string DESC NULLS LAST");
        }
        else if (query.OrderBy != null)
        {
            var direction = query.OrderBy.Descending ? "DESC" : "ASC";
            sql.Append($" ORDER BY {MapOrderBySelector(query.OrderBy.Property)} {direction}");
        }
        else if (!string.IsNullOrEmpty(query.TextSearch))
        {
            // Relevance parity with GenerateCrossSchemaSelectQuery: rank by a hybrid score so the
            // LIMIT keeps the most-RELEVANT rows, not arbitrary scan order. Same ladder: exact
            // name > name-prefix > id-prefix > name-substring > id-substring >
            // description-substring. An explicit OrderBy (handled above) supersedes. The ladder's
            // LIKE patterns are built from the term verbatim (no EscapeLikePattern), matching the
            // PG generator — so no ESCAPE clause here.
            parameters["scoreText"] = query.TextSearch;
            var m = Marker("scoreText");
            sql.Append(
                " ORDER BY (CASE " +
                $"WHEN LOWER(COALESCE(n.\"name\",'')) = LOWER({m}) THEN 1000 " +
                $"WHEN LOWER(COALESCE(n.\"name\",'')) LIKE LOWER({m}) || '%' THEN 600 " +
                $"WHEN LOWER(COALESCE(n.\"id\",'')) LIKE LOWER({m}) || '%' THEN 500 " +
                $"WHEN LOWER(COALESCE(n.\"name\",'')) LIKE '%' || LOWER({m}) || '%' THEN 300 " +
                $"WHEN LOWER(COALESCE(n.\"id\",'')) LIKE '%' || LOWER({m}) || '%' THEN 200 " +
                $"WHEN LOWER(COALESCE(n.\"description\",'')) LIKE '%' || LOWER({m}) || '%' THEN 100 " +
                "ELSE 0 END) DESC, n.\"last_modified\" DESC NULLS LAST");
        }

        if (query.Limit.HasValue)
            sql.Append($" LIMIT {query.Limit.Value}");

        return (sql.ToString(), parameters);
    }

    /// <summary>
    /// Multi-path overload — emits <c>n."path" IN (:scopePath0, :scopePath1, ...)</c> for the
    /// <see cref="QueryScope.Exact"/> + multi-value <c>path:a|b|c</c> case
    /// (canonical use: routing-layer "longest-matching-prefix" lookup with
    /// <c>sort:pathLength-desc limit:1</c>). Other scopes / single-path values
    /// fall through to the single-path overload. Array binds are always expanded to
    /// IN-lists of individual markers (no <c>= ANY(array)</c> in this dialect).
    /// </summary>
    /// <param name="paths">The candidate paths; <c>null</c>/single-element falls through to the single-path overload.</param>
    /// <param name="scope">How the paths are matched; only <see cref="QueryScope.Exact"/> supports multi-path push-down.</param>
    /// <param name="useMainNode">When true, scopes against <c>n."main_node"</c> instead of <c>n."path"</c>.</param>
    /// <param name="qualifiedTable">Optional table reference used by the <see cref="QueryScope.NextLevel"/> anti-join.</param>
    /// <returns>The scope clause text and the bound parameter map (bare-name keys).</returns>
    public (string Clause, Dictionary<string, object> Parameters) GenerateScopeClause(
        IReadOnlyList<string>? paths, QueryScope scope, bool useMainNode = false, string? qualifiedTable = null)
    {
        if (paths == null || paths.Count <= 1)
            return GenerateScopeClause(paths is { Count: 1 } ? paths[0] : null, scope, useMainNode, qualifiedTable);

        // Only Exact scope supports multi-path push-down today. Subtree/Children/etc.
        // would require OR-ing N LIKE clauses — caller can either emit those itself
        // or stick to single-path for non-Exact scopes.
        if (scope != QueryScope.Exact)
            return GenerateScopeClause(paths[0], scope, useMainNode, qualifiedTable);

        var parameters = new Dictionary<string, object>();
        var paramNames = new List<string>(paths.Count);
        for (var i = 0; i < paths.Count; i++)
        {
            var name = $"scopePath{i}";
            paramNames.Add(Marker(name));
            parameters[name] = paths[i].Trim('/');
        }
        var column = useMainNode ? "n.\"main_node\"" : "n.\"path\"";
        var clause = $"{column} IN ({string.Join(", ", paramNames)})";
        return (clause, parameters);
    }

    /// <summary>
    /// Builds the path-scoping predicate (with bound parameters) for a single base path and
    /// <paramref name="scope"/> — exact, children, or subtree matching against the path or main-node column.
    /// </summary>
    /// <param name="basePath">The base path to scope to; <c>null</c> yields an empty clause.</param>
    /// <param name="scope">How <paramref name="basePath"/> is matched (exact, children, subtree, etc.).</param>
    /// <param name="useMainNode">When true, scopes against <c>n."main_node"</c> instead of <c>n."path"</c>.</param>
    /// <param name="qualifiedTable">Optional table reference for the <see cref="QueryScope.NextLevel"/> anti-join; bare names are quoted/qualified.</param>
    /// <returns>The scope clause text (empty when no base path) and the bound parameter map (bare-name keys).</returns>
    public (string Clause, Dictionary<string, object> Parameters) GenerateScopeClause(
        string? basePath, QueryScope scope, bool useMainNode = false, string? qualifiedTable = null)
    {
        var parameters = new Dictionary<string, object>();

        if (basePath == null)
            return ("", parameters);

        var normalizedPath = basePath.Trim('/');

        // For satellite-table queries, the scope filter targets `main_node` (the
        // path of the parent the satellite is attached to) instead of `namespace`
        // / `path`, because satellite namespaces include the satellite suffix
        // (e.g. "OrgAlpha/_Thread") while the user's `namespace:X` qualifier
        // expresses an attachment-scoped predicate ("satellites attached to
        // nodes within X"). Children semantics (the default for `namespace:X`)
        // therefore map to Subtree-on-main_node — a thread at
        // `Org/doc/_Thread` with `main_node=Org/doc` is conceptually "in Org"
        // for partition-fan-out purposes.
        var clause = scope switch
        {
            QueryScope.Exact => useMainNode
                ? GenerateMainNodeExactClause(normalizedPath, parameters)
                : GenerateExactClause(normalizedPath, parameters),
            QueryScope.Children => useMainNode
                ? GenerateMainNodeSubtreeClause(normalizedPath, parameters)
                : GenerateChildrenClause(normalizedPath, parameters),
            QueryScope.Descendants => useMainNode
                ? GenerateMainNodeDescendantsClause(normalizedPath, parameters)
                : GenerateDescendantsClause(normalizedPath, parameters),
            QueryScope.Subtree => useMainNode
                ? GenerateMainNodeSubtreeClause(normalizedPath, parameters)
                : GenerateSubtreeClause(normalizedPath, parameters),
            QueryScope.Ancestors => GenerateAncestorsClause(normalizedPath, parameters),
            QueryScope.AncestorsAndSelf => GenerateAncestorsAndSelfClause(normalizedPath, parameters),
            QueryScope.Hierarchy => GenerateHierarchyClause(normalizedPath, parameters),
            // NextLevel = the populated frontier: descendants with no nearer active ancestor.
            // Satellite (useMainNode) navigation isn't a thing — degrade to the main-node subtree.
            QueryScope.NextLevel => useMainNode
                ? GenerateMainNodeSubtreeClause(normalizedPath, parameters)
                : GenerateNextLevelClause(
                    normalizedPath,
                    qualifiedTable is null ? null : QualifyTable(qualifiedTable),
                    parameters),
            _ => ""
        };

        return (clause, parameters);
    }

    private static string GenerateMainNodeExactClause(string path, Dictionary<string, object> parameters)
    {
        parameters["scopeMain"] = path;
        return $"n.\"main_node\" = {Marker("scopeMain")}";
    }

    private static string GenerateMainNodeDescendantsClause(string path, Dictionary<string, object> parameters)
    {
        parameters["scopeMainPrefix"] = $"{path}/";
        return $"n.\"main_node\" LIKE {Marker("scopeMainPrefix")} || '%'";
    }

    private static string GenerateMainNodeSubtreeClause(string path, Dictionary<string, object> parameters)
    {
        parameters["scopeMain"] = path;
        parameters["scopeMainPrefix"] = $"{path}/";
        return $"(n.\"main_node\" = {Marker("scopeMain")} OR n.\"main_node\" LIKE {Marker("scopeMainPrefix")} || '%')";
    }

    /// <summary>
    /// Generates a UNION ALL query across multiple schemas.
    /// Each schema gets the same WHERE clause but different schema-qualified table names.
    ///
    /// <para><paramref name="aclSchemas"/> gates the per-branch access-control SQL: the
    /// partition/node permission predicates are emitted ONLY for schemas in that set. Schemas
    /// outside the set are PUBLIC content (e.g. the mirrored documentation) that ship
    /// <c>mesh_nodes</c> WITHOUT the per-partition permission tables — referencing those missing
    /// relations would fail the whole UNION. This replaces the PG stored proc's
    /// <c>to_regclass</c> existence guard with an explicit caller-provided set.</para>
    ///
    /// <para><paramref name="activityUserId"/> opts into the
    /// <c>source:activity</c> / <c>source:accessed</c> JOIN form: for activity,
    /// each schema's branch INNER JOINs <c>{schema}.activities</c> on
    /// <c>main_node = n."path"</c>; for accessed, it JOINs
    /// <c>{schema}.user_activities</c> by the user's namespace. <c>is:main</c>
    /// is implied (<c>n."main_node" = n."path"</c>) and the default sort becomes
    /// the joined satellite's <c>last_modified</c> so activity-recency
    /// ordering survives the UNION.</para>
    ///
    /// <para><paramref name="contentSchemas"/> folds indexed content into the SAME omnibox UNION: for a
    /// FREE-TEXT query (a non-empty <see cref="ParsedQuery.TextSearch"/>) ONLY, each content schema adds
    /// a branch that lexically matches <c>{schema}.content_chunks.chunk_text</c> and projects each file's
    /// best chunk to its synthetic <c>Document</c> row (path slug, <c>_Documents</c> namespace — replicates
    /// <c>DocumentPaths.For/Slug</c>; see <see cref="SlugSql"/>). Pure structured queries skip it, and an
    /// empty list adds nothing. Dedup uses QUALIFY/ROW_NUMBER instead of PG's DISTINCT ON.</para>
    /// </summary>
    /// <param name="query">The parsed query to translate.</param>
    /// <param name="schemas">Schemas to UNION across.</param>
    /// <param name="aclSchemas">Schemas that carry the per-partition permission tables; access-control SQL is emitted only for these.</param>
    /// <param name="userId">Optional user id for the per-schema access-control predicate.</param>
    /// <param name="tableName">Per-schema table to select from (bare name; qualified per schema).</param>
    /// <param name="activityUserId">Optional user id enabling the <c>source:accessed</c> JOIN form.</param>
    /// <param name="contentSchemas">Schemas whose <c>content_chunks</c> join the free-text omnibox UNION.</param>
    /// <returns>The SQL statement text and the bound parameter map (bare-name keys).</returns>
    public (string Sql, Dictionary<string, object> Parameters) GenerateCrossSchemaSelectQuery(
        ParsedQuery query,
        IReadOnlyList<string> schemas,
        IReadOnlyCollection<string> aclSchemas,
        string? userId = null,
        string tableName = "mesh_nodes",
        string? activityUserId = null,
        IReadOnlyList<string>? contentSchemas = null)
    {
        var (whereClause, parameters) = GenerateWhereClause(query);
        var whereCore = whereClause.StartsWith("WHERE ", StringComparison.Ordinal)
            ? whereClause[6..]
            : whereClause;

        // Push-down for the routing-layer `path:a|b|c` form (PathResolutionService
        // emits this to fetch every ancestor in one query and pick the longest
        // by `sort:length(path)-desc limit:1`). Without an IN-list filter, the
        // cross-schema UNION returns EVERY row in the satellite table, the outer
        // ORDER BY picks whichever row has the longest path, and the resolver
        // surfaces a sibling instead of the requested node.
        if (query.Paths is { Count: > 1 } && query.Scope == QueryScope.Exact)
        {
            var paramNames = new List<string>(query.Paths.Count);
            for (var i = 0; i < query.Paths.Count; i++)
            {
                var name = $"xspath{i}";
                paramNames.Add(Marker(name));
                parameters[name] = query.Paths[i].Trim('/');
            }
            var pathInClause = $"n.\"path\" IN ({string.Join(", ", paramNames)})";
            whereCore = string.IsNullOrEmpty(whereCore)
                ? pathInClause
                : $"{pathInClause} AND {whereCore}";
        }
        else if (query.Paths is null or { Count: <= 1 }
                 && !string.IsNullOrEmpty(query.Path)
                 && query.Scope == QueryScope.Exact
                 && !query.Path.Contains('*'))
        {
            // Single-path exact form goes through the same routing surface
            // (PathResolutionService also fires path:X for trivial requests).
            // Same logic — pin the satellite UNION to that one path.
            parameters["xspath0"] = query.Path.Trim('/');
            var pathEqClause = $"n.\"path\" = {Marker("xspath0")}";
            whereCore = string.IsNullOrEmpty(whereCore)
                ? pathEqClause
                : $"{pathEqClause} AND {whereCore}";
        }
        else if (query.Paths is null or { Count: <= 1 }
                 && !string.IsNullOrEmpty(query.Path)
                 && query.Scope != QueryScope.Exact
                 && !query.Path.Contains('*'))
        {
            // Subtree / Children / Descendants / Hierarchy / AncestorsAndSelf —
            // same class of bug as the Exact branch above, just one level out.
            // Without this push-down the cross-schema UNION returns every row
            // in the satellite table (see the PG generator's EventCalendar/Source
            // prod repro); the scope clause MUST be pushed into every branch.
            var (scopeClause, scopeParams) = GenerateScopeClause(query.Path, query.Scope);
            if (!string.IsNullOrEmpty(scopeClause))
            {
                whereCore = string.IsNullOrEmpty(whereCore)
                    ? scopeClause
                    : $"{scopeClause} AND {whereCore}";
                foreach (var (k, v) in scopeParams)
                    parameters[k] = v;
            }
        }

        var isActivity = query.Source == QuerySource.Activity;
        var isAccessed = query.Source == QuerySource.Accessed && !string.IsNullOrEmpty(activityUserId);

        if (isAccessed)
            parameters["actUserNs"] = $"{activityUserId}/_UserActivity";

        var aclSet = aclSchemas as IReadOnlySet<string>
            ?? new HashSet<string>(aclSchemas, StringComparer.OrdinalIgnoreCase);

        var parts = new List<string>();
        foreach (var schema in schemas)
        {
            var qualifiedTable = SnowflakeIdentifiers.Qualify(schema, tableName);
            var activityTable = SnowflakeIdentifiers.Qualify(schema, "activities");
            var userActivityTable = SnowflakeIdentifiers.Qualify(schema, "user_activities");
            var uepTable = SnowflakeIdentifiers.Qualify(schema, "user_effective_permissions");
            var ntpTable = SnowflakeIdentifiers.Qualify(schema, "node_type_permissions");

            // For source:activity, project the JOINed activity's last_modified into
            // the same column slot so the outer ORDER BY ranks rows by activity recency.
            // For source:accessed, project the UserActivity row's last_modified the same way.
            // For plain queries, n."last_modified" is fine.
            var lastModifiedExpr = isActivity
                ? "act.\"last_modified\""
                : (isAccessed ? "ua.\"last_modified\"" : "n.\"last_modified\"");

            var selectSql = $"SELECT {NodeColumnsPrefix}, {lastModifiedExpr} AS \"last_modified\", " +
                "n.\"version\", n.\"state\", n.\"content\", " +
                $"n.\"desired_id\", n.\"main_node\", {SyncBehaviorColumn(tableName)} FROM {qualifiedTable} n";

            if (isAccessed)
                selectSql += $" INNER JOIN {userActivityTable} ua ON ua.\"namespace\" = {Marker("actUserNs")}" +
                             " AND ua.\"node_type\" = 'UserActivity'" +
                             " AND REPLACE(n.\"path\", '/', '_') = ua.\"id\"";
            else if (isActivity)
                selectSql += $" INNER JOIN {activityTable} act ON act.\"main_node\" = n.\"path\"" +
                             " AND act.\"node_type\" = 'Activity'";

            // ACL only for schemas that actually carry the permission tables — public content
            // schemas would 42S02 the whole UNION (the PG stored proc guarded this with
            // to_regclass; here the caller supplies the provisioned set explicitly).
            var accessClause = aclSet.Contains(schema)
                ? BuildPerSchemaAccessClause(userId, schema, uepTable, ntpTable, parameters)
                : "";
            var mainNodeFilter = (isActivity || isAccessed) ? "n.\"main_node\" = n.\"path\"" : null;

            var clauses = new List<string>();
            if (!string.IsNullOrEmpty(whereCore)) clauses.Add(whereCore);
            if (mainNodeFilter is not null) clauses.Add(mainNodeFilter);
            if (!string.IsNullOrEmpty(accessClause)) clauses.Add(accessClause);
            var fullWhere = clauses.Count == 0 ? "" : "WHERE " + string.Join(" AND ", clauses);

            parts.Add($"{selectSql} {fullWhere}");
        }

        // Content branches — only for a FREE-TEXT omnibox query, and only over schemas that hold a
        // content_chunks table. Each branch lexically matches the chunk text and projects each file's
        // best chunk to its synthetic Document row (slug + _Documents namespace — replicates
        // DocumentPaths.For/Slug; see SlugSql). The 15 projected columns align positionally with the
        // mesh_nodes branches above so the outer SELECT * / ReadMeshNode shape is uniform; the term is
        // inlined (this branch inlines the term like the PG generator) and single-quotes doubled.
        // The term is NOT LIKE-escaped (bug-for-bug parity with PG) so no ESCAPE clause is emitted.
        if (contentSchemas is { Count: > 0 } && !string.IsNullOrEmpty(query.TextSearch))
        {
            var term = query.TextSearch.Replace("'", "''");
            foreach (var schema in contentSchemas)
            {
                var contentTable = SnowflakeIdentifiers.Qualify(schema, "content_chunks");
                var likeClause = IlikeClause("cc.\"chunk_text\"", $"'%{term}%'");
                // One (best) row per file. PG used DISTINCT ON with an unkeyed pick (arbitrary
                // chunk); the ROW_NUMBER translation needs a deterministic window ORDER BY —
                // most-recent chunk wins (documented deviation from PG's arbitrary pick).
                parts.Add(BuildDedupedContentArm(
                    DocumentProjection,
                    contentTable,
                    likeClause,
                    "cc.\"last_modified\" DESC",
                    extraProjected: null));
            }
        }

        var sql = string.Join(" UNION ALL ", parts);

        if (query.OrderBy != null)
        {
            var direction = query.OrderBy.Descending ? "DESC" : "ASC";
            // Strip "n." prefix — the outer query uses alias "combined", not "n"
            var orderCol = MapOrderBySelector(query.OrderBy.Property).Replace("n.", "");
            sql = $"SELECT * FROM ({sql}) combined ORDER BY {orderCol} {direction}";
        }
        else if (isActivity || isAccessed)
        {
            // Activity / accessed default ordering: satellite's last_modified DESC.
            // We projected the joined timestamp into the last_modified column slot above,
            // so a plain column reference is enough.
            sql = $"SELECT * FROM ({sql}) combined ORDER BY \"last_modified\" DESC NULLS LAST";
        }
        else if (!string.IsNullOrEmpty(query.TextSearch))
        {
            // Relevance: rank by a hybrid score so the LIMIT keeps the most-RELEVANT rows, not
            // arbitrary scan order. Score: exact name > name-prefix > id-prefix > name-substring
            // > id-substring > description-substring. Sort by score DESC, NOT alphabetically; an
            // explicit OrderBy (handled above) supersedes. Term inlined (this wrapper inlines the
            // term like the PG generator); single-quotes doubled for safety.
            var t = query.TextSearch.Replace("'", "''");
            sql = $"SELECT * FROM ({sql}) combined ORDER BY (CASE " +
                $"WHEN LOWER(COALESCE(\"name\",'')) = LOWER('{t}') THEN 1000 " +
                $"WHEN LOWER(COALESCE(\"name\",'')) LIKE LOWER('{t}') || '%' THEN 600 " +
                $"WHEN LOWER(COALESCE(\"id\",'')) LIKE LOWER('{t}') || '%' THEN 500 " +
                $"WHEN LOWER(COALESCE(\"name\",'')) LIKE '%' || LOWER('{t}') || '%' THEN 300 " +
                $"WHEN LOWER(COALESCE(\"id\",'')) LIKE '%' || LOWER('{t}') || '%' THEN 200 " +
                $"WHEN LOWER(COALESCE(\"description\",'')) LIKE '%' || LOWER('{t}') || '%' THEN 100 " +
                "ELSE 0 END) DESC, \"last_modified\" DESC NULLS LAST";
        }

        if (query.Limit.HasValue)
            sql += $" LIMIT {query.Limit.Value}";

        return (sql, parameters);
    }

    /// <summary>
    /// Per-schema access clause for the cross-schema UNION: partition_access gate + public-read
    /// node types + the longest-prefix node permission predicate (see
    /// <see cref="BuildNodeAccessPredicate"/>). Reuses one <c>acUser_cross</c> parameter across
    /// branches. Empty when <paramref name="userId"/> is not set (system access).
    /// </summary>
    private string BuildPerSchemaAccessClause(
        string? userId, string schema, string uepTable, string ntpTable,
        Dictionary<string, object> parameters)
    {
        if (string.IsNullOrEmpty(userId))
            return "";

        // Reuse existing param if already added, else create new
        const string paramName = "acUser_cross";
        if (!parameters.ContainsKey(paramName))
            parameters[paramName] = userId;
        var marker = Marker(paramName);

        var userList = userId == WellKnownUsers.Anonymous
            ? marker : $"{marker}, 'Public'";

        var publicReadClause = userId == WellKnownUsers.Anonymous ? "" :
            $"EXISTS (SELECT 1 FROM {ntpTable} ntp WHERE ntp.\"node_type\" = n.\"node_type\" AND ntp.\"public_read\" = true)";

        var partitionAccessExists =
            $"EXISTS (SELECT 1 FROM {PartitionAccessTable} pa WHERE pa.\"user_id\" IN ({userList}) AND pa.\"partition\" = '{EscapeSqlLiteral(schema)}')";

        var nodeAccess = BuildNodeAccessPredicate(uepTable, marker, userList);

        if (!string.IsNullOrEmpty(publicReadClause))
            return $"({publicReadClause} OR ({partitionAccessExists} AND ({nodeAccess})))";

        return $"({partitionAccessExists} AND ({nodeAccess}))";
    }

    /// <summary>
    /// The node-level permission predicate: the user owns the node, OR the longest matching
    /// <c>user_effective_permissions</c> path-prefix row (the caller's own row breaking
    /// same-length ties) allows Read.
    /// <para>With MAX_BY: one aggregate subquery whose score is
    /// <c>LENGTH(prefix) * 2 + IFF(own-row, 1, 0)</c> — length dominates (×2), the +1 prefers the
    /// caller's own row at equal length; exactly PG's <c>ORDER BY LENGTH(prefix) DESC,
    /// own-row-first LIMIT 1</c>.</para>
    /// <para>Without MAX_BY: the equivalent EXISTS(allow) AND NOT EXISTS(stronger deny) pair —
    /// an allow row wins unless a deny row is strictly longer, or same-length but caller-owned
    /// while the allow row is not. (At exact ties within the same ownership class PG's LIMIT 1
    /// picks arbitrarily; the EXISTS pair resolves such ties to allow.)</para>
    /// The prefix LIKE uses the stored prefix verbatim (no ESCAPE) — bug-for-bug parity with PG.
    /// </summary>
    private string BuildNodeAccessPredicate(string uepTable, string userMarker, string userList)
    {
        if (Capabilities.SupportsMaxBy)
            return $"""
                n."main_node" = {userMarker}
                OR (SELECT MAX_BY(uep."is_allow", LENGTH(uep."node_path_prefix") * 2 + IFF(uep."user_id" = {userMarker}, 1, 0))
                    FROM {uepTable} uep
                    WHERE uep."user_id" IN ({userList})
                      AND uep."permission" = 'Read'
                      AND n."main_node" LIKE uep."node_path_prefix" || '%') = true
                """;

        return $"""
            n."main_node" = {userMarker}
            OR EXISTS (SELECT 1 FROM {uepTable} allow_p
                WHERE allow_p."user_id" IN ({userList})
                  AND allow_p."permission" = 'Read'
                  AND allow_p."is_allow" = true
                  AND n."main_node" LIKE allow_p."node_path_prefix" || '%'
                  AND NOT EXISTS (SELECT 1 FROM {uepTable} deny_p
                      WHERE deny_p."user_id" IN ({userList})
                        AND deny_p."permission" = 'Read'
                        AND deny_p."is_allow" = false
                        AND n."main_node" LIKE deny_p."node_path_prefix" || '%'
                        AND (LENGTH(deny_p."node_path_prefix") > LENGTH(allow_p."node_path_prefix")
                             OR (LENGTH(deny_p."node_path_prefix") = LENGTH(allow_p."node_path_prefix")
                                 AND deny_p."user_id" = {userMarker}
                                 AND allow_p."user_id" <> {userMarker}))))
            """;
    }

    /// <summary>
    /// SQL that replicates <c>DocumentPaths.Slug</c> (MeshWeaver.ContentCollections.Indexing) for a
    /// <c>content_chunks.file_path</c>: keep <c>[A-Za-z0-9._-]</c> verbatim, collapse every other run to
    /// a single <c>-</c>, trim leading/trailing <c>-</c>, and fall back to <c>'document'</c> when empty.
    /// The C# <c>DocumentPaths.Slug</c> is the source of truth — this SQL MUST stay in lock-step with it.
    /// Snowflake's REGEXP_REPLACE is global by default, so PG's <c>'g'</c> flags are dropped.
    /// </summary>
    private const string SlugSql =
        "COALESCE(NULLIF(REGEXP_REPLACE(REGEXP_REPLACE(TRIM(cc.\"file_path\"), '[^A-Za-z0-9._-]+', '-'), '^-+|-+$', ''), ''), 'document')";

    /// <summary>
    /// File name from a chunk's path: strip everything up to the last slash or backslash.
    /// The pattern's SQL literal is <c>'^.*[\\\\/]'</c> — Snowflake string literals treat
    /// backslash as an escape, so four literal backslashes yield the regex char class
    /// <c>[\\/]</c> (backslash or slash), matching the PG semantics.
    /// </summary>
    private const string FileNameSql =
        @"REGEXP_REPLACE(cc.""file_path"", '^.*[\\\\/]', '')";

    /// <summary>
    /// 200-char single-line preview of a chunk's text: whitespace runs collapsed to one space.
    /// SQL literal <c>'\\s+'</c> → regex <c>\s+</c> after Snowflake string-literal escaping.
    /// REGEXP_REPLACE is global by default (PG's <c>'g'</c> flag dropped).
    /// </summary>
    private const string ChunkPreviewSql =
        @"LEFT(REGEXP_REPLACE(cc.""chunk_text"", '\\s+', ' '), 200)";

    /// <summary>
    /// The synthetic Document projection off a <c>content_chunks</c> row (alias <c>cc</c>) — 15
    /// columns positionally aligned with the mesh-node projection so UNION arms and ReadMeshNode
    /// stay shape-uniform. All aliases quoted; NULL casts use Snowflake types
    /// (<c>string</c>/<c>variant</c>).
    /// </summary>
    private static readonly string DocumentProjection =
        $"{SlugSql} AS \"id\", " +
        "cc.\"collection_path\" || '/_Documents' AS \"namespace\", " +
        $"{FileNameSql} AS \"name\", " +
        "'Document' AS \"node_type\", " +
        $"{ChunkPreviewSql} AS \"description\", " +
        "NULL::string AS \"category\", NULL::string AS \"icon\", NULL::int AS \"display_order\", " +
        "cc.\"last_modified\" AS \"last_modified\", 0::bigint AS \"version\", 2::smallint AS \"state\", " +
        "NULL::variant AS \"content\", NULL::string AS \"desired_id\", " +
        $"cc.\"collection_path\" || '/_Documents/' || {SlugSql} AS \"main_node\", " +
        "0::smallint AS \"sync_behavior\"";

    /// <summary>Column names of <see cref="DocumentProjection"/>, in projection order.</summary>
    private static readonly ImmutableArray<string> DocumentColumns =
    [
        "id", "namespace", "name", "node_type", "description", "category", "icon", "display_order",
        "last_modified", "version", "state", "content", "desired_id", "main_node", "sync_behavior"
    ];

    /// <summary>
    /// Builds a parenthesized best-chunk-per-file content arm — the Snowflake translation of PG's
    /// <c>SELECT DISTINCT ON (collection_path, file_path) … ORDER BY collection_path, file_path[, rank]</c>.
    /// With QUALIFY support: <c>QUALIFY ROW_NUMBER() OVER (PARTITION BY … ORDER BY …) = 1</c>.
    /// Without: a derived table adds <c>"_rn"</c> and the outer select filters <c>"_rn" = 1</c>,
    /// re-listing the projected columns explicitly so the arm's column count still aligns with the
    /// UNION. The arm is parenthesized so it stays a self-contained UNION branch.
    /// </summary>
    /// <param name="projection">The projected column list (aliases quoted).</param>
    /// <param name="contentTable">Qualified <c>content_chunks</c> reference.</param>
    /// <param name="whereClause">The arm's WHERE predicate (without the keyword).</param>
    /// <param name="dedupOrderBy">Window ORDER BY choosing the winning chunk per file.</param>
    /// <param name="extraProjected">Optional extra outer column (e.g. <c>"_distance"</c>) projected past the standard 15.</param>
    private string BuildDedupedContentArm(
        string projection, string contentTable, string whereClause, string dedupOrderBy,
        string? extraProjected)
    {
        const string partition = "PARTITION BY cc.\"collection_path\", cc.\"file_path\"";

        if (Capabilities.SupportsQualify)
            return $"(SELECT {projection} FROM {contentTable} cc WHERE {whereClause} " +
                   $"QUALIFY ROW_NUMBER() OVER ({partition} ORDER BY {dedupOrderBy}) = 1)";

        var outerColumns = string.Join(", ", DocumentColumns.Select(c => $"d.\"{c}\""));
        if (extraProjected is not null)
            outerColumns += $", d.\"{extraProjected}\"";
        return $"(SELECT {outerColumns} FROM (SELECT {projection}, " +
               $"ROW_NUMBER() OVER ({partition} ORDER BY {dedupOrderBy}) AS \"_rn\" " +
               $"FROM {contentTable} cc WHERE {whereClause}) d WHERE d.\"_rn\" = 1)";
    }

    /// <summary>
    /// Formats the query vector as an inline Snowflake vector literal —
    /// <c>[0.1,0.2,…]::VECTOR(FLOAT, N)</c> — with invariant-culture component formatting.
    /// Inlined (not bound) because driver-side vector binding is unverified; the literal is pure
    /// numeric content so no quoting/injection concern exists.
    /// </summary>
    private static string VectorLiteral(float[] vector)
        => "[" + string.Join(",", vector.Select(v => v.ToString(CultureInfo.InvariantCulture)))
             + $"]::VECTOR(FLOAT, {vector.Length})";

    /// <summary>
    /// Builds a vector-similarity <c>SELECT</c> — cosine distance expressed as
    /// <c>(1 - VECTOR_COSINE_SIMILARITY("embedding", vectorLiteral))</c>, numerically identical to
    /// pgvector's <c>&lt;=&gt;</c> so ordering is unchanged — optionally folding in a lexical term and
    /// indexed content chunks, with access control and an optional namespace-prefix scope.
    /// Returns the top <paramref name="topK"/> matches. Requires
    /// <see cref="SnowflakeCapabilities.SupportsVector"/>.
    /// </summary>
    /// <param name="filterQuery">Optional parsed query providing additional structured predicates.</param>
    /// <param name="queryVector">The embedding vector to rank candidates against (inlined as an invariant-culture literal).</param>
    /// <param name="userId">Optional user id for the access-control predicate.</param>
    /// <param name="topK">Maximum number of nearest matches to return.</param>
    /// <param name="lexicalTerm">Optional lexical term blended into the ranking alongside vector similarity.</param>
    /// <param name="namespacePath">Optional namespace prefix to scope the search to a partition subtree.</param>
    /// <param name="includeContentChunks">When true, also ranks indexed content chunks and projects them to their owning document.</param>
    /// <returns>The SQL statement text and the bound parameter map (bare-name keys; the vector itself is inlined, not bound).</returns>
    /// <exception cref="NotSupportedException">The endpoint does not support the VECTOR type (see <see cref="SnowflakeCapabilities.SupportsVector"/>).</exception>
    public (string Sql, Dictionary<string, object> Parameters) GenerateVectorSearchQuery(
        ParsedQuery? filterQuery,
        float[] queryVector,
        string? userId = null,
        int topK = 10,
        string? lexicalTerm = null,
        string? namespacePath = null,
        bool includeContentChunks = false)
    {
        if (!Capabilities.SupportsVector)
            throw new NotSupportedException(
                "The connected Snowflake endpoint does not support the VECTOR type " +
                "(SnowflakeCapabilities.SupportsVector = false); vector search cannot be generated. " +
                "Callers must stay on the lexical query path for this endpoint.");

        var parameters = new Dictionary<string, object>();
        var vectorLiteral = VectorLiteral(queryVector);
        var meshDistance = $"(1 - VECTOR_COSINE_SIMILARITY(n.\"embedding\", {vectorLiteral}))";

        // Namespace-prefix predicate — applied PER BRANCH inside the generator (not post-injected by a
        // string Replace("WHERE", …) downstream, which would corrupt a UNION's two WHERE keywords).
        var hasNamespace = !string.IsNullOrEmpty(namespacePath);
        if (hasNamespace)
            parameters["nsPrefix"] = $"{namespacePath!.Trim('/')}/";

        // 🔀 HYBRID recall: when the user typed a term, a row is eligible if it has an embedding
        // (semantic neighbour) OR it lexically matches the term on name/id/description — so turning on
        // an embedding provider NEVER hides un-embedded content that is still findable lexically. Pure-
        // semantic queries (no term) keep the embedding-only filter. The lexical tier in the ORDER BY
        // then ranks exact/prefix name matches ahead of pure-semantic neighbours. The tier's LIKE
        // patterns are term-verbatim (no EscapeLikePattern → no ESCAPE clause), matching PG.
        var hasLex = !string.IsNullOrEmpty(lexicalTerm);
        if (hasLex) parameters["lexTerm"] = lexicalTerm!;
        var lexMarker = Marker("lexTerm");
        var lexMatch =
            $"LOWER(COALESCE(n.\"name\",'')) LIKE '%' || LOWER({lexMarker}) || '%' " +
            $"OR LOWER(COALESCE(n.\"id\",'')) LIKE '%' || LOWER({lexMarker}) || '%' " +
            $"OR LOWER(COALESCE(n.\"description\",'')) LIKE '%' || LOWER({lexMarker}) || '%'";

        // ── Branch 1: existing mesh nodes (real embeddings on mesh_nodes) ──
        var meshTable = QualifyTable("mesh_nodes");
        var meshClauses = new List<string>
        {
            hasLex ? $"(n.\"embedding\" IS NOT NULL OR {lexMatch})" : "n.\"embedding\" IS NOT NULL"
        };

        if (filterQuery != null)
        {
            var (whereClause, filterParams) = GenerateWhereClause(filterQuery, userId);
            if (!string.IsNullOrEmpty(whereClause))
            {
                // Strip "WHERE " prefix since we're building our own WHERE
                meshClauses.Add(whereClause[6..]);
                foreach (var (k, v) in filterParams)
                    parameters[k] = v;
            }
        }
        else if (!string.IsNullOrEmpty(userId))
        {
            meshClauses.Add(GenerateAccessControlClause(userId));
            foreach (var (k, v) in _parameters)
                parameters[k] = v;
        }

        if (hasNamespace)
            meshClauses.Add($"n.\"path\" LIKE {Marker("nsPrefix")} || '%'");

        var branch1 = new StringBuilder(
            $"SELECT {NodeColumnsPrefix}, n.\"last_modified\", n.\"version\", n.\"state\", n.\"content\", " +
            $"n.\"desired_id\", n.\"main_node\", n.\"sync_behavior\", {meshDistance} AS \"_distance\" FROM {meshTable} n WHERE ");
        branch1.Append(string.Join(" AND ", meshClauses));

        // No content branch → keep the original single-branch shape (cosine, or lexical-tier+cosine).
        if (!includeContentChunks)
        {
            // Hybrid rank: when a lexical term is present, lexical exact/prefix matches outrank
            // pure semantic neighbours and cosine distance breaks ties WITHIN a tier. A user
            // typing an exact node name must get it first even if another node is semantically
            // closer. No term (pure semantic search) → cosine only, unchanged.
            if (hasLex)
            {
                branch1.Append(
                    " ORDER BY (CASE " +
                    $"WHEN LOWER(COALESCE(n.\"name\",'')) = LOWER({lexMarker}) THEN 0 " +
                    $"WHEN LOWER(COALESCE(n.\"name\",'')) LIKE LOWER({lexMarker}) || '%' THEN 1 " +
                    $"WHEN LOWER(COALESCE(n.\"id\",'')) LIKE LOWER({lexMarker}) || '%' THEN 2 " +
                    $"WHEN LOWER(COALESCE(n.\"name\",'')) LIKE '%' || LOWER({lexMarker}) || '%' THEN 3 " +
                    $"ELSE 4 END), {meshDistance}");
            }
            else
            {
                branch1.Append($" ORDER BY {meshDistance}");
            }
            branch1.Append($" LIMIT {topK}");
            return (branch1.ToString(), parameters);
        }

        // ── Branch 2: indexed content_chunks, each file's best-matching chunk projected to its
        //    synthetic Document mesh node. Path convention (slug, _Documents namespace) replicates
        //    DocumentPaths.For/Slug — see SlugSql. QUALIFY ROW_NUMBER (the DISTINCT ON translation)
        //    keeps ONE Document row per file (a file yields many chunks), the closest chunk winning.
        //    The 15+1 projected columns align positionally with branch 1 so ReadMeshNode materializes
        //    a valid MeshNode (state = 2 Active survives the structural filter). ──
        var contentTable = QualifyTable("content_chunks");
        var ccDistance = $"(1 - VECTOR_COSINE_SIMILARITY(cc.\"embedding\", {vectorLiteral}))";
        var contentWhere = "cc.\"embedding\" IS NOT NULL";
        if (hasNamespace)
            contentWhere += $" AND (cc.\"collection_path\" || '/_Documents/' || {SlugSql}) LIKE {Marker("nsPrefix")} || '%'";

        // QUALIFY may reference the select alias (it is evaluated after SELECT); the no-QUALIFY
        // derived-table fallback repeats the distance expression in the window ORDER BY instead of
        // relying on lateral alias support.
        var contentProjection = $"{DocumentProjection}, {ccDistance} AS \"_distance\"";
        var contentArm = Capabilities.SupportsQualify
            ? $"(SELECT {contentProjection} FROM {contentTable} cc WHERE {contentWhere} " +
              "QUALIFY ROW_NUMBER() OVER (PARTITION BY cc.\"collection_path\", cc.\"file_path\" " +
              "ORDER BY \"_distance\") = 1)"
            : BuildDedupedContentArm(
                contentProjection, contentTable, contentWhere, ccDistance, extraProjected: "_distance");

        // Unified ranking is cosine distance only (no lexical tier carried across the UNION) — the
        // wrapped outer ORDER BY ranks both branches by "_distance" ASC and the LIMIT keeps the
        // closest. The content arm is parenthesized so it stays a self-contained UNION branch.
        var sql =
            $"SELECT * FROM ({branch1} UNION ALL {contentArm}) u ORDER BY u.\"_distance\" ASC LIMIT {topK}";
        return (sql, parameters);
    }

    #region Scope Clauses

    private static string GenerateExactClause(string path, Dictionary<string, object> parameters)
    {
        parameters["scopePath"] = path;
        return $"n.\"path\" = {Marker("scopePath")}";
    }

    private static string GenerateChildrenClause(string path, Dictionary<string, object> parameters)
    {
        parameters["scopeNs"] = path;
        return $"n.\"namespace\" = {Marker("scopeNs")}";
    }

    private static string GenerateDescendantsClause(string path, Dictionary<string, object> parameters)
    {
        if (string.IsNullOrEmpty(path))
            return ""; // descendants of root = all nodes in schema, no path filter needed
        parameters["scopePrefix"] = $"{path}/";
        return $"n.\"path\" LIKE {Marker("scopePrefix")} || '%'";
    }

    private static string GenerateSubtreeClause(string path, Dictionary<string, object> parameters)
    {
        if (string.IsNullOrEmpty(path))
            return ""; // subtree of root = all nodes in schema, no path filter needed
        parameters["scopePath"] = path;
        parameters["scopePrefix"] = $"{path}/";
        return $"(n.\"path\" = {Marker("scopePath")} OR n.\"path\" LIKE {Marker("scopePrefix")} || '%')";
    }

    /// <summary>
    /// The <see cref="QueryScope.NextLevel"/> "populated frontier" — a single anti-join: a node is
    /// returned when it is a strict descendant of <paramref name="path"/> AND no other active node
    /// in the same table sits strictly between it and <paramref name="path"/>. This is what skips
    /// empty intermediate namespace segments (e.g. <c>a/b/node</c> surfaces directly at the root).
    /// One indexed query — no N+1 child-count probes.
    ///
    /// <para><paramref name="qualifiedTable"/> is the (already quoted) table the outer query reads
    /// (the anti-join must reference the same relation). When it's null — cross-schema / unscoped
    /// callers can't name one table — we degrade to plain descendants (documented: NextLevel is a
    /// within-partition scope).</para>
    /// </summary>
    private static string GenerateNextLevelClause(
        string path, string? qualifiedTable, Dictionary<string, object> parameters)
    {
        if (string.IsNullOrEmpty(qualifiedTable))
            return GenerateDescendantsClause(path, parameters);

        string nPrefix, ancPrefix;
        if (string.IsNullOrEmpty(path))
        {
            // Frontier of the whole schema = nodes with no active ancestor at all.
            nPrefix = "n.\"path\" <> ''";
            ancPrefix = "anc.\"path\" <> ''";
        }
        else
        {
            parameters["scopePrefix"] = $"{path}/";
            nPrefix = $"n.\"path\" LIKE {Marker("scopePrefix")} || '%'";
            ancPrefix = $"anc.\"path\" LIKE {Marker("scopePrefix")} || '%'";
        }

        return $"({nPrefix} AND NOT EXISTS (" +
               $"SELECT 1 FROM {qualifiedTable} anc " +
               $"WHERE anc.\"state\" = 2 AND anc.\"path\" <> n.\"path\" AND {ancPrefix} " +
               "AND n.\"path\" LIKE anc.\"path\" || '/%'))";
    }

    private static string GenerateAncestorsClause(string path, Dictionary<string, object> parameters)
    {
        var ancestors = GetAncestorPaths(path);
        if (ancestors.Length == 0)
            return "FALSE";

        var paramNames = new List<string>();
        for (var i = 0; i < ancestors.Length; i++)
        {
            var paramName = $"ancestor{i}";
            parameters[paramName] = ancestors[i];
            paramNames.Add(Marker(paramName));
        }
        return $"n.\"path\" IN ({string.Join(", ", paramNames)})";
    }

    private static string GenerateAncestorsAndSelfClause(string path, Dictionary<string, object> parameters)
    {
        var ancestors = GetAncestorPaths(path);
        var allPaths = ancestors.Append(path).ToArray();

        var paramNames = new List<string>();
        for (var i = 0; i < allPaths.Length; i++)
        {
            var paramName = $"ancestor{i}";
            parameters[paramName] = allPaths[i];
            paramNames.Add(Marker(paramName));
        }
        return $"n.\"path\" IN ({string.Join(", ", paramNames)})";
    }

    private static string GenerateHierarchyClause(string path, Dictionary<string, object> parameters)
    {
        var ancestors = GetAncestorPaths(path);

        var paramNames = new List<string>();
        for (var i = 0; i < ancestors.Length; i++)
        {
            var paramName = $"ancestor{i}";
            parameters[paramName] = ancestors[i];
            paramNames.Add(Marker(paramName));
        }

        var selfParam = $"ancestor{ancestors.Length}";
        parameters[selfParam] = path;
        paramNames.Add(Marker(selfParam));

        parameters["scopePrefix"] = $"{path}/";

        var ancestorsClause = $"n.\"path\" IN ({string.Join(", ", paramNames)})";
        var descendantsClause = $"n.\"path\" LIKE {Marker("scopePrefix")} || '%'";

        return $"({ancestorsClause} OR {descendantsClause})";
    }

    #endregion

    #region Filter Clauses

    private string GenerateNodeClause(QueryNode node)
    {
        return node switch
        {
            QueryComparison comparison => GenerateComparisonClause(comparison.Condition),
            QueryAnd and => GenerateAndClause(and),
            QueryOr or => GenerateOrClause(or),
            _ => ""
        };
    }

    private string GenerateComparisonClause(QueryCondition condition)
    {
        var selector = MapSelector(condition.Selector);

        string NextParam()
        {
            return $"p{_paramIndex++}";
        }

        switch (condition.Operator)
        {
            case QueryOperator.Equal:
            {
                var paramName = NextParam();
                if (IsTextField(condition.Selector))
                {
                    _parameters[paramName] = condition.Value.ToLowerInvariant();
                    return $"LOWER({selector}) = {Marker(paramName)}";
                }
                _parameters[paramName] = ConvertValue(condition.Selector, condition.Value);
                return $"{selector} = {Marker(paramName)}";
            }

            case QueryOperator.NotEqual:
            {
                var paramName = NextParam();
                // 🚨 SQL three-valued logic: NULL != 'x' evaluates to NULL,
                // not TRUE — so a bare `{sel} != {val}` filter silently drops
                // every row where {sel} is NULL. For negated filters this is
                // almost never what the user wants (e.g. `-content.status:Done`
                // should INCLUDE threads created before Done existed where
                // content.status is absent → null). Coalesce with IS NULL so
                // null is treated as "definitely not equal".
                if (IsTextField(condition.Selector))
                {
                    _parameters[paramName] = condition.Value.ToLowerInvariant();
                    return $"(LOWER({selector}) != {Marker(paramName)} OR {selector} IS NULL)";
                }
                _parameters[paramName] = ConvertValue(condition.Selector, condition.Value);
                return $"({selector} != {Marker(paramName)} OR {selector} IS NULL)";
            }

            case QueryOperator.GreaterThan:
            {
                var paramName = NextParam();
                _parameters[paramName] = ConvertValue(condition.Value);
                return IsVariantField(condition.Selector)
                    ? $"TRY_CAST({selector} AS NUMBER) > {Marker(paramName)}"
                    : $"{selector} > {Marker(paramName)}";
            }

            case QueryOperator.LessThan:
            {
                var paramName = NextParam();
                _parameters[paramName] = ConvertValue(condition.Value);
                return IsVariantField(condition.Selector)
                    ? $"TRY_CAST({selector} AS NUMBER) < {Marker(paramName)}"
                    : $"{selector} < {Marker(paramName)}";
            }

            case QueryOperator.GreaterOrEqual:
            {
                var paramName = NextParam();
                _parameters[paramName] = ConvertValue(condition.Value);
                return IsVariantField(condition.Selector)
                    ? $"TRY_CAST({selector} AS NUMBER) >= {Marker(paramName)}"
                    : $"{selector} >= {Marker(paramName)}";
            }

            case QueryOperator.LessOrEqual:
            {
                var paramName = NextParam();
                _parameters[paramName] = ConvertValue(condition.Value);
                return IsVariantField(condition.Selector)
                    ? $"TRY_CAST({selector} AS NUMBER) <= {Marker(paramName)}"
                    : $"{selector} <= {Marker(paramName)}";
            }

            case QueryOperator.Like:
            {
                var paramName = NextParam();
                var pattern = condition.Value.Replace("*", "%");
                if (!pattern.Contains('%'))
                    pattern = $"%{pattern}%";
                // The user's wildcard pattern is bound verbatim (PG parity — its metacharacters
                // are the point), so no ESCAPE clause is declared.
                _parameters[paramName] = pattern;
                return IlikeClause(selector, Marker(paramName));
            }

            case QueryOperator.In:
                var isTextIn = IsTextField(condition.Selector);
                var inParams = new List<string>();
                foreach (var value in condition.Values)
                {
                    var inParamName = NextParam();
                    _parameters[inParamName] = isTextIn ? value.ToLowerInvariant() : ConvertValue(condition.Selector, value);
                    inParams.Add(Marker(inParamName));
                }
                return isTextIn
                    ? $"LOWER({selector}) IN ({string.Join(", ", inParams)})"
                    : $"{selector} IN ({string.Join(", ", inParams)})";

            case QueryOperator.NotIn:
                var isTextNotIn = IsTextField(condition.Selector);
                var notInParams = new List<string>();
                foreach (var value in condition.Values)
                {
                    var notInParamName = NextParam();
                    _parameters[notInParamName] = isTextNotIn ? value.ToLowerInvariant() : ConvertValue(condition.Selector, value);
                    notInParams.Add(Marker(notInParamName));
                }
                // 🚨 Same null-handling as NotEqual: NULL NOT IN (...) is NULL,
                // not TRUE — coalesce with IS NULL so rows with null selector
                // are not silently filtered out of negated lookups.
                return isTextNotIn
                    ? $"(LOWER({selector}) NOT IN ({string.Join(", ", notInParams)}) OR {selector} IS NULL)"
                    : $"({selector} NOT IN ({string.Join(", ", notInParams)}) OR {selector} IS NULL)";

            default:
                return "";
        }
    }

    private string GenerateAndClause(QueryAnd and)
    {
        var clauses = and.Children
            .Select(GenerateNodeClause)
            .Where(c => !string.IsNullOrEmpty(c))
            .ToList();

        if (clauses.Count == 0) return "";
        if (clauses.Count == 1) return clauses[0];
        return $"({string.Join(" AND ", clauses)})";
    }

    private string GenerateOrClause(QueryOr or)
    {
        var clauses = or.Children
            .Select(GenerateNodeClause)
            .Where(c => !string.IsNullOrEmpty(c))
            .ToList();

        if (clauses.Count == 0) return "";
        if (clauses.Count == 1) return clauses[0];
        return $"({string.Join(" OR ", clauses)})";
    }

    private string GenerateTextSearchClause(string textSearch)
    {
        // Split into terms — ALL terms must match as substrings (mirrors InMemory QueryEvaluator
        // behavior). ILIKE substring matching throughout (the tsvector constructs of the PG stored
        // proc are not ported). Each term is LIKE-escaped and the escape character declared
        // explicitly — Snowflake has NO default escape character — unless the endpoint lacks
        // LIKE … ESCAPE, in which case the raw term is bound (degraded matching for terms that
        // contain LIKE metacharacters).
        var terms = textSearch.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0)
            return "";

        var textExpr = "COALESCE(n.\"name\",'') || ' ' || COALESCE(n.\"path\",'') || ' ' || " +
                       "COALESCE(n.\"description\",'') || ' ' || COALESCE(n.\"node_type\",'')";
        var clauses = new List<string>();

        foreach (var term in terms)
        {
            var paramName = $"p{_paramIndex++}";
            _parameters[paramName] = Capabilities.SupportsLikeEscape
                ? $"%{EscapeLikePattern(term)}%"
                : $"%{term}%";
            clauses.Add($"{IlikeClause(textExpr, Marker(paramName))}{LikeEscapeSuffix}");
        }

        return clauses.Count == 1 ? clauses[0] : $"({string.Join(" AND ", clauses)})";
    }

    /// <summary>
    /// Escapes special LIKE/ILIKE pattern characters (%, _, \) in user input. Any pattern built
    /// with this MUST carry the explicit <c>ESCAPE '\\'</c> declaration (see
    /// <see cref="LikeEscapeSuffix"/>) — unlike PostgreSQL, Snowflake has no default escape character.
    /// </summary>
    private static string EscapeLikePattern(string input)
    {
        return input
            .Replace("\\", "\\\\")
            .Replace("%", "\\%")
            .Replace("_", "\\_");
    }

    /// <summary>Doubles single quotes for values inlined as SQL string literals.</summary>
    private static string EscapeSqlLiteral(string input) => input.Replace("'", "''");

    private string GenerateAccessControlClause(string userId)
    {
        var paramName = $"acUser{_paramIndex++}";
        _parameters[paramName] = userId;
        var marker = Marker(paramName);
        // Anonymous users only get their own permissions (no Public inheritance).
        // All other users also inherit Public permissions as a baseline floor.
        // Node types marked as public_read in node_type_permissions are visible to authenticated users.
        var userList = userId == WellKnownUsers.Anonymous
            ? marker
            : $"{marker}, 'Public'";

        var uepTable = QualifyTable("user_effective_permissions");
        var ntpTable = QualifyTable("node_type_permissions");

        // Partition-level access check (only for schema-qualified queries).
        // partition_access controls which schemas the user can see.
        // Public-read node types bypass the partition check — they're visible to all authenticated users.
        var hasPartitionCheck = !string.IsNullOrEmpty(SchemaName);
        var partitionAccessExists = hasPartitionCheck
            ? $"EXISTS (SELECT 1 FROM {PartitionAccessTable} pa WHERE pa.\"user_id\" IN ({userList}) AND pa.\"partition\" = '{EscapeSqlLiteral(SchemaName!)}')"
            : "";

        // Public-read node types (e.g. User, Markdown) are visible to all authenticated users
        // who have partition access. public_read skips node-level permission checks but
        // still requires partition_access — prevents cross-partition data leakage.
        var publicReadClause = userId == WellKnownUsers.Anonymous
            ? ""
            : $"EXISTS (SELECT 1 FROM {ntpTable} ntp WHERE ntp.\"node_type\" = n.\"node_type\" AND ntp.\"public_read\" = true)";

        // Build the access control clause:
        // A node is visible if the user has partition access (when schema-qualified) AND:
        //   (a) public-read node type (no further permission check), OR
        //   (b) owns the node OR has Read permission (longest matching prefix wins,
        //       own row breaks ties — see BuildNodeAccessPredicate).
        var nodeAccessClause = BuildNodeAccessPredicate(uepTable, marker, userList);

        if (hasPartitionCheck)
        {
            // Schema-qualified: partition_access is always required.
            // public_read skips node-level checks but NOT partition access.
            if (!string.IsNullOrEmpty(publicReadClause))
            {
                return $"""
                    (
                        {partitionAccessExists} AND ({publicReadClause} OR {nodeAccessClause})
                    )
                    """;
            }

            return $"""
                (
                    {partitionAccessExists} AND ({nodeAccessClause})
                )
                """;
        }

        // No schema: just node-level access (or public-read bypass)
        if (!string.IsNullOrEmpty(publicReadClause))
        {
            return $"""
                (
                    {publicReadClause}
                    OR {nodeAccessClause}
                )
                """;
        }

        return $"({nodeAccessClause})";
    }

    #endregion

    /// <summary>
    /// Whether a selector resolves to a variant content extraction (as opposed to a typed column) —
    /// such selectors get <c>TRY_CAST(… AS NUMBER)</c> for numeric comparisons.
    /// </summary>
    private static bool IsVariantField(string selector) =>
        !PropertyMap.ContainsKey(selector);

    /// <summary>
    /// Selector-aware conversion: the <c>state</c> column is a smallint backing
    /// <c>MeshNodeState</c>, so symbolic values in queries (<c>state:Active</c>)
    /// must map to the enum's numeric value — sending the raw string would make the
    /// numeric comparison fail on the typed column.
    /// </summary>
    private static object ConvertValue(string selector, string value)
    {
        if (selector.Equals("state", StringComparison.OrdinalIgnoreCase)
            && Enum.TryParse<MeshNodeState>(value, ignoreCase: true, out var state))
            return (short)state;
        return ConvertValue(value);
    }

    /// <summary>
    /// Converts a query literal to its natural CLR type for binding. Date/time values parse to
    /// <see cref="DateTimeOffset"/>; the storage layer stores timestamps as <c>TIMESTAMP_NTZ</c>
    /// holding UTC, so the adapter binds these as UTC instants.
    /// </summary>
    private static object ConvertValue(string value)
    {
        if (bool.TryParse(value, out var boolVal))
            return boolVal;

        if (long.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var longVal))
            return longVal;

        if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var doubleVal))
            return doubleVal;

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dateVal))
            return dateVal;

        return value;
    }

    /// <summary>
    /// Returns the ancestor paths of <paramref name="path"/>, from the top-level segment down to the
    /// immediate parent (excluding <paramref name="path"/> itself). Returns an empty array for a root or empty path.
    /// </summary>
    /// <param name="path">The slash-separated node path to derive ancestors from.</param>
    /// <returns>The ancestor paths, ordered shallowest-first.</returns>
    public static string[] GetAncestorPaths(string path)
    {
        if (string.IsNullOrEmpty(path))
            return [];

        var segments = path.Split('/');
        var ancestors = new List<string>();
        for (var i = 1; i < segments.Length; i++)
            ancestors.Add(string.Join("/", segments.Take(i)));
        return ancestors.ToArray();
    }
}
