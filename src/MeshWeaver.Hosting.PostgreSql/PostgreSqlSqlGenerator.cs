using System.Globalization;
using System.Text;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using Pgvector;

namespace MeshWeaver.Hosting.PostgreSql;

/// <summary>
/// Generates PostgreSQL SQL queries from parsed query AST.
/// </summary>
public class PostgreSqlSqlGenerator
{
    private int _paramIndex;
    private readonly Dictionary<string, object> _parameters = new();

    /// <summary>
    /// PostgreSQL schema name for qualifying access control tables.
    /// When set, tables like user_effective_permissions become "schema"."table".
    /// </summary>
    public string? SchemaName { get; init; }

    private string QualifyTable(string table)
        => string.IsNullOrEmpty(SchemaName) ? table : $"\"{SchemaName}\".\"{table}\"";

    private static readonly Dictionary<string, string> PropertyMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["name"] = "n.name",
        ["nodeType"] = "n.node_type",
        ["node_type"] = "n.node_type",
        ["description"] = "n.description",
        ["category"] = "n.category",
        ["icon"] = "n.icon",
        ["order"] = "n.display_order",
        ["display_order"] = "n.display_order",
        ["lastModified"] = "n.last_modified",
        ["last_modified"] = "n.last_modified",
        ["version"] = "n.version",
        ["state"] = "n.state",
        ["id"] = "n.id",
        ["namespace"] = "n.namespace",
        ["path"] = "n.path",
        ["mainNode"] = "n.main_node",
        ["main_node"] = "n.main_node"
    };

    /// <summary>
    /// Non-text columns where case-insensitive comparison should NOT be applied.
    /// Everything else (text columns + JSONB content fields extracted via ->>) is treated as text.
    /// </summary>
    private static readonly HashSet<string> NonTextColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "order", "display_order", "version", "state", "lastModified", "last_modified"
    };

    private static bool IsTextField(string selector) =>
        !NonTextColumns.Contains(selector);

    /// <summary>
    /// Maps a selector from the query AST to a PostgreSQL column expression.
    /// </summary>
    public static string MapSelector(string selector)
    {
        if (PropertyMap.TryGetValue(selector, out var mapped))
            return mapped;

        // content.X.Y → n.content->'X'->>'Y'
        if (selector.StartsWith("content.", StringComparison.OrdinalIgnoreCase))
        {
            var parts = selector.Split('.');
            if (parts.Length == 2)
                return $"n.content->>'{parts[1]}'";

            var sb = new StringBuilder("n.content");
            for (var i = 1; i < parts.Length - 1; i++)
                sb.Append($"->'{parts[i]}'");
            sb.Append($"->>'{parts[^1]}'");
            return sb.ToString();
        }

        // Unknown selectors fall back to JSONB content
        return $"n.content->>'{selector}'";
    }

    /// <summary>
    /// Maps a selector for ORDER BY (returns typed column or JSONB extraction).
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

        if (PropertyMap.TryGetValue(selector, out var mapped))
            return mapped;

        if (selector.StartsWith("content.", StringComparison.OrdinalIgnoreCase))
        {
            var parts = selector.Split('.');
            if (parts.Length == 2)
                return $"n.content->>'{parts[1]}'";

            var sb = new StringBuilder("n.content");
            for (var i = 1; i < parts.Length - 1; i++)
                sb.Append($"->'{parts[i]}'");
            sb.Append($"->>'{parts[^1]}'");
            return sb.ToString();
        }

        return $"n.content->>'{selector}'";
    }

    /// <summary>
    /// Allow-listed SQL functions usable in <c>sort:func(field)-desc</c>.
    /// Tight allow-list — no arbitrary SQL in the sort selector.
    /// </summary>
    private static readonly HashSet<string> AllowedSqlFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "length", "lower", "upper"
    };

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
            clauses.Add("n.main_node = n.path");
        }

        // Context-based exclusion: exclude node types that are configured to be
        // hidden from the given context (e.g., Role, User excluded from "search").
        if (excludedNodeTypes is { Count: > 0 })
        {
            var paramNames = new List<string>();
            foreach (var nt in excludedNodeTypes)
            {
                var p = $"@p{_paramIndex++}";
                _parameters[p] = nt;
                paramNames.Add(p);
            }
            clauses.Add($"(n.node_type IS NULL OR n.node_type NOT IN ({string.Join(", ", paramNames)}))");
        }

        // Only return Active nodes (state=2) — excludes Transient and Deleted
        clauses.Add("n.state = 2");

        var whereClause = clauses.Count > 0
            ? "WHERE " + string.Join(" AND ", clauses)
            : "";

        return (whereClause, new Dictionary<string, object>(_parameters));
    }

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

        // n.content is a JSONB column that can be many KB. When the caller projects via
        // `select:` and didn't ask for "content", emit NULL::jsonb AS content so the
        // result-set shape is unchanged (ReadMeshNode still finds the column) but the
        // planner avoids the heap fetch + de-tuple of large blobs.
        var contentColumn = includeContent ? "n.content" : "NULL::jsonb AS content";

        var sql = new StringBuilder("SELECT n.id, n.namespace, n.name, n.node_type, n.description, " +
            $"n.category, n.icon, n.display_order, n.last_modified, n.version, n.state, {contentColumn}, " +
            $"n.desired_id, n.main_node FROM {tableName} n");

        if (isAccessedQuery)
        {
            // JOIN with UserActivity nodes stored in the user_activities
            // satellite table — they live under {userId}/_UserActivity per the
            // post-v10 per-user partition layout.
            parameters["@actUserNs"] = $"{activityUserId}/_UserActivity";
            sql.Append($" INNER JOIN {userActivityTable} ua ON ua.namespace = @actUserNs" +
                        " AND ua.node_type = 'UserActivity'" +
                        " AND REPLACE(n.path, '/', '_') = ua.id");
        }
        else if (isActivityQuery)
        {
            // JOIN with Activity satellites stored in the activities satellite table
            sql.Append($" INNER JOIN {activityTable} act ON act.main_node = n.path" +
                        " AND act.node_type = 'Activity'");
        }

        // Both source queries restrict to main content nodes only (main_node = path)
        if (isActivityQuery || isAccessedQuery)
        {
            var mainNodeFilter = "n.main_node = n.path";
            whereClause = string.IsNullOrEmpty(whereClause)
                ? $"WHERE {mainNodeFilter}"
                : $"{whereClause} AND {mainNodeFilter}";
        }

        if (!string.IsNullOrEmpty(whereClause))
            sql.Append($" {whereClause}");

        if (isAccessedQuery)
        {
            sql.Append(" ORDER BY ua.last_modified DESC NULLS LAST");
        }
        else if (isActivityQuery && query.OrderBy == null)
        {
            // Default ordering: most recent activity first
            sql.Append(" ORDER BY act.content->>'Start' DESC NULLS LAST");
        }
        else if (query.OrderBy != null)
        {
            var direction = query.OrderBy.Descending ? "DESC" : "ASC";
            sql.Append($" ORDER BY {MapOrderBySelector(query.OrderBy.Property)} {direction}");
        }
        else if (!string.IsNullOrEmpty(query.TextSearch))
        {
            // PG-side relevance parity with GenerateCrossSchemaSelectQuery (#20): rank by a
            // hybrid score so the LIMIT keeps the most-RELEVANT rows, not arbitrary heap order
            // (a relevant row could otherwise fall outside the LIMIT before any C#-side
            // ComputeRowScores re-rank ever sees it). Same ladder: exact name > name-prefix >
            // id-prefix > name-substring > id-substring > description-substring. An explicit
            // OrderBy (handled above) supersedes. Single-schema keeps the `n` alias (no UNION
            // wrap) and parameterises the term (unlike the inlined cross-schema generator).
            parameters["@scoreText"] = query.TextSearch;
            sql.Append(
                " ORDER BY (CASE " +
                "WHEN LOWER(COALESCE(n.name,'')) = LOWER(@scoreText) THEN 1000 " +
                "WHEN LOWER(COALESCE(n.name,'')) LIKE LOWER(@scoreText) || '%' THEN 600 " +
                "WHEN LOWER(COALESCE(n.id,'')) LIKE LOWER(@scoreText) || '%' THEN 500 " +
                "WHEN LOWER(COALESCE(n.name,'')) LIKE '%' || LOWER(@scoreText) || '%' THEN 300 " +
                "WHEN LOWER(COALESCE(n.id,'')) LIKE '%' || LOWER(@scoreText) || '%' THEN 200 " +
                "WHEN LOWER(COALESCE(n.description,'')) LIKE '%' || LOWER(@scoreText) || '%' THEN 100 " +
                "ELSE 0 END) DESC, n.last_modified DESC NULLS LAST");
        }

        if (query.Limit.HasValue)
            sql.Append($" LIMIT {query.Limit.Value}");

        return (sql.ToString(), parameters);
    }

    /// <summary>
    /// Multi-path overload — emits <c>n.path IN (@p0, @p1, ...)</c> for the
    /// <see cref="QueryScope.Exact"/> + multi-value <c>path:a|b|c</c> case
    /// (canonical use: routing-layer "longest-matching-prefix" lookup with
    /// <c>sort:pathLength-desc limit:1</c>). Other scopes / single-path values
    /// fall through to the single-path overload.
    /// </summary>
    public (string Clause, Dictionary<string, object> Parameters) GenerateScopeClause(
        IReadOnlyList<string>? paths, QueryScope scope, bool useMainNode = false)
    {
        if (paths == null || paths.Count <= 1)
            return GenerateScopeClause(paths is { Count: 1 } ? paths[0] : null, scope, useMainNode);

        // Only Exact scope supports multi-path push-down today. Subtree/Children/etc.
        // would require OR-ing N LIKE clauses — caller can either emit those itself
        // or stick to single-path for non-Exact scopes.
        if (scope != QueryScope.Exact)
            return GenerateScopeClause(paths[0], scope, useMainNode);

        var parameters = new Dictionary<string, object>();
        var paramNames = new List<string>(paths.Count);
        for (var i = 0; i < paths.Count; i++)
        {
            var name = $"@scopePath{i}";
            paramNames.Add(name);
            parameters[name] = paths[i].Trim('/');
        }
        var column = useMainNode ? "n.main_node" : "n.path";
        var clause = $"{column} IN ({string.Join(", ", paramNames)})";
        return (clause, parameters);
    }

    public (string Clause, Dictionary<string, object> Parameters) GenerateScopeClause(
        string? basePath, QueryScope scope, bool useMainNode = false)
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
            _ => ""
        };

        return (clause, parameters);
    }

    private static string GenerateMainNodeExactClause(string path, Dictionary<string, object> parameters)
    {
        parameters["@scopeMain"] = path;
        return "n.main_node = @scopeMain";
    }

    private static string GenerateMainNodeDescendantsClause(string path, Dictionary<string, object> parameters)
    {
        parameters["@scopeMainPrefix"] = $"{path}/";
        return "n.main_node LIKE @scopeMainPrefix || '%'";
    }

    private static string GenerateMainNodeSubtreeClause(string path, Dictionary<string, object> parameters)
    {
        parameters["@scopeMain"] = path;
        parameters["@scopeMainPrefix"] = $"{path}/";
        return "(n.main_node = @scopeMain OR n.main_node LIKE @scopeMainPrefix || '%')";
    }

    /// <summary>
    /// Generates a UNION ALL query across multiple schemas.
    /// Each schema gets the same WHERE clause but different schema-qualified table names.
    ///
    /// <para><paramref name="activityUserId"/> opts into the
    /// <c>source:activity</c> / <c>source:accessed</c> JOIN form: for activity,
    /// each schema's branch INNER JOINs <c>{schema}.activities</c> on
    /// <c>main_node = n.path</c>; for accessed, it JOINs
    /// <c>{schema}.user_activities</c> by the user's namespace. <c>is:main</c>
    /// is implied (<c>n.main_node = n.path</c>) and the default sort becomes
    /// the joined satellite's <c>last_modified</c> so activity-recency
    /// ordering survives the UNION.</para>
    /// </summary>
    public (string Sql, Dictionary<string, object> Parameters) GenerateCrossSchemaSelectQuery(
        ParsedQuery query,
        IReadOnlyList<string> schemas,
        string? userId = null,
        string tableName = "mesh_nodes",
        string? activityUserId = null)
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
        // surfaces a sibling instead of the requested node. Single-schema
        // queries don't hit this because PostgreSqlStorageAdapter.QueryAsyncInternal
        // post-injects the IN clause; cross-schema needs the same treatment.
        // Repro: ThreadUrlResolutionTest.ResolvePath_SatelliteByFullPath for
        // _Access (auto-grant + user grant) failed pre-fix.
        if (query.Paths is { Count: > 1 } && query.Scope == QueryScope.Exact)
        {
            var paramNames = new List<string>(query.Paths.Count);
            for (var i = 0; i < query.Paths.Count; i++)
            {
                var name = $"@xspath{i}";
                paramNames.Add(name);
                parameters[name] = query.Paths[i].Trim('/');
            }
            var pathInClause = $"n.path IN ({string.Join(", ", paramNames)})";
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
            parameters["@xspath0"] = query.Path.Trim('/');
            var pathEqClause = "n.path = @xspath0";
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
            // in the satellite table; the per-schema PostgreSqlStorageAdapter
            // path applies the scope clause via GenerateScopeClause but the
            // cross-schema path (engaged whenever ResolveTable != "mesh_nodes",
            // i.e. any namespace ending in a satellite segment like /Source
            // or any nodeType-routed satellite) did not.
            // Repro: SqlGeneratorTests.GenerateCrossSchemaSelectQuery_NamespaceSubtree_PushesDownPathFilter.
            // Prod symptom: namespace:Systemorph/EventCalendar/Source scope:subtree
            // nodeType:Code returned 47 Code rows across the whole Systemorph
            // partition (SocialMedia/, Post/, FutuRe/Pricing/, Event/, …)
            // instead of just the EventCalendar/Source subtree, breaking
            // NodeType compile of every page backed by EventCalendar.
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
            parameters["@actUserNs"] = $"{activityUserId}/_UserActivity";

        var parts = new List<string>();
        foreach (var schema in schemas)
        {
            var qualifiedTable = $"\"{schema}\".\"{tableName}\"";
            var activityTable = $"\"{schema}\".\"activities\"";
            var userActivityTable = $"\"{schema}\".\"user_activities\"";
            var uepTable = $"\"{schema}\".\"user_effective_permissions\"";
            var ntpTable = $"\"{schema}\".\"node_type_permissions\"";

            // For source:activity, project the JOINed activity's last_modified into
            // the same column slot so the outer ORDER BY ranks rows by activity recency.
            // For source:accessed, project the UserActivity row's last_modified the same way.
            // For plain queries, n.last_modified is fine.
            var lastModifiedExpr = isActivity ? "act.last_modified" : (isAccessed ? "ua.last_modified" : "n.last_modified");

            var selectSql = "SELECT n.id, n.namespace, n.name, n.node_type, n.description, " +
                $"n.category, n.icon, n.display_order, {lastModifiedExpr} AS last_modified, " +
                "n.version, n.state, n.content, " +
                $"n.desired_id, n.main_node FROM {qualifiedTable} n";

            if (isAccessed)
                selectSql += $" INNER JOIN {userActivityTable} ua ON ua.namespace = @actUserNs" +
                             " AND ua.node_type = 'UserActivity'" +
                             " AND REPLACE(n.path, '/', '_') = ua.id";
            else if (isActivity)
                selectSql += $" INNER JOIN {activityTable} act ON act.main_node = n.path" +
                             " AND act.node_type = 'Activity'";

            var accessClause = BuildPerSchemaAccessClause(userId, schema, uepTable, ntpTable, parameters);
            var mainNodeFilter = (isActivity || isAccessed) ? "n.main_node = n.path" : null;

            var clauses = new List<string>();
            if (!string.IsNullOrEmpty(whereCore)) clauses.Add(whereCore);
            if (mainNodeFilter is not null) clauses.Add(mainNodeFilter);
            if (!string.IsNullOrEmpty(accessClause)) clauses.Add(accessClause);
            var fullWhere = clauses.Count == 0 ? "" : "WHERE " + string.Join(" AND ", clauses);

            parts.Add($"{selectSql} {fullWhere}");
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
            sql = $"SELECT * FROM ({sql}) combined ORDER BY last_modified DESC NULLS LAST";
        }
        else if (!string.IsNullOrEmpty(query.TextSearch))
        {
            // #20 PG-side relevance: rank by a hybrid score so the LIMIT keeps the most-RELEVANT
            // rows, not arbitrary heap order (where a relevant row could fall outside the LIMIT
            // and the C# merge never sees it — a relevance bug). Score: exact name > name-prefix
            // > id-prefix > name-substring > id-substring > description-substring. Sort by score
            // DESC, NOT alphabetically; an explicit OrderBy (handled above) supersedes. Term
            // inlined (this generator inlines params); single-quotes doubled for safety.
            var t = query.TextSearch.Replace("'", "''");
            sql = $"SELECT * FROM ({sql}) combined ORDER BY (CASE " +
                $"WHEN LOWER(COALESCE(name,'')) = LOWER('{t}') THEN 1000 " +
                $"WHEN LOWER(COALESCE(name,'')) LIKE LOWER('{t}') || '%' THEN 600 " +
                $"WHEN LOWER(COALESCE(id,'')) LIKE LOWER('{t}') || '%' THEN 500 " +
                $"WHEN LOWER(COALESCE(name,'')) LIKE '%' || LOWER('{t}') || '%' THEN 300 " +
                $"WHEN LOWER(COALESCE(id,'')) LIKE '%' || LOWER('{t}') || '%' THEN 200 " +
                $"WHEN LOWER(COALESCE(description,'')) LIKE '%' || LOWER('{t}') || '%' THEN 100 " +
                "ELSE 0 END) DESC, last_modified DESC NULLS LAST";
        }

        if (query.Limit.HasValue)
            sql += $" LIMIT {query.Limit.Value}";

        return (sql, parameters);
    }

    private string BuildPerSchemaAccessClause(
        string? userId, string schema, string uepTable, string ntpTable,
        Dictionary<string, object> parameters)
    {
        if (string.IsNullOrEmpty(userId))
            return "";

        // Reuse existing param if already added, else create new
        var paramName = $"@acUser_cross";
        if (!parameters.ContainsKey(paramName))
            parameters[paramName] = userId;

        var userList = userId == MeshWeaver.Mesh.Security.WellKnownUsers.Anonymous
            ? paramName : $"{paramName}, 'Public'";

        var publicReadClause = userId == MeshWeaver.Mesh.Security.WellKnownUsers.Anonymous ? "" :
            $"EXISTS (SELECT 1 FROM {ntpTable} ntp WHERE ntp.node_type = n.node_type AND ntp.public_read = true)";

        var partitionAccessExists = $"EXISTS (SELECT 1 FROM public.partition_access pa WHERE pa.user_id IN ({userList}) AND pa.partition = '{schema}')";

        var nodeAccess = $"""
            n.main_node = {paramName}
            OR (SELECT uep.is_allow FROM {uepTable} uep
                WHERE uep.user_id IN ({userList}) AND uep.permission = 'Read'
                  AND n.main_node LIKE uep.node_path_prefix || '%'
                ORDER BY LENGTH(uep.node_path_prefix) DESC,
                         CASE WHEN uep.user_id = {paramName} THEN 0 ELSE 1 END
                LIMIT 1) = true
            """;

        if (!string.IsNullOrEmpty(publicReadClause))
            return $"({publicReadClause} OR ({partitionAccessExists} AND ({nodeAccess})))";

        return $"({partitionAccessExists} AND ({nodeAccess}))";
    }

    public (string Sql, Dictionary<string, object> Parameters) GenerateVectorSearchQuery(
        ParsedQuery? filterQuery,
        float[] queryVector,
        string? userId = null,
        int topK = 10)
    {
        var parameters = new Dictionary<string, object>();

        var meshTable = QualifyTable("mesh_nodes");
        var sql = new StringBuilder(
            "SELECT n.id, n.namespace, n.name, n.node_type, n.description, " +
            "n.category, n.icon, n.display_order, n.last_modified, n.version, n.state, n.content, " +
            $"n.desired_id, n.main_node FROM {meshTable} n");

        var clauses = new List<string> { "n.embedding IS NOT NULL" };

        if (filterQuery != null)
        {
            var (whereClause, filterParams) = GenerateWhereClause(filterQuery, userId);
            if (!string.IsNullOrEmpty(whereClause))
            {
                // Strip "WHERE " prefix since we're building our own WHERE
                clauses.Add(whereClause[6..]);
                foreach (var (k, v) in filterParams)
                    parameters[k] = v;
            }
        }
        else if (!string.IsNullOrEmpty(userId))
        {
            clauses.Add(GenerateAccessControlClause(userId));
            parameters = new Dictionary<string, object>(_parameters);
        }

        sql.Append(" WHERE ");
        sql.Append(string.Join(" AND ", clauses));

        parameters["@queryVector"] = new Vector(queryVector);
        sql.Append(" ORDER BY n.embedding <=> @queryVector");
        sql.Append($" LIMIT {topK}");

        return (sql.ToString(), parameters);
    }

    #region Scope Clauses

    private static string GenerateExactClause(string path, Dictionary<string, object> parameters)
    {
        parameters["@scopePath"] = path;
        return "n.path = @scopePath";
    }

    private static string GenerateChildrenClause(string path, Dictionary<string, object> parameters)
    {
        parameters["@scopeNs"] = path;
        return "n.namespace = @scopeNs";
    }

    private static string GenerateDescendantsClause(string path, Dictionary<string, object> parameters)
    {
        if (string.IsNullOrEmpty(path))
            return ""; // descendants of root = all nodes in schema, no path filter needed
        parameters["@scopePrefix"] = $"{path}/";
        return "n.path LIKE @scopePrefix || '%'";
    }

    private static string GenerateSubtreeClause(string path, Dictionary<string, object> parameters)
    {
        if (string.IsNullOrEmpty(path))
            return ""; // subtree of root = all nodes in schema, no path filter needed
        parameters["@scopePath"] = path;
        parameters["@scopePrefix"] = $"{path}/";
        return "(n.path = @scopePath OR n.path LIKE @scopePrefix || '%')";
    }

    private static string GenerateAncestorsClause(string path, Dictionary<string, object> parameters)
    {
        var ancestors = GetAncestorPaths(path);
        if (ancestors.Length == 0)
            return "FALSE";

        var paramNames = new List<string>();
        for (var i = 0; i < ancestors.Length; i++)
        {
            var paramName = $"@ancestor{i}";
            parameters[paramName] = ancestors[i];
            paramNames.Add(paramName);
        }
        return $"n.path IN ({string.Join(", ", paramNames)})";
    }

    private static string GenerateAncestorsAndSelfClause(string path, Dictionary<string, object> parameters)
    {
        var ancestors = GetAncestorPaths(path);
        var allPaths = ancestors.Append(path).ToArray();

        var paramNames = new List<string>();
        for (var i = 0; i < allPaths.Length; i++)
        {
            var paramName = $"@ancestor{i}";
            parameters[paramName] = allPaths[i];
            paramNames.Add(paramName);
        }
        return $"n.path IN ({string.Join(", ", paramNames)})";
    }

    private static string GenerateHierarchyClause(string path, Dictionary<string, object> parameters)
    {
        var ancestors = GetAncestorPaths(path);

        var paramNames = new List<string>();
        for (var i = 0; i < ancestors.Length; i++)
        {
            var paramName = $"@ancestor{i}";
            parameters[paramName] = ancestors[i];
            paramNames.Add(paramName);
        }

        var selfParam = $"@ancestor{ancestors.Length}";
        parameters[selfParam] = path;
        paramNames.Add(selfParam);

        parameters["@scopePrefix"] = $"{path}/";

        var ancestorsClause = $"n.path IN ({string.Join(", ", paramNames)})";
        var descendantsClause = "n.path LIKE @scopePrefix || '%'";

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
            return $"@p{_paramIndex++}";
        }

        switch (condition.Operator)
        {
            case QueryOperator.Equal:
            {
                var paramName = NextParam();
                if (IsTextField(condition.Selector))
                {
                    _parameters[paramName] = condition.Value.ToLowerInvariant();
                    return $"LOWER({selector}) = {paramName}";
                }
                _parameters[paramName] = ConvertValue(condition.Value);
                return $"{selector} = {paramName}";
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
                    return $"(LOWER({selector}) != {paramName} OR {selector} IS NULL)";
                }
                _parameters[paramName] = ConvertValue(condition.Value);
                return $"({selector} != {paramName} OR {selector} IS NULL)";
            }

            case QueryOperator.GreaterThan:
            {
                var paramName = NextParam();
                _parameters[paramName] = ConvertValue(condition.Value);
                return IsJsonbField(condition.Selector)
                    ? $"CAST({selector} AS numeric) > {paramName}"
                    : $"{selector} > {paramName}";
            }

            case QueryOperator.LessThan:
            {
                var paramName = NextParam();
                _parameters[paramName] = ConvertValue(condition.Value);
                return IsJsonbField(condition.Selector)
                    ? $"CAST({selector} AS numeric) < {paramName}"
                    : $"{selector} < {paramName}";
            }

            case QueryOperator.GreaterOrEqual:
            {
                var paramName = NextParam();
                _parameters[paramName] = ConvertValue(condition.Value);
                return IsJsonbField(condition.Selector)
                    ? $"CAST({selector} AS numeric) >= {paramName}"
                    : $"{selector} >= {paramName}";
            }

            case QueryOperator.LessOrEqual:
            {
                var paramName = NextParam();
                _parameters[paramName] = ConvertValue(condition.Value);
                return IsJsonbField(condition.Selector)
                    ? $"CAST({selector} AS numeric) <= {paramName}"
                    : $"{selector} <= {paramName}";
            }

            case QueryOperator.Like:
            {
                var paramName = NextParam();
                var pattern = condition.Value.Replace("*", "%");
                if (!pattern.Contains('%'))
                    pattern = $"%{pattern}%";
                _parameters[paramName] = pattern;
                return $"{selector} ILIKE {paramName}";
            }

            case QueryOperator.In:
                var isTextIn = IsTextField(condition.Selector);
                var inParams = new List<string>();
                foreach (var value in condition.Values)
                {
                    var inParamName = NextParam();
                    _parameters[inParamName] = isTextIn ? value.ToLowerInvariant() : ConvertValue(value);
                    inParams.Add(inParamName);
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
                    _parameters[notInParamName] = isTextNotIn ? value.ToLowerInvariant() : ConvertValue(value);
                    notInParams.Add(notInParamName);
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
        // Split into terms — ALL terms must match as substrings (mirrors InMemory QueryEvaluator behavior).
        // Uses ILIKE for substring/prefix matching instead of plainto_tsquery which only matches full words.
        var terms = textSearch.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0)
            return "";

        var textExpr = "COALESCE(n.name,'') || ' ' || COALESCE(n.path,'') || ' ' || COALESCE(n.description,'') || ' ' || COALESCE(n.node_type,'')";
        var clauses = new List<string>();

        foreach (var term in terms)
        {
            var paramName = $"@p{_paramIndex++}";
            _parameters[paramName] = $"%{EscapeLikePattern(term)}%";
            clauses.Add($"{textExpr} ILIKE {paramName}");
        }

        return clauses.Count == 1 ? clauses[0] : $"({string.Join(" AND ", clauses)})";
    }

    /// <summary>
    /// Escapes special LIKE/ILIKE pattern characters (%, _, \) in user input.
    /// </summary>
    private static string EscapeLikePattern(string input)
    {
        return input
            .Replace("\\", "\\\\")
            .Replace("%", "\\%")
            .Replace("_", "\\_");
    }

    private string GenerateAccessControlClause(string userId)
    {
        var paramName = $"@acUser{_paramIndex++}";
        _parameters[paramName] = userId;
        // Anonymous users only get their own permissions (no Public inheritance).
        // All other users also inherit Public permissions as a baseline floor.
        // NodeType definitions (node_type = 'NodeType') are always publicly readable.
        // Node types marked as public_read in node_type_permissions are visible to authenticated users.
        var userList = userId == WellKnownUsers.Anonymous
            ? $"{paramName}"
            : $"{paramName}, 'Public'";

        var uepTable = QualifyTable("user_effective_permissions");
        var ntpTable = QualifyTable("node_type_permissions");

        // Partition-level access check (only for schema-qualified queries).
        // partition_access controls which schemas the user can see.
        // Public-read node types bypass the partition check — they're visible to all authenticated users.
        var hasPartitionCheck = !string.IsNullOrEmpty(SchemaName);
        var partitionAccessExists = hasPartitionCheck
            ? $"EXISTS (SELECT 1 FROM public.partition_access pa WHERE pa.user_id IN ({userList}) AND pa.partition = '{SchemaName}')"
            : "";

        // Public-read node types (e.g. User, Markdown) are visible to all authenticated users
        // who have partition access. public_read skips node-level permission checks but
        // still requires partition_access — prevents cross-partition data leakage.
        var publicReadClause = userId == WellKnownUsers.Anonymous
            ? ""
            : $"EXISTS (SELECT 1 FROM {ntpTable} ntp WHERE ntp.node_type = n.node_type AND ntp.public_read = true)";

        // Build the access control clause:
        // A node is visible if the user has partition access (when schema-qualified) AND:
        //   (a) public-read node type (no further permission check), OR
        //   (b) owns the node OR has Read permission
        var nodeAccessClause = $"""
                n.main_node = {paramName}
                OR
                (SELECT uep.is_allow
                 FROM {uepTable} uep
                 WHERE uep.user_id IN ({userList})
                   AND uep.permission = 'Read'
                   AND n.main_node LIKE uep.node_path_prefix || '%'
                 ORDER BY LENGTH(uep.node_path_prefix) DESC,
                          CASE WHEN uep.user_id = {paramName} THEN 0 ELSE 1 END
                 LIMIT 1) = true
            """;

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

    private static bool IsJsonbField(string selector) =>
        !PropertyMap.ContainsKey(selector);

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
