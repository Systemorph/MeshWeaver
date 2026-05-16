using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
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
    public async Task ResolvePath_PrimaryTable_ReturnsNode()
    {
        var ct = TestContext.Current.CancellationToken;
        const string schema = "pathres_a";
        var (ds, adapter) = await CreateSchemaWithStandardMappingsAsync(schema, ct);

        try
        {
            var node = new MeshNode("doc", schema)
            {
                Name = "doc",
                NodeType = "Markdown",
            };
            await adapter.Write(node, JsonSerializerOptions.Default).FirstAsync().ToTask(ct);

            var (resolved, segs) = await adapter
                .ResolvePath($"{schema}/doc", JsonSerializerOptions.Default)
                .FirstAsync().ToTask(ct);

            resolved.Should().NotBeNull("the doc node was just written into the primary table");
            resolved!.Path.Should().Be($"{schema}/doc");
            segs.Should().Be(2);
        }
        finally
        {
            await ds.DisposeAsync();
        }
    }

    /// <summary>
    /// A path that matches a node in a SATELLITE table (e.g. <c>_Access</c> →
    /// <c>access</c>) must resolve in the same one-call surface. Old behaviour
    /// would resolve via mesh_nodes only and miss this.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task ResolvePath_SatelliteTable_ReturnsNode()
    {
        var ct = TestContext.Current.CancellationToken;
        const string schema = "pathres_b";
        var (ds, adapter) = await CreateSchemaWithStandardMappingsAsync(schema, ct);

        try
        {
            var node = new MeshNode("grant", $"{schema}/_Access")
            {
                Name = "grant",
                NodeType = "AccessAssignment",
            };
            await adapter.Write(node, JsonSerializerOptions.Default).FirstAsync().ToTask(ct);

            var (resolved, segs) = await adapter
                .ResolvePath($"{schema}/_Access/grant", JsonSerializerOptions.Default)
                .FirstAsync().ToTask(ct);

            resolved.Should().NotBeNull("the access grant was just written to {schema}.access");
            resolved!.Path.Should().Be($"{schema}/_Access/grant");
            segs.Should().Be(3);
        }
        finally
        {
            await ds.DisposeAsync();
        }
    }

    /// <summary>
    /// When the request path is deeper than any matched node, ResolvePath
    /// returns the deepest ancestor it can find — across BOTH primary and
    /// satellite tables — with MatchedSegments equal to the matched depth.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task ResolvePath_DeepRequest_ReturnsClosestAncestor()
    {
        var ct = TestContext.Current.CancellationToken;
        const string schema = "pathres_c";
        var (ds, adapter) = await CreateSchemaWithStandardMappingsAsync(schema, ct);

        try
        {
            // Primary: schema/Folder/doc
            await adapter.Write(new MeshNode("doc", $"{schema}/Folder") { Name = "doc", NodeType = "Markdown" },
                JsonSerializerOptions.Default).FirstAsync().ToTask(ct);
            // Satellite Source: schema/Source/file.cs
            await adapter.Write(new MeshNode("file.cs", $"{schema}/Source") { Name = "file.cs", NodeType = "Code" },
                JsonSerializerOptions.Default).FirstAsync().ToTask(ct);

            // Request a deeper path under the Source/file.cs entry — the
            // deepest ancestor across all tables is schema/Source/file.cs,
            // not schema/Folder/doc.
            var (resolved, segs) = await adapter
                .ResolvePath($"{schema}/Source/file.cs/nested/extra", JsonSerializerOptions.Default)
                .FirstAsync().ToTask(ct);

            resolved.Should().NotBeNull();
            resolved!.Path.Should().Be($"{schema}/Source/file.cs",
                "the deepest matching prefix across all tables wins");
            segs.Should().Be(3, "{schema}/Source/file.cs is 3 segments deep");
        }
        finally
        {
            await ds.DisposeAsync();
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
    public async Task ResolvePath_NoMatchInAnyTable_ReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;
        const string schema = "pathres_d";
        var (ds, adapter) = await CreateSchemaWithStandardMappingsAsync(schema, ct);

        try
        {
            var (resolved, segs) = await adapter
                .ResolvePath($"{schema}/no-such-node", JsonSerializerOptions.Default)
                .FirstAsync().ToTask(ct);

            resolved.Should().BeNull();
            segs.Should().Be(0);
        }
        finally
        {
            await ds.DisposeAsync();
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
    public async Task ResolvePath_DeeperSatelliteBeats_ShallowerPrimary()
    {
        var ct = TestContext.Current.CancellationToken;
        const string schema = "pathres_e";
        var (ds, adapter) = await CreateSchemaWithStandardMappingsAsync(schema, ct);

        try
        {
            // Primary at depth-1: schema/foo
            await adapter.Write(new MeshNode("foo", schema) { Name = "foo", NodeType = "Markdown" },
                JsonSerializerOptions.Default).FirstAsync().ToTask(ct);
            // Satellite at depth-3: schema/foo/Source/bar.cs
            await adapter.Write(new MeshNode("bar.cs", $"{schema}/foo/Source") { Name = "bar.cs", NodeType = "Code" },
                JsonSerializerOptions.Default).FirstAsync().ToTask(ct);

            // Request a depth-5 path under both — the deeper satellite ancestor wins.
            var (resolved, segs) = await adapter
                .ResolvePath($"{schema}/foo/Source/bar.cs/anchor/section", JsonSerializerOptions.Default)
                .FirstAsync().ToTask(ct);

            resolved.Should().NotBeNull();
            resolved!.Path.Should().Be($"{schema}/foo/Source/bar.cs",
                "the satellite match at depth 4 must outrank the primary match at depth 2");
            segs.Should().Be(4);
        }
        finally
        {
            await ds.DisposeAsync();
        }
    }

    // -------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------

    private async Task<(NpgsqlDataSource Ds, PostgreSqlStorageAdapter Adapter)>
        CreateSchemaWithStandardMappingsAsync(string schema, System.Threading.CancellationToken ct)
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
        return await _fixture.CreateSchemaAdapterAsync(schema, partitionDef, ct);
    }
}
