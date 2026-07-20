using System.Text.RegularExpressions;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using Xunit;

namespace MeshWeaver.Hosting.Snowflake.Test;

/// <summary>
/// Unit tests for SnowflakeSqlGenerator. No database required — these pin the emitted Snowflake
/// dialect: quoted identifiers, :name markers with bare-key parameter maps, variant content
/// accessors, ILIKE + explicit ESCAPE, MAX_BY access control (and the EXISTS fallback),
/// inline vector literals with VECTOR_COSINE_SIMILARITY, QUALIFY dedup (and the ROW_NUMBER
/// fallback), and the aclSchemas gating of the cross-schema UNION.
/// </summary>
public class SnowflakeSqlGeneratorTests
{
    [Fact]
    public void MapSelector_KnownProperties_AreQuoted()
    {
        SnowflakeSqlGenerator.MapSelector("name").Should().Be(@"n.""name""");
        SnowflakeSqlGenerator.MapSelector("nodeType").Should().Be(@"n.""node_type""");
        SnowflakeSqlGenerator.MapSelector("description").Should().Be(@"n.""description""");
        SnowflakeSqlGenerator.MapSelector("category").Should().Be(@"n.""category""");
        SnowflakeSqlGenerator.MapSelector("version").Should().Be(@"n.""version""");
        SnowflakeSqlGenerator.MapSelector("state").Should().Be(@"n.""state""");
        SnowflakeSqlGenerator.MapSelector("path").Should().Be(@"n.""path""");
    }

    [Fact]
    public void MapSelector_ContentFields_UseVariantPathAccessors()
    {
        // PG: n.content->>'status'  →  Snowflake: n."content":"status"::string
        SnowflakeSqlGenerator.MapSelector("content.status")
            .Should().Be(@"n.""content"":""status""::string");
        // Nested: n.content->'address'->>'city' → n."content":"address"."city"::string
        SnowflakeSqlGenerator.MapSelector("content.address.city")
            .Should().Be(@"n.""content"":""address"".""city""::string");
    }

    [Fact]
    public void MapSelector_Unknown_FallsBackToContentVariant()
    {
        SnowflakeSqlGenerator.MapSelector("unknownField")
            .Should().Be(@"n.""content"":""unknownField""::string");
    }

    [Fact]
    public void GenerateSelectQuery_SimpleFilter_UsesColonMarkersAndBareParameterKeys()
    {
        var gen = new SnowflakeSqlGenerator();
        var query = new ParsedQuery(
            Filter: new QueryComparison(new QueryCondition("nodeType", QueryOperator.Equal, ["Story"])),
            TextSearch: null);

        var (sql, parameters) = gen.GenerateSelectQuery(query);

        sql.Should().Contain("WHERE");
        sql.Should().Contain(@"LOWER(n.""node_type"") = :p0");
        // Snowflake.Data binds :name markers with BARE-name parameter registration —
        // no PG-style @ markers anywhere.
        sql.Should().NotContain("@");
        parameters["p0"].Should().Be("story");
    }

    [Fact]
    public void GenerateSelectQuery_SymbolicStateFilter_MapsToNumericState()
    {
        // `state:Active` must bind the MeshNodeState NUMERIC value — the state column
        // is a number; binding the raw string would fail the typed comparison.
        var gen = new SnowflakeSqlGenerator();
        var query = new ParsedQuery(
            Filter: new QueryComparison(new QueryCondition("state", QueryOperator.Equal, ["Active"])),
            TextSearch: null);

        var (sql, parameters) = gen.GenerateSelectQuery(query);

        sql.Should().Contain(@"n.""state"" = :p0");
        parameters["p0"].Should().Be((short)MeshNodeState.Active);
    }

    [Fact]
    public void GenerateSelectQuery_TextSearch_IlikeWithExplicitEscape()
    {
        // The text-search term goes through EscapeLikePattern (\ _ % escaped). Snowflake has NO
        // default LIKE escape character (PG defaults to backslash), so the escaped pattern MUST
        // declare it explicitly or `\_` matches a literal backslash instead of a literal underscore.
        var gen = new SnowflakeSqlGenerator();
        var query = new ParsedQuery(null, "my_term");

        var (sql, parameters) = gen.GenerateSelectQuery(query);

        sql.Should().Contain(@"ILIKE :p0 ESCAPE '\\'");
        sql.Should().Contain(@"COALESCE(n.""name"",'')");
        parameters["p0"].Should().Be(@"%my\_term%");
    }

