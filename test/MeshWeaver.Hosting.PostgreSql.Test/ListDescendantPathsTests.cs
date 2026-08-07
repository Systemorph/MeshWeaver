using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Mesh;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Tests for <see cref="PostgreSqlStorageAdapter.ListDescendantPaths"/> — the native
/// authoritative subtree enumeration the recursive-delete planner and its post-delete
/// verification run on (issue #839). One UNION round-trip must return every strict
/// descendant across the primary <c>mesh_nodes</c> table AND every satellite table,
/// including rows behind node-less intermediate segments, with LIKE wildcards in the
/// root escaped so <c>_</c>-bearing roots never leak sibling subtrees into a deletion plan.
/// </summary>
[Collection("PostgreSql")]
public class ListDescendantPathsTests : IAsyncLifetime
{
    private readonly PostgreSqlFixture _fixture;
    private readonly JsonSerializerOptions _options = new();
    private Npgsql.NpgsqlDataSource _schemaDs = null!;
    private PostgreSqlStorageAdapter _adapter = null!;

    private static readonly PartitionDefinition Partition = new()
    {
        Namespace = "DescOrg",
        DataSource = "default",
        Schema = "descendant_paths_test",
        Versioned = true,
        TableMappings = PartitionDefinition.DefaultSegmentTableMappings(),
        NodeTypeTableMappings = PartitionDefinition.DefaultNodeTypeTableMappings(),
    };

    public ListDescendantPathsTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    public async ValueTask InitializeAsync()
    {
        var (ds, adapter) = await _fixture.CreateSchemaAdapterAsync(
            "descendant_paths_test", Partition, TestContext.Current.CancellationToken);
        _schemaDs = ds;
        _adapter = adapter;
    }

    public ValueTask DisposeAsync()
    {
        _schemaDs?.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact(Timeout = 30000)]
    public async Task Enumerates_Satellites_And_NodelessGaps_InOneCall()
    {
        await _adapter.Write(new MeshNode("a", "DescOrg/Space")
        { Name = "A", NodeType = "Markdown" }, _options).Should().Within(30.Seconds()).Emit();
        // Gap descendant: no node at DescOrg/Space/a/b.
        await _adapter.Write(new MeshNode("c", "DescOrg/Space/a/b")
        { Name = "C", NodeType = "Markdown" }, _options).Should().Within(30.Seconds()).Emit();
        // Satellite descendant: routed to the `threads` table by the _Thread segment.
        await _adapter.Write(new MeshNode("t1", "DescOrg/Space/a/_Thread")
        { Name = "T1", NodeType = "Thread" }, _options).Should().Within(30.Seconds()).Emit();
        // Outside the subtree — must not appear.
        await _adapter.Write(new MeshNode("other", "DescOrg/Space")
        { Name = "Other", NodeType = "Markdown" }, _options).Should().Within(30.Seconds()).Emit();

        var descendants = await _adapter.ListDescendantPaths("DescOrg/Space/a")
            .Should().Within(30.Seconds()).Emit();

        descendants.Should().BeEquivalentTo(new[]
        {
            "DescOrg/Space/a/b/c",
            "DescOrg/Space/a/_Thread/t1"
        }, JsonSerializerOptions.Default);
    }

    [Fact(Timeout = 30000)]
    public async Task Escapes_LikeWildcards_In_Root_So_Sibling_Subtrees_Never_Leak()
    {
        // `_` is a single-char LIKE wildcard: an unescaped prefix pattern for the root
        // 'DescOrg/W_X' would also match 'DescOrg/WaX/…' — pulling a SIBLING subtree
        // into a deletion plan.
        await _adapter.Write(new MeshNode("n1", "DescOrg/W_X")
        { Name = "N1", NodeType = "Markdown" }, _options).Should().Within(30.Seconds()).Emit();
        await _adapter.Write(new MeshNode("n2", "DescOrg/WaX")
        { Name = "N2", NodeType = "Markdown" }, _options).Should().Within(30.Seconds()).Emit();

        var descendants = await _adapter.ListDescendantPaths("DescOrg/W_X")
            .Should().Within(30.Seconds()).Emit();

        descendants.Should().BeEquivalentTo(new[] { "DescOrg/W_X/n1" }, JsonSerializerOptions.Default);
    }

    [Fact(Timeout = 30000)]
    public async Task Root_Itself_Is_Excluded()
    {
        await _adapter.Write(new MeshNode("root", "DescOrg/Solo")
        { Name = "Root", NodeType = "Markdown" }, _options).Should().Within(30.Seconds()).Emit();

        var descendants = await _adapter.ListDescendantPaths("DescOrg/Solo/root")
            .Should().Within(30.Seconds()).Emit();

        descendants.Should().BeEmpty();
    }
}
