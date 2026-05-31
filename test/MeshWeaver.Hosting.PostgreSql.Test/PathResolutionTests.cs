using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Mesh;
using Npgsql;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Pins the path-resolution contract on the Postgres storage adapter:
/// resolving a path to its closest matching MeshNode must take EXACTLY ONE
/// SQL round-trip and must consider every table in the partition's schema
/// (primary <c>mesh_nodes</c> + each satellite table named in
/// <see cref="PartitionDefinition.TableMappings"/>). The old multi-step walk
/// (catalog STEP1..STEP4) issued up to 1+N+N queries per resolve; that is
/// the layering bug this contract prevents.
/// </summary>
[Collection("PostgreSql")]
public class PathResolutionTests
{
    private readonly PostgreSqlFixture _fixture;

    public PathResolutionTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// A path that matches a node in the partition's PRIMARY table resolves
    /// to that node in one query.
    /// </summary>
    [Fact(Timeout = 60000)]
    public void ResolvePath_PrimaryTable_ReturnsNode()
    {
        var ct = TestContext.Current.CancellationToken;
        const string schema = "pathres_a";
        var (ds, adapter) = CreateSchemaWithStandardMappings(schema, ct);

        try
        {
            var node = new MeshNode("doc", schema)
            {
                Name = "doc",
                NodeType = "Markdown",
            };
            adapter.Write(node, JsonSerializerOptions.Default).Should().Within(30.Seconds()).Emit();

            var (resolved, segs) = adapter
                .ResolvePath($"{schema}/doc", JsonSerializerOptions.Default)
                .Should().Within(30.Seconds()).Emit();

            resolved.Should().NotBeNull("the doc node was just written into the primary table");
            resolved!.Path.Should().Be($"{schema}/doc");
            segs.Should().Be(2);
        }
        finally
        {
            ds.DisposeReactive().Should().Within(30.Seconds()).Emit();
        }
    }

    /// <summary>
    /// A path that matches a node in a SATELLITE table (e.g. <c>_Access</c> →
    /// <c>access</c>) must resolve in the same one-call surface. Old behaviour
    /// would resolve via mesh_nodes only and miss this.
    /// </summary>
    [Fact(Timeout = 60000)]
    public void ResolvePath_SatelliteTable_ReturnsNode()
    {
        var ct = TestContext.Current.CancellationToken;
        const string schema = "pathres_b";
        var (ds, adapter) = CreateSchemaWithStandardMappings(schema, ct);

        try
        {
            var node = new MeshNode("grant", $"{schema}/_Access")
            {
                Name = "grant",
                NodeType = "AccessAssignment",
            };
            adapter.Write(node, JsonSerializerOptions.Default).Should().Within(30.Seconds()).Emit();

            var (resolved, segs) = adapter
                .ResolvePath($"{schema}/_Access/grant", JsonSerializerOptions.Default)
                .Should().Within(30.Seconds()).Emit();

            resolved.Should().NotBeNull("the access grant was just written to {schema}.access");
            resolved!.Path.Should().Be($"{schema}/_Access/grant");
            segs.Should().Be(3);
        }
        finally
        {
            ds.DisposeReactive().Should().Within(30.Seconds()).Emit();
        }
    }

    /// <summary>
    /// When the request path is deeper than any matched node, ResolvePath
    /// returns the deepest ancestor it can find — across BOTH primary and
    /// satellite tables — with MatchedSegments equal to the matched depth.
    /// </summary>
    [Fact(Timeout = 60000)]
    public void ResolvePath_DeepRequest_ReturnsClosestAncestor()
    {
        var ct = TestContext.Current.CancellationToken;
        const string schema = "pathres_c";
        var (ds, adapter) = CreateSchemaWithStandardMappings(schema, ct);

        try
        {
            // Primary: schema/Folder/doc
            adapter.Write(new MeshNode("doc", $"{schema}/Folder") { Name = "doc", NodeType = "Markdown" },
                JsonSerializerOptions.Default).Should().Within(30.Seconds()).Emit();
            // Satellite Source: schema/Source/file.cs
            adapter.Write(new MeshNode("file.cs", $"{schema}/Source") { Name = "file.cs", NodeType = "Code" },
                JsonSerializerOptions.Default).Should().Within(30.Seconds()).Emit();

            // Request a deeper path under the Source/file.cs entry — the
            // deepest ancestor across all tables is schema/Source/file.cs,
            // not schema/Folder/doc.
            var (resolved, segs) = adapter
                .ResolvePath($"{schema}/Source/file.cs/nested/extra", JsonSerializerOptions.Default)
                .Should().Within(30.Seconds()).Emit();

            resolved.Should().NotBeNull();
            resolved!.Path.Should().Be($"{schema}/Source/file.cs",
                "the deepest matching prefix across all tables wins");
            segs.Should().Be(3, "{schema}/Source/file.cs is 3 segments deep");
        }
        finally
        {
            ds.DisposeReactive().Should().Within(30.Seconds()).Emit();
        }
    }