    [Fact]
    public void GenerateSelectQuery_TextSearch_NoIlike_FallsBackToLowerLike()
    {
        var capabilities = SnowflakeCapabilities.AllOn with { SupportsIlike = false };
        var gen = new SnowflakeSqlGenerator(capabilities);
        var query = new ParsedQuery(null, "laptop");

        var (sql, parameters) = gen.GenerateSelectQuery(query);

        sql.Should().Contain("LIKE LOWER(:p0)");
        sql.Should().NotContain("ILIKE");
        parameters["p0"].Should().Be("%laptop%");
    }

    [Fact]
    public void GenerateSelectQuery_TextSearch_NoLikeEscape_BindsUnescapedWithoutEscapeClause()
    {
        // When the endpoint lacks LIKE … ESCAPE, an escaped pattern would mis-match (`\_` becomes a
        // literal backslash) — so the pattern is bound UNESCAPED and no ESCAPE clause is emitted
        // (degraded: LIKE metacharacters in the term act as wildcards).
        var capabilities = SnowflakeCapabilities.AllOn with { SupportsLikeEscape = false };
        var gen = new SnowflakeSqlGenerator(capabilities);
        var query = new ParsedQuery(null, "my_term");

        var (sql, parameters) = gen.GenerateSelectQuery(query);

        sql.Should().NotContain("ESCAPE");
        parameters["p0"].Should().Be("%my_term%");
    }

    [Fact]
    public void GenerateSelectQuery_LikeOperator_NoEscapeClause_BugForBugParity()
    {
        // The user's wildcard pattern (name:*test*) is bound verbatim — PG deliberately does NOT
        // escape it (the metacharacters are the point), so no ESCAPE clause is declared either.
        var gen = new SnowflakeSqlGenerator();
        var query = new ParsedQuery(
            Filter: new QueryComparison(new QueryCondition("name", QueryOperator.Like, ["*test*"])),
            TextSearch: null);

        var (sql, parameters) = gen.GenerateSelectQuery(query);

        sql.Should().Contain(@"n.""name"" ILIKE :p0");
        sql.Should().NotContain("ESCAPE");
        parameters["p0"].Should().Be("%test%");
    }

    [Fact]
    public void GenerateSelectQuery_NumericContentComparison_UsesTryCast()
    {
        // PG: CAST(content->>'count' AS numeric) — Snowflake: TRY_CAST(… AS NUMBER), a deliberate
        // difference: garbage content values null out of the comparison instead of failing the
        // whole statement.
        var gen = new SnowflakeSqlGenerator();
        var query = new ParsedQuery(
            Filter: new QueryComparison(new QueryCondition("content.count", QueryOperator.GreaterThan, ["5"])),
            TextSearch: null);

        var (sql, parameters) = gen.GenerateSelectQuery(query);

        sql.Should().Contain(@"TRY_CAST(n.""content"":""count""::string AS NUMBER) > :p0");
        sql.Should().NotContain("CAST(n.\"content\":\"count\"::string AS numeric)");
        parameters["p0"].Should().Be(5L);
    }

    [Fact]
    public void GenerateSelectQuery_InOperator_ExpandsToIndividualMarkers()
    {
        // Array binds are always expanded to IN-lists of individual :markers —
        // no `= ANY(array)` in this dialect.
        var gen = new SnowflakeSqlGenerator();
        var query = new ParsedQuery(
            Filter: new QueryComparison(new QueryCondition("nodeType", QueryOperator.In, ["Story", "Task"])),
            TextSearch: null);

        var (sql, parameters) = gen.GenerateSelectQuery(query);

        sql.Should().Contain("IN (:p0, :p1)");
        sql.Should().NotContain("ANY(");
        parameters["p0"].Should().Be("story");
        parameters["p1"].Should().Be("task");
    }

