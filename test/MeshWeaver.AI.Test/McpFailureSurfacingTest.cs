using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Locks the contract that the MCP mesh tools (<see cref="MeshOperations"/>) ALWAYS surface a
/// human-readable <c>"Error…"</c> string when a mutation cannot be performed — they never return
/// a success line for a node that was not actually created/updated.
///
/// <para>Motivation: on atioz an MCP <c>create</c> of a node with an unregistered NodeType returned
/// <c>"Created"</c> while the node landed as a bare row (no nodeType / no content). The agent then
/// believed the node existed. These tests pin every failure mode at the MCP boundary so a silent
/// success can never regress unnoticed. DB-free (in-memory monolith), so they run in CI without
/// Postgres.</para>
/// </summary>
public class McpFailureSurfacingTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static CancellationToken Ct => new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token;

    private string Json(MeshNode node) => JsonSerializer.Serialize(node, Mesh.JsonSerializerOptions);

    private Task<string> CreateAsync(string json) =>
        new MeshOperations(Mesh).Create(json).FirstAsync().Timeout(TimeSpan.FromSeconds(45)).ToTask(Ct);

    private Task<string> UpdateAsync(string json) =>
        new MeshOperations(Mesh).Update(json).FirstAsync().Timeout(TimeSpan.FromSeconds(45)).ToTask(Ct);

    private Task<string> PatchAsync(string path, string fields) =>
        new MeshOperations(Mesh).Patch(path, fields).FirstAsync().Timeout(TimeSpan.FromSeconds(45)).ToTask(Ct);

    private MeshNode ValidMarkdown(string id, string ns = TestPartition) =>
        new(id, ns) { Name = id, NodeType = "Markdown", Content = new MarkdownContent { Content = $"# {id}" } };

    [Fact(Timeout = 60000)]
    public async Task Create_InvalidJson_ReturnsError()
    {
        var result = await CreateAsync("not json at all");
        result.Should().StartWith("Invalid JSON",
            "unparseable input must surface as an error, never a silent success");
    }

    [Fact(Timeout = 60000)]
    public async Task Create_MissingName_ReturnsError()
    {
        var node = new MeshNode("NoName" + Guid.NewGuid().ToString("N")[..8], TestPartition)
        {
            NodeType = "Markdown",
            Content = new MarkdownContent { Content = "# x" }
        };
        var result = await CreateAsync(Json(node));
        result.Should().StartWith("Error").And.Contain("name");
    }

    /// <summary>
    /// THE rsalzmann CASE. An unregistered NodeType must be rejected with an error, NOT
    /// silently created. (In-memory the type-existence check fires because the NodeType
    /// string survives deserialization; this pins that the MCP layer relays the rejection.)
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task Create_UnregisteredNodeType_ReturnsError()
    {
        var node = new MeshNode("BadType" + Guid.NewGuid().ToString("N")[..8], TestPartition)
        {
            Name = "Bad Type",
            NodeType = "ThisTypeDoesNotExist",
            Content = new MarkdownContent { Content = "# x" }
        };
        var result = await CreateAsync(Json(node));
        result.Should().StartWith("Error",
            "an unregistered NodeType must surface as an MCP error, not a 'Created' line for a bare node");
        result.Should().Contain("ThisTypeDoesNotExist");
    }

    [Fact(Timeout = 60000)]
    public async Task Create_BareNode_NoTypeNoContent_ReturnsError()
    {
        var node = new MeshNode("Bare" + Guid.NewGuid().ToString("N")[..8], TestPartition) { Name = "Bare" };
        var result = await CreateAsync(Json(node));
        result.Should().StartWith("Error",
            "a node with neither NodeType nor Content cannot spawn a hub and must be rejected");
    }

    [Fact(Timeout = 60000)]
    public async Task Create_DuplicateNode_ReturnsError()
    {
        var json = Json(ValidMarkdown("Dup" + Guid.NewGuid().ToString("N")[..8]));

        var first = await CreateAsync(json);
        first.Should().StartWith("Created", "the first create of a fresh path must succeed");

        var second = await CreateAsync(json);
        second.Should().StartWith("Error",
            "creating a node that already exists must surface as an error, not a second 'Created'");
    }

    [Fact(Timeout = 60000)]
    public async Task Update_NonExistentNode_ReturnsError()
    {
        var json = "[" + Json(ValidMarkdown("Ghost" + Guid.NewGuid().ToString("N")[..8])) + "]";
        var result = await UpdateAsync(json);
        result.Should().Contain("Error",
            "updating a node that does not exist must surface as an error");
    }

    [Fact(Timeout = 60000)]
    public async Task Update_MissingNodeType_ReturnsError()
    {
        // Update requires a complete node; a missing nodeType is a caller error.
        var node = new MeshNode("NoType" + Guid.NewGuid().ToString("N")[..8], TestPartition)
        {
            Name = "No Type",
            Content = new MarkdownContent { Content = "# x" }
        };
        var result = await UpdateAsync("[" + Json(node) + "]");
        result.Should().Contain("Error").And.Contain("nodeType");
    }

    [Fact(Timeout = 60000)]
    public async Task Patch_NonExistentNode_ReturnsError()
    {
        var path = $"{TestPartition}/Ghost{Guid.NewGuid():N}";
        var result = await PatchAsync(path, """{"name":"Updated"}""");
        result.Should().StartWith("Error",
            "patching a node that does not exist must surface as an error, not a silent no-op success");
    }

    /// <summary>
    /// The rsalzmann "HelloWorld" case (the runnable, DB-free counterpart of
    /// <c>OrleansTopLevelPartitionGuardTest</c>). A top-level (empty namespace) node whose
    /// NodeType does not own a partition must be rejected — even for the admin identity the
    /// test base logs in — with a SPEAKING message that tells the caller to use their own
    /// user space or a Space. Pre-fix the create silently succeeds (the partition write guard
    /// fails open); post-fix it is rejected by the top-level partition rule.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task Create_TopLevelNonPartition_ReturnsSpeakingError()
    {
        // Empty namespace ⇒ top-level. Markdown does not own a partition.
        var node = new MeshNode("HelloWorld" + Guid.NewGuid().ToString("N")[..8])
        {
            Name = "Hello World",
            NodeType = "Markdown",
            Content = new MarkdownContent { Content = "# Hello World" }
        };

        var result = await CreateAsync(Json(node));

        result.Should().StartWith("Error",
            "a top-level non-partition node must be rejected even for an admin (only User/Space may root a partition)");
        result.Should().Contain("top level",
            "the message must explain the root is reserved for partitions");
        result.Should().Contain("Space",
            "the message must guide the caller to put content in their user space or a Space");
    }
}