    /// <summary>
    /// A partition whose primary AND satellite tables are empty must still
    /// be a valid "the partition exists" answer — ResolvePath returns a
    /// null node with MatchedSegments=0, and the caller (PathResolutionService)
    /// is responsible for the partition-root virtual fallback. This test
    /// pins the contract that ResolvePath does NOT silently fabricate a
    /// virtual root — that policy lives one layer up.
    /// </summary>
    [Fact(Timeout = 60000)]
    public void ResolvePath_NoMatchInAnyTable_ReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;
        const string schema = "pathres_d";
        var (ds, adapter) = CreateSchemaWithStandardMappings(schema, ct);

        try
        {
            var (resolved, segs) = adapter
                .ResolvePath($"{schema}/no-such-node", JsonSerializerOptions.Default)
                .Should().Within(30.Seconds()).Emit();

            resolved.Should().BeNull();
            segs.Should().Be(0);
        }
        finally
        {
            ds.DisposeReactive().Should().Within(30.Seconds()).Emit();
        }
    }

    /// <summary>
    /// Cross-table deepest-match contract: when the request path matches
    /// nodes in BOTH the primary table and a satellite table at different
    /// depths, the deeper one wins regardless of which table it lives in.
    /// This pins the UNION semantic — a single query consults every table
    /// in the schema rather than checking mesh_nodes first and falling
    /// back to satellites.
    /// </summary>
    [Fact(Timeout = 60000)]
    public void ResolvePath_DeeperSatelliteBeats_ShallowerPrimary()
    {
        var ct = TestContext.Current.CancellationToken;
        const string schema = "pathres_e";
        var (ds, adapter) = CreateSchemaWithStandardMappings(schema, ct);

        try
        {
            // Primary at depth-1: schema/foo
            adapter.Write(new MeshNode("foo", schema) { Name = "foo", NodeType = "Markdown" },
                JsonSerializerOptions.Default).Should().Within(30.Seconds()).Emit();
            // Satellite at depth-3: schema/foo/Source/bar.cs
            adapter.Write(new MeshNode("bar.cs", $"{schema}/foo/Source") { Name = "bar.cs", NodeType = "Code" },
                JsonSerializerOptions.Default).Should().Within(30.Seconds()).Emit();

            // Request a depth-5 path under both — the deeper satellite ancestor wins.
            var (resolved, segs) = adapter
                .ResolvePath($"{schema}/foo/Source/bar.cs/anchor/section", JsonSerializerOptions.Default)
                .Should().Within(30.Seconds()).Emit();

            resolved.Should().NotBeNull();
            resolved!.Path.Should().Be($"{schema}/foo/Source/bar.cs",
                "the satellite match at depth 4 must outrank the primary match at depth 2");
            segs.Should().Be(4);
        }
        finally
        {
            ds.DisposeReactive().Should().Within(30.Seconds()).Emit();
        }
    }

    /// <summary>
    /// Regression for the prod "/rbuergi → No node found" outage even after
    /// the partition-root MeshNode was correctly written to
    /// <c>{username}.mesh_nodes</c> at <c>(namespace='', id={username})</c>.
    ///
    /// <para>Root cause: <see cref="PostgreSqlPathRoutingAdapter"/> (the
    /// <see cref="IStorageAdapter"/> singleton in DI) didn't override
    /// <see cref="IStorageAdapter.ResolvePath"/> — only
    /// <c>FindBestPrefixMatch</c>. <see cref="PathResolutionService"/>
    /// calls <c>_storageAdapter.ResolvePath(path)</c>; the default impl
    /// delegates to <c>FindBestPrefixMatch</c>, which only probes
    /// <c>mesh_nodes</c>. The per-schema <c>PostgreSqlStorageAdapter</c>'s
    /// UNION-across-satellites override was never reached, so every bare
    /// partition lookup whose User content sat in <c>mesh_nodes</c> still
    /// worked but lookups that depended on the satellite UNION (e.g.
    /// future partition-root content that lives in <c>code</c> /
    /// <c>access</c> / etc.) silently missed.</para>
    ///
    /// <para>This test goes through the partition provider's routing
    /// surface — the same one prod code resolves via DI — instead of
    /// poking the per-schema adapter directly like the other tests in this
    /// file do.</para>
    /// </summary>
    [Fact(Timeout = 60000)]
    public void RoutingAdapter_ForwardsResolvePath_ToPerSchemaAdapter()
    {
        var ct = TestContext.Current.CancellationToken;
        const string schema = "pathres_routingfwd";
        var (ds, _) = CreateSchemaWithStandardMappings(schema, ct);

        try
        {
            using var provider = new PostgreSqlPartitionStorageProvider(
                _fixture.DataSource,
                _fixture.ConnectionString,
                new PostgreSqlStorageOptions { ConnectionString = _fixture.ConnectionString },
                partitions: null);
            provider.RegisterPartition(new PartitionDefinition
            {
                Namespace = schema,
                DataSource = "default",
                Schema = schema,
                Table = "mesh_nodes",
                TableMappings = PartitionDefinition.StandardTableMappings,
                Versioned = true,
            });

            // Write a User node directly into the schema at the bare partition
            // root — the post-v10 onboarding shape (path = "{schema}",
            // namespace='', id=schema, nodeType=User).
            var schemaAdapter = new PostgreSqlStorageAdapter(ds, partitionDefinition: new PartitionDefinition
            {
                Namespace = schema,
                Schema = schema,
                Table = "mesh_nodes",
                TableMappings = PartitionDefinition.StandardTableMappings,
            });
            var userNode = new MeshNode(schema)
            {
                Name = schema,
                NodeType = "User",
                State = MeshNodeState.Active,
            };
            schemaAdapter.Write(userNode, JsonSerializerOptions.Default).Should().Within(30.Seconds()).Emit();

            // Act: hit the ROUTING adapter (same surface PathResolutionService
            // sees as IStorageAdapter in DI), not the per-schema one.
            var routingAdapter = provider.Adapter;
            var (resolved, segs) = routingAdapter
                .ResolvePath(schema, JsonSerializerOptions.Default)
                .Should().Within(30.Seconds()).Emit();

            // Assert: the routing layer must surface the User node. Pre-fix,
            // the default IStorageAdapter.ResolvePath impl delegated to
            // FindBestPrefixMatch which works for mesh_nodes-only matches —
            // this test ALSO exercises the contract that the routing adapter
            // forwards to the per-schema adapter's ResolvePath override, not
            // to its FindBestPrefixMatch.
            resolved.Should().NotBeNull(
                "PostgreSqlPathRoutingAdapter.ResolvePath must forward to the per-schema adapter's " +
                "ResolvePath override — the default IStorageAdapter impl probes mesh_nodes only " +
                "and misses content in satellite tables (prod symptom: /rbuergi → 'No node found' " +
                "even after V20 placed the User row at ns='' id=rbuergi).");
            resolved!.Path.Should().Be(schema);
            resolved.NodeType.Should().Be("User");
            segs.Should().Be(1);
        }
        finally
        {
            ds.DisposeReactive().Should().Within(30.Seconds()).Emit();
        }
    }

    // -------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------

    private (NpgsqlDataSource Ds, PostgreSqlStorageAdapter Adapter)
        CreateSchemaWithStandardMappings(string schema, System.Threading.CancellationToken ct)
    {
        var partitionDef = new PartitionDefinition
        {
            Namespace = schema,
            DataSource = "default",
            Schema = schema,
            Table = "mesh_nodes",
            TableMappings = PartitionDefinition.StandardTableMappings,
            Versioned = true,
        };
        // The fixture's IObservable form keeps the low-level schema DDL async
        // inside; the test body asserts reactively (§2a).
        return _fixture.CreateSchemaAdapter(schema, partitionDef, ct)
            .Should().Within(60.Seconds()).Emit();
    }
}