    [Fact]
    public void GenerateSelectQuery_NotEqualOnContentField_TolerantOfNull()
    {
        // Three-valued logic: `x != 'done'` is NULL (not TRUE) when x is NULL — the negated filter
        // must coalesce with IS NULL so "field absent" counts as "not equal".
        var gen = new SnowflakeSqlGenerator();
        var query = new ParsedQuery(
            Filter: new QueryComparison(new QueryCondition("content.status", QueryOperator.NotEqual, ["Done"])),
            TextSearch: null);

        var (sql, _) = gen.GenerateSelectQuery(query);

        sql.Should().Contain("IS NULL");
        sql.Should().Contain("!=");
    }

    [Fact]
    public void GenerateSelectQuery_NotInOperator_TolerantOfNull()
    {
        var gen = new SnowflakeSqlGenerator();
        var query = new ParsedQuery(
            Filter: new QueryComparison(new QueryCondition("content.status", QueryOperator.NotIn, ["Done", "Cancelled"])),
            TextSearch: null);

        var (sql, _) = gen.GenerateSelectQuery(query);

        sql.Should().Contain("NOT IN (:p0, :p1)");
        sql.Should().Contain("IS NULL");
    }

    [Fact]
    public void GenerateSelectQuery_TextSearch_RanksByHybridRelevance()
    {
        // A text query with no explicit OrderBy must ORDER BY the hybrid relevance ladder so LIMIT
        // keeps the most-relevant rows (exact-name=1000 > name-prefix=600 > id-prefix=500 > …).
        var gen = new SnowflakeSqlGenerator();
        var query = ParsedQuery.Empty with { TextSearch = "laptop" };

        var (sql, parameters) = gen.GenerateSelectQuery(query);

        sql.Should().Contain("ORDER BY (CASE");
        sql.Should().Contain(@"WHEN LOWER(COALESCE(n.""name"",'')) = LOWER(:scoreText) THEN 1000");
        sql.Should().Contain(@"END) DESC, n.""last_modified"" DESC NULLS LAST");
        parameters["scoreText"].Should().Be("laptop");
    }

    [Fact]
    public void GenerateSelectQuery_ExplicitOrderBy_SupersedesRelevance()
    {
        var gen = new SnowflakeSqlGenerator();
        var query = ParsedQuery.Empty with
        {
            TextSearch = "laptop",
            OrderBy = new OrderByClause("name")
        };

        var (sql, _) = gen.GenerateSelectQuery(query);

        sql.Should().Contain(@"ORDER BY n.""name"" ASC");
        sql.Should().NotContain("THEN 1000");
    }

    [Fact]
    public void GenerateSelectQuery_ContentProjection_VariantNullWhenExcluded()
    {
        var gen = new SnowflakeSqlGenerator();

        var (withContent, _) = gen.GenerateSelectQuery(ParsedQuery.Empty);
        withContent.Should().Contain(@"n.""content"",").And.NotContain(@"NULL::variant AS ""content""");

        var (withoutContent, _) = gen.GenerateSelectQuery(ParsedQuery.Empty, includeContent: false);
        // PG's NULL::jsonb becomes NULL::variant — same shape-preserving trick, Snowflake type.
        withoutContent.Should().Contain(@"NULL::variant AS ""content""").And.NotContain(@"n.""content"",");
    }

    [Fact]
    public void GenerateSelectQuery_AccessControl_UsesMaxByLongestPrefix()
    {
        // PG's correlated `ORDER BY LENGTH(prefix) DESC, own-row-first LIMIT 1` subquery becomes a
        // MAX_BY aggregate: score = LENGTH(prefix) * 2 + IFF(own-row, 1, 0) — longest prefix wins,
        // the caller's own row breaks same-length ties.
        var gen = new SnowflakeSqlGenerator();
        var query = new ParsedQuery(null, null);

        var (sql, parameters) = gen.GenerateSelectQuery(query, userId: "alice");

        sql.Should().Contain(@"MAX_BY(uep.""is_allow"", LENGTH(uep.""node_path_prefix"") * 2 + IFF(uep.""user_id"" = :acUser0, 1, 0))");
        sql.Should().Contain("user_effective_permissions");
        sql.Should().Contain("'Read'");
        sql.Should().Contain("= true");
        parameters["acUser0"].Should().Be("alice");
    }

