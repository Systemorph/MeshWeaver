using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Hosting.Cosmos.Test;

/// <summary>
/// One fact per storage primitive <see cref="CosmosStorageAdapter"/> relies on, asserting
/// round-trip CORRECTNESS (not just no-throw) against the connected endpoint — the vnext
/// emulator by default, or a real account via <c>COSMOS_CONNECTION</c>.
///
/// <para>
/// These pin the emulator's capability baseline. vnext is a re-implementation (it runs a
/// <c>pgcosmos</c> Postgres extension behind the Cosmos wire protocol, per its own startup log),
/// so it is NOT guaranteed to behave identically to the service on every construct. When it
/// regresses or gains one, exactly one fact here flips — which is precisely the signal needed
/// before trusting the emulator as the verification substrate for further adapter work.
/// </para>
///
/// <para>
/// Each fact uses its own node paths and cleans up after itself, so they stay order-independent
/// and re-runnable against a persistent real account.
/// </para>
/// </summary>
[Trait("Category", "Cosmos")]
[Collection("Cosmos")]
public class EmulatorSmokeTests(CosmosFixture fixture)
{
    private readonly JsonSerializerOptions _options = new();

    private CosmosStorageAdapter CreateAdapter() => new(fixture.Nodes, fixture.Partitions);

    [Fact]
    public async Task Write_Then_Read_RoundTripsNode()
    {
        fixture.SkipUnlessAvailable();
        var adapter = CreateAdapter();
        const string ns = "smoke";

        var node = new MeshNode("roundtrip", ns)
        {
            Name = "roundtrip",
            NodeType = "Markdown",
        };

        await adapter.Write(node, _options).Should().Within(30.Seconds()).Emit();

        var read = await adapter.Read($"{ns}/roundtrip", _options)
            .Should().Within(30.Seconds()).Emit();

        read.Should().NotBeNull("the node was just written");
        read!.Path.Should().Be($"{ns}/roundtrip");
        read.Name.Should().Be("roundtrip");
        read.NodeType.Should().Be("Markdown");
    }

    [Fact]
    public async Task Write_IsUpsert_NotDuplicate()
    {
        fixture.SkipUnlessAvailable();
        var adapter = CreateAdapter();
        const string ns = "smoke";

        var node = new MeshNode("upsert", ns) { Name = "first", NodeType = "Markdown" };
        await adapter.Write(node, _options).Should().Within(30.Seconds()).Emit();

        // Same path, different content — must replace in place, not append a second document.
        await adapter.Write(node with { Name = "second" }, _options)
            .Should().Within(30.Seconds()).Emit();

        var read = await adapter.Read($"{ns}/upsert", _options)
            .Should().Within(30.Seconds()).Emit();
        read!.Name.Should().Be("second", "the second write must overwrite, not duplicate");
    }

    [Fact]
    public async Task Exists_TracksWriteAndDelete()
    {
        fixture.SkipUnlessAvailable();
        var adapter = CreateAdapter();
        const string ns = "smoke";
        var path = $"{ns}/exists";

        (await adapter.Exists(path).Should().Within(30.Seconds()).Emit())
            .Should().BeFalse("nothing has been written at this path yet");

        await adapter.Write(new MeshNode("exists", ns) { Name = "exists", NodeType = "Markdown" }, _options)
            .Should().Within(30.Seconds()).Emit();

        (await adapter.Exists(path).Should().Within(30.Seconds()).Emit())
            .Should().BeTrue("the node was written");

        await adapter.Delete(path).Should().Within(30.Seconds()).Emit();

        (await adapter.Exists(path).Should().Within(30.Seconds()).Emit())
            .Should().BeFalse("the node was deleted");
    }

    [Fact]
    public async Task ListChildPaths_ReturnsChildren()
    {
        fixture.SkipUnlessAvailable();
        var adapter = CreateAdapter();
        const string ns = "smokelist";

        await adapter.Write(new MeshNode("a", ns) { Name = "a", NodeType = "Markdown" }, _options)
            .Should().Within(30.Seconds()).Emit();
        await adapter.Write(new MeshNode("b", ns) { Name = "b", NodeType = "Markdown" }, _options)
            .Should().Within(30.Seconds()).Emit();

        var (nodePaths, _) = await adapter.ListChildPaths(ns)
            .Should().Within(30.Seconds()).Emit();

        nodePaths.Should().BeEquivalentTo(
            new[] { $"{ns}/a", $"{ns}/b" },
            JsonSerializerOptions.Default);
    }

    [Fact]
    public async Task QueryNodes_FiltersByNodeType()
    {
        fixture.SkipUnlessAvailable();
        var adapter = CreateAdapter();
        const string ns = "smokequery";

        await adapter.Write(new MeshNode("md", ns) { Name = "md", NodeType = "Markdown" }, _options)
            .Should().Within(30.Seconds()).Emit();
        await adapter.Write(new MeshNode("code", ns) { Name = "code", NodeType = "Code" }, _options)
            .Should().Within(30.Seconds()).Emit();

        var query = new QueryParser().Parse($"namespace:{ns} nodeType:Markdown");

        var results = new List<MeshNode>();
        await foreach (var n in adapter.QueryNodesAsync(
                           query,
                           ct: TestContext.Current.CancellationToken))
        {
            results.Add(n);
        }

        results.Select(r => r.Path).Should().BeEquivalentTo(
            new[] { $"{ns}/md" },
            JsonSerializerOptions.Default);
    }
}