    [Fact]
    public void GenerateSelectQuery_AccessControl_NoMaxBy_EmitsExistsAllowDenyPair()
    {
        // Fallback shape for endpoints without MAX_BY: an allow row with no deny row that is
        // longer — or same-length but caller-owned while the allow row is not.
        var capabilities = SnowflakeCapabilities.AllOn with { SupportsMaxBy = false };
        var gen = new SnowflakeSqlGenerator(capabilities);
        var query = new ParsedQuery(null, null);

        var (sql, parameters) = gen.GenerateSelectQuery(query, userId: "alice");

        sql.Should().NotContain("MAX_BY");
        sql.Should().Contain("allow_p");
        sql.Should().Contain("deny_p");
        sql.Should().Contain("NOT EXISTS");
        sql.Should().Contain(@"LENGTH(deny_p.""node_path_prefix"") > LENGTH(allow_p.""node_path_prefix"")");
        parameters["acUser0"].Should().Be("alice");
    }

    [Fact]
    public void GenerateSelectQuery_AccessControl_Anonymous_OmitsPublicFloorAndPublicRead()
    {
        // Anonymous users only get their own permissions: no 'Public' inheritance and no
        // public-read node-type bypass.
        var gen = new SnowflakeSqlGenerator();
        var query = new ParsedQuery(null, null);

        var (sql, _) = gen.GenerateSelectQuery(query, userId: WellKnownUsers.Anonymous);

        sql.Should().Contain("user_effective_permissions");
        sql.Should().NotContain("'Public'");
        sql.Should().NotContain("node_type_permissions");
    }

    [Fact]
    public void GenerateScopeClause_BasicScopes_QuotedColumnsAndBareKeys()
    {
        var gen = new SnowflakeSqlGenerator();

        var (exact, exactParams) = gen.GenerateScopeClause("ACME/Project", QueryScope.Exact);
        exact.Should().Be(@"n.""path"" = :scopePath");
        exactParams["scopePath"].Should().Be("ACME/Project");

        var (children, childrenParams) = gen.GenerateScopeClause("ACME/Project", QueryScope.Children);
        children.Should().Be(@"n.""namespace"" = :scopeNs");
        childrenParams["scopeNs"].Should().Be("ACME/Project");

        var (descendants, descendantsParams) = gen.GenerateScopeClause("ACME", QueryScope.Descendants);
        descendants.Should().Be(@"n.""path"" LIKE :scopePrefix || '%'");
        descendantsParams["scopePrefix"].Should().Be("ACME/");

        var (subtree, _) = gen.GenerateScopeClause("ACME", QueryScope.Subtree);
        subtree.Should().Contain(@"n.""path"" = :scopePath");
        subtree.Should().Contain(@"n.""path"" LIKE :scopePrefix || '%'");
        subtree.Should().Contain("OR");

        // Empty base path = whole schema — no path filter at all.
        var (emptyClause, emptyParams) = gen.GenerateScopeClause("", QueryScope.Descendants);
        emptyClause.Should().BeEmpty("descendants of root should return all nodes with no path constraint");
        emptyParams.Should().BeEmpty();
    }

    [Fact]
    public void GenerateScopeClause_NextLevel_WithTable_EmitsFrontierAntiJoin()
    {
        var gen = new SnowflakeSqlGenerator();

        var (clause, parameters) = gen.GenerateScopeClause(
            "ACME", QueryScope.NextLevel, qualifiedTable: "\"acme\".\"mesh_nodes\"");

        clause.Should().Contain(@"n.""path"" LIKE :scopePrefix || '%'");
        clause.Should().Contain("NOT EXISTS");
        clause.Should().Contain("\"acme\".\"mesh_nodes\" anc");
        clause.Should().Contain(@"anc.""state"" = 2");
        clause.Should().Contain(@"n.""path"" LIKE anc.""path"" || '/%'");
        parameters["scopePrefix"].Should().Be("ACME/");
    }

    [Fact]
    public void GenerateScopeClause_NextLevel_NullTable_DegradesToDescendants()
    {
        var gen = new SnowflakeSqlGenerator();

        var (clause, parameters) = gen.GenerateScopeClause("ACME", QueryScope.NextLevel);

        clause.Should().Be(@"n.""path"" LIKE :scopePrefix || '%'");
        clause.Should().NotContain("NOT EXISTS");
        parameters["scopePrefix"].Should().Be("ACME/");
    }

    [Fact]
    public void GenerateScopeClause_MultiPath_ExpandsInList()
    {
        var gen = new SnowflakeSqlGenerator();

        var (clause, parameters) = gen.GenerateScopeClause(
            new[] { "ACME", "ACME/Project" }, QueryScope.Exact);

        clause.Should().Be(@"n.""path"" IN (:scopePath0, :scopePath1)");
        parameters["scopePath0"].Should().Be("ACME");
        parameters["scopePath1"].Should().Be("ACME/Project");
    }

    [Fact]
    public void GenerateCrossSchemaSelectQuery_AclSchemas_GateAccessClausePerBranch()
    {
        // Access-control SQL is emitted ONLY for schemas in aclSchemas — public content schemas
        // (e.g. the mirrored documentation) ship mesh_nodes WITHOUT the permission tables, and
        // referencing those missing relations would fail the whole UNION. This replaces the PG
        // stored proc's to_regclass existence guard.
        var parser = new QueryParser();
        var parsed = parser.Parse("nodeType:Story");
        var gen = new SnowflakeSqlGenerator();

        var (sql, parameters) = gen.GenerateCrossSchemaSelectQuery(
            parsed, new[] { "acme", "doc" }, aclSchemas: new[] { "acme" }, userId: "alice");

        // Both branches select — only the ACL-provisioned one carries the permission predicates.
        sql.Should().Contain("\"acme\".\"mesh_nodes\"");
        sql.Should().Contain("\"doc\".\"mesh_nodes\"");
        sql.Should().Contain("UNION ALL");
        sql.Should().Contain("\"acme\".\"user_effective_permissions\"");
        sql.Should().NotContain("\"doc\".\"user_effective_permissions\"");
        sql.Should().NotContain("\"doc\".\"node_type_permissions\"");
        sql.Should().Contain(@"pa.""partition"" = 'acme'");
        sql.Should().NotContain(@"pa.""partition"" = 'doc'");
        parameters["acUser_cross"].Should().Be("alice");
    }

    [Fact]
    public void GenerateCrossSchemaSelectQuery_FreeTextWithContentSchemas_QualifyDedupedContentBranch()
    {
        // The cross-partition lexical omnibox folds indexed content into the SAME UNION. PG deduped
        // best-chunk-per-file with SELECT DISTINCT ON; Snowflake uses QUALIFY ROW_NUMBER() = 1.
        var parser = new QueryParser();
        var parsed = parser.Parse("pension scope:descendants");
        var gen = new SnowflakeSqlGenerator();

        var (sql, _) = gen.GenerateCrossSchemaSelectQuery(
            parsed, new[] { "rbuergi", "acme" }, aclSchemas: [], userId: null,
            tableName: "mesh_nodes", contentSchemas: new[] { "rbuergi" });

        sql.Should().Contain("\"rbuergi\".\"content_chunks\"");
        sql.Should().Contain("'Document'");
        sql.Should().Contain("_Documents");
        sql.Should().Contain(@"cc.""chunk_text"" ILIKE '%pension%'");
        sql.Should().Contain(@"QUALIFY ROW_NUMBER() OVER (PARTITION BY cc.""collection_path"", cc.""file_path""");
        // Content arm stays parenthesized — a self-contained UNION branch.
        sql.Should().Contain("UNION ALL (SELECT");
        // Relevance wrapper ranks the merged rows; the ladder + recency tiebreak survive the UNION.
        sql.Should().Contain("ORDER BY (CASE");
        sql.Should().Contain(@"""last_modified"" DESC NULLS LAST");
    }

    [Fact]
    public void GenerateCrossSchemaSelectQuery_StructuredOnly_NoContentBranch()
    {
        var parser = new QueryParser();
        var parsed = parser.Parse("nodeType:Story namespace:rbuergi");
        var gen = new SnowflakeSqlGenerator();

        var (sql, _) = gen.GenerateCrossSchemaSelectQuery(
            parsed, new[] { "rbuergi" }, aclSchemas: [], userId: null,
            tableName: "mesh_nodes", contentSchemas: new[] { "rbuergi" });

        sql.Should().NotContain("content_chunks");
    }

    [Fact]
    public void GenerateCrossSchemaSelectQuery_NamespaceSubtree_PushesDownPathFilter()
    {
        // Same push-down guarantee as the PG generator: a satellite-table cross-schema UNION must
        // carry the scope filter through, or it returns every row in the partition.
        var parser = new QueryParser();
        var parsed = parser.Parse(
            "namespace:Systemorph/EventCalendar/Source scope:subtree nodeType:Code");
        var gen = new SnowflakeSqlGenerator();

        var (sql, parameters) = gen.GenerateCrossSchemaSelectQuery(
            parsed, new[] { "systemorph" }, aclSchemas: [], userId: null, tableName: "code");

        // `namespace:X scope:subtree` degrades to DESCENDANTS at parse time (namespace:X can
        // never match the node at path X itself) — the pushed-down clause is the prefix LIKE.
        sql.Should().Contain(@"n.""path"" LIKE :scopePrefix");
        sql.Should().NotContain(@"n.""path"" = :scopePath");
        // Satellite selects project the Include default so the UNION shape stays uniform.
        sql.Should().Contain(@"0::smallint AS ""sync_behavior""");
        parameters["scopePrefix"].Should().Be("Systemorph/EventCalendar/Source/");
    }

    [Fact]
    public void GenerateCrossSchemaSelectQuery_Accessed_JoinsCallersUserActivitiesInEveryBranch()
    {
        // Mirrors the PG generator: the caller's access log lives in the CALLER's partition
        // schema, so every branch joins that ONE user_activities table (cross-partition
        // "last accessed" would otherwise always be empty).
        var parser = new QueryParser();
        var parsed = parser.Parse("source:accessed is:main sort:LastModified-desc");
        var gen = new SnowflakeSqlGenerator();

        var (sql, parameters) = gen.GenerateCrossSchemaSelectQuery(
            parsed, new[] { "acme", "northwind" }, aclSchemas: [], userId: null,
            tableName: "mesh_nodes", activityUserId: "rbuergi", contentSchemas: null,
            activityUserSchema: "rbuergi");

        sql.Should().Contain(@"""acme"".""mesh_nodes""");
        sql.Should().Contain(@"""northwind"".""mesh_nodes""");
        sql.Should().Contain(@"""rbuergi"".""user_activities""",
            "every branch joins the caller's access log");
        sql.Should().NotContain(@"""acme"".""user_activities""");
        sql.Should().NotContain(@"""northwind"".""user_activities""");
        parameters["actUserNs"].Should().Be("rbuergi/_UserActivity");
    }

    [Fact]
    public void GenerateVectorSearchQuery_InlinesInvariantVectorLiteral()
    {
        // pgvector's `embedding <=> @queryVector` becomes 1 - VECTOR_COSINE_SIMILARITY with the
        // vector INLINED as an invariant-culture literal (driver-side vector binding unverified) —
        // numerically identical to <=>, ordering unchanged.
        var gen = new SnowflakeSqlGenerator();

        var (sql, parameters) = gen.GenerateVectorSearchQuery(
            filterQuery: null, queryVector: new[] { 0.1f, 0.2f, 0.3f }, userId: null, topK: 5);

        sql.Should().Contain(@"(1 - VECTOR_COSINE_SIMILARITY(n.""embedding"", [0.1,0.2,0.3]::VECTOR(FLOAT, 3)))");
        sql.Should().Contain(@"AS ""_distance""");
        sql.Should().Contain("LIMIT 5");
        parameters.Should().NotContainKey("queryVector");
        sql.Should().NotContain("<=>");
    }

    [Fact]
    public void GenerateVectorSearchQuery_WithLexicalTerm_LexicalTierThenCosine()
    {
        // Hybrid: an exact/prefix lexical match must outrank a closer semantic neighbour, with
        // cosine distance breaking ties WITHIN a tier.
        var gen = new SnowflakeSqlGenerator();

        var (sql, parameters) = gen.GenerateVectorSearchQuery(
            filterQuery: null, queryVector: new[] { 0.1f, 0.2f, 0.3f }, userId: null, topK: 5,
            lexicalTerm: "laptop");

        sql.Should().Contain("ORDER BY (CASE");
        sql.Should().Contain(@"WHEN LOWER(COALESCE(n.""name"",'')) = LOWER(:lexTerm) THEN 0");
        // Lexical tier first, cosine as the in-tier tiebreaker.
        sql.Should().Contain("END), (1 - VECTOR_COSINE_SIMILARITY");
        parameters["lexTerm"].Should().Be("laptop");
    }

    [Fact]
    public void GenerateVectorSearchQuery_ContentChunks_QualifyDedupAndOuterDistanceOrder()
    {
        // PG's `SELECT DISTINCT ON (collection_path, file_path) … ORDER BY keys, dist` becomes
        // QUALIFY ROW_NUMBER() OVER (PARTITION BY keys ORDER BY "_distance") = 1; the outer
        // wrapper ranks BOTH branches by the projected distance and keeps the closest.
        var gen = new SnowflakeSqlGenerator { SchemaName = "rbuergi" };

        var (sql, _) = gen.GenerateVectorSearchQuery(
            filterQuery: null, queryVector: new[] { 0.1f, 0.2f, 0.3f }, userId: null, topK: 7,
            includeContentChunks: true);

        sql.Should().Contain("UNION ALL");
        sql.Should().Contain("\"rbuergi\".\"content_chunks\"");
        sql.Should().Contain("_Documents");
        sql.Should().Contain("'Document'");
        sql.Should().Contain(
            @"QUALIFY ROW_NUMBER() OVER (PARTITION BY cc.""collection_path"", cc.""file_path"" ORDER BY ""_distance"") = 1");
        sql.Should().Contain(@") u ORDER BY u.""_distance"" ASC LIMIT 7");
        // The content arm stays parenthesized — a self-contained UNION branch.
        sql.Should().Contain("UNION ALL (SELECT");
        sql.Should().NotContain("DISTINCT ON");
    }

    [Fact]
    public void GenerateVectorSearchQuery_ContentChunks_NoQualify_RowNumberDerivedTableFallback()
    {
        // Endpoints without QUALIFY get the derived-table shape: inner ROW_NUMBER as "_rn",
        // outer WHERE "_rn" = 1 with the columns re-listed so the UNION arm shape stays aligned.
        var capabilities = SnowflakeCapabilities.AllOn with { SupportsQualify = false };
        var gen = new SnowflakeSqlGenerator(capabilities) { SchemaName = "rbuergi" };

        var (sql, _) = gen.GenerateVectorSearchQuery(
            filterQuery: null, queryVector: new[] { 0.1f, 0.2f, 0.3f }, userId: null, topK: 7,
            includeContentChunks: true);

        sql.Should().NotContain("QUALIFY");
        sql.Should().Contain(@"AS ""_rn""");
        sql.Should().Contain(@"WHERE d.""_rn"" = 1");
        sql.Should().Contain(@"ROW_NUMBER() OVER (PARTITION BY cc.""collection_path"", cc.""file_path""");
        sql.Should().Contain(@"d.""_distance""");
    }

    [Fact]
    public void GenerateVectorSearchQuery_WithNamespace_PredicateInGeneratedSql()
    {
        // The namespace-prefix predicate is emitted INSIDE the generator (per branch), bound to
        // :nsPrefix — never post-injected by a string Replace("WHERE", …).
        var gen = new SnowflakeSqlGenerator { SchemaName = "rbuergi" };

        var (sql, parameters) = gen.GenerateVectorSearchQuery(
            filterQuery: null, queryVector: new[] { 0.1f, 0.2f, 0.3f }, userId: null, topK: 5,
            namespacePath: "rbuergi/Space", includeContentChunks: true);

        sql.Should().Contain(@"n.""path"" LIKE :nsPrefix || '%'");
        sql.Should().Contain("'/_Documents/' ||");
        parameters["nsPrefix"].Should().Be("rbuergi/Space/");
    }

    [Fact]
    public void GenerateVectorSearchQuery_NoVectorSupport_Throws()
    {
        // Endpoints without the VECTOR type cannot serve vector search at all — callers must stay
        // on the lexical path; a silent lexical downgrade here would mask a misconfiguration.
        var capabilities = SnowflakeCapabilities.AllOn with { SupportsVector = false };
        var gen = new SnowflakeSqlGenerator(capabilities);

        Action act = () => gen.GenerateVectorSearchQuery(
            filterQuery: null, queryVector: new[] { 0.1f, 0.2f }, userId: null, topK: 5);

        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void GeneratedSql_AllTableReferencesQuoted()
    {
        // Snowflake uppercases unquoted identifiers while the mesh's tables are created with
        // quoted lowercase names — ONE unquoted table ref resolves to a different (missing)
        // relation. Scan every FROM/JOIN target across the main statement shapes.
        var parser = new QueryParser();

        var single = new SnowflakeSqlGenerator { SchemaName = "rbuergi" };
        var (singleSql, _) = single.GenerateSelectQuery(
            ParsedQuery.Empty with { TextSearch = "laptop" }, userId: "alice");
        AssertAllTableReferencesQuoted(singleSql);

        var accessed = new SnowflakeSqlGenerator { SchemaName = "rbuergi" };
        var (accessedSql, _) = accessed.GenerateSelectQuery(
            parser.Parse("nodeType:Story source:accessed"), userId: "alice", activityUserId: "alice");
        AssertAllTableReferencesQuoted(accessedSql);

        var cross = new SnowflakeSqlGenerator();
        var (crossSql, _) = cross.GenerateCrossSchemaSelectQuery(
            parser.Parse("pension"), new[] { "acme", "doc" }, aclSchemas: new[] { "acme" },
            userId: "alice", contentSchemas: new[] { "doc" });
        AssertAllTableReferencesQuoted(crossSql);

        var vector = new SnowflakeSqlGenerator { SchemaName = "rbuergi" };
        var (vectorSql, _) = vector.GenerateVectorSearchQuery(
            filterQuery: null, queryVector: new[] { 0.1f, 0.2f, 0.3f }, userId: "alice", topK: 5,
            lexicalTerm: "laptop", includeContentChunks: true);
        AssertAllTableReferencesQuoted(vectorSql);
    }

    /// <summary>
    /// Scans <paramref name="sql"/> for FROM/JOIN targets and fails on any that does not start
    /// with a quoted identifier (subquery parens are skipped by the pattern).
    /// </summary>
    private static void AssertAllTableReferencesQuoted(string sql)
    {
        var offenders = Regex.Matches(sql, @"\b(?:FROM|JOIN)\s+([^\s(]+)")
            .Select(m => m.Groups[1].Value)
            .Where(target => !target.StartsWith('"'))
            .ToList();

        offenders.Should().BeEmpty(
            $"every FROM/JOIN target must be a quoted identifier (Snowflake uppercases unquoted names) but found: {string.Join(", ", offenders)} in: {sql}");
    }

    [Fact]
    public void GetAncestorPaths_ReturnsCorrectPaths()
    {
        SnowflakeSqlGenerator.GetAncestorPaths("ACME/Project/Story1")
            .Should().BeEquivalentTo(new[] { "ACME", "ACME/Project" }, System.Text.Json.JsonSerializerOptions.Default);

        SnowflakeSqlGenerator.GetAncestorPaths("ACME")
            .Should().BeEmpty();

        SnowflakeSqlGenerator.GetAncestorPaths("")
            .Should().BeEmpty();
    }
}
