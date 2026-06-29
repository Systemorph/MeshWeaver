using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.AI.Plugins;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Negative-path coverage for the MCP mesh tools (<see cref="MeshOperations"/> + the chunk-navigation
/// tools): every failure mode must surface a human-readable <c>Error</c>/<c>Not found</c>/failure envelope —
/// NEVER a silent success or a "found" line for something that isn't there. Complements
/// <see cref="McpFailureSurfacingTest"/> (create/update/patch) with the remaining verbs (get, delete, move,
/// copy, execute_script), the "operate on a DELETED node" case, a missing-access case, and the
/// content-chunk tools. DB-free (in-memory monolith), so it runs in CI without Postgres.
/// </summary>
public class McpNegativeOperationsTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static CancellationToken Ct => new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token;
    private MeshOperations Ops => new(Mesh);
    private string Json(MeshNode node) => JsonSerializer.Serialize(node, Mesh.JsonSerializerOptions);

    private Task<string> Run(IObservable<string> op) =>
        op.FirstAsync().Timeout(TimeSpan.FromSeconds(45)).ToTask(Ct);

    private MeshNode ValidMarkdown(string id, string ns = TestPartition) =>
        new(id, ns) { Name = id, NodeType = "Markdown", Content = new MarkdownContent { Content = $"# {id}" } };

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8];

    // ── get / delete / move / copy / execute_script: not-found surfaces, never a phantom success ──

    [Fact(Timeout = 60000)]
    public async Task Get_NonExistentNode_ReturnsNotFound()
    {
        var result = await Run(Ops.Get($"{TestPartition}/{Unique("Ghost")}"));
        result.Should().StartWith("Not found",
            "getting a node that does not exist must surface 'Not found', not an empty/successful envelope");
    }

    [Fact(Timeout = 60000)]
    public async Task Delete_NonExistentNode_ReturnsError()
    {
        // `paths` is a JSON array of paths.
        var result = await Run(Ops.Delete($"[\"{TestPartition}/{Unique("Ghost")}\"]"));
        result.Should().Contain("Error",
            "deleting a node that does not exist must surface as an error, not a 'Deleted' line");
    }

    [Fact(Timeout = 60000)]
    public async Task Move_NonExistentSource_ReturnsError()
    {
        var result = await Run(Ops.Move($"{TestPartition}/{Unique("Ghost")}", $"{TestPartition}/{Unique("Dest")}"));
        result.Should().Contain("Error",
            "moving a node that does not exist must surface as an error, not a 'Moved' line");
    }

    [Fact(Timeout = 60000)]
    public async Task Move_MissingArgs_ReturnsError()
    {
        (await Run(Ops.Move("", $"{TestPartition}/x"))).Should().StartWith("Error").And.Contain("sourcePath");
        (await Run(Ops.Move($"{TestPartition}/x", ""))).Should().StartWith("Error").And.Contain("targetPath");
    }

    [Fact(Timeout = 60000)]
    public async Task Copy_NonExistentSource_ReturnsError()
    {
        var result = await Run(Ops.Copy($"{TestPartition}/{Unique("Ghost")}", TestPartition));
        result.Should().Contain("Error",
            "copying a node that does not exist must surface as an error, not a 'Copied' line");
    }

    [Fact(Timeout = 60000)]
    public async Task ExecuteScript_NonExistentNode_ReturnsErrorStatus()
    {
        var result = await Run(Ops.ExecuteScript($"{TestPartition}/{Unique("Ghost")}"));
        // ExecuteScript returns a JSON envelope; a missing/un-runnable target must report status:"Error".
        using var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("status").GetString()
            .Should().NotBe("Success", "executing a non-existent script node must not report success");
    }

    // ── the "file deleted" case: every verb on a node that WAS deleted must fail cleanly ──

    [Fact(Timeout = 60000)]
    public async Task DeletedNode_EveryVerb_FailsCleanly()
    {
        var node = ValidMarkdown(Unique("Doomed"));
        (await Run(Ops.Create(Json(node)))).Should().StartWith("Created", "setup: the node is created first");
        (await Run(Ops.Delete($"[\"{node.Path}\"]"))).Should().StartWith("Deleted", "setup: then deleted");

        // After deletion, no verb may report success for the now-missing node.
        (await Run(Ops.Get(node.Path))).Should().StartWith("Not found",
            "GET on a deleted node must say 'Not found'");
        (await Run(Ops.Update("[" + Json(node) + "]"))).Should().Contain("Error",
            "UPDATE on a deleted node must surface an error");
        (await Run(Ops.Patch(node.Path, """{"name":"Revived"}"""))).Should().StartWith("Error",
            "PATCH on a deleted node must surface an error, not a silent no-op success");
        (await Run(Ops.Move(node.Path, $"{TestPartition}/{Unique("Dest")}"))).Should().Contain("Error",
            "MOVE of a deleted node must surface an error");
    }

    // NOTE: "missing access" (a non-owner reading/writing another partition's private node) is a
    // PER-USER RLS concern enforced at the Postgres storage layer (StorageAdapterMeshQueryProvider /
    // RlsNodeValidator). The DB-free in-memory monolith here does NOT run that read-RLS pass, so a
    // cross-user denial cannot be exercised in this layer. It is covered by the PG-backed e2e MCP
    // test (McpAccessDeniedE2ETest) where real RLS applies.

    // ── content-chunk tools: bad input / not-indexed never returns a phantom chunk ──

    [Fact(Timeout = 60000)]
    public async Task GetChunk_MissingArgs_ReturnsFailureEnvelope()
    {
        var sp = Mesh.ServiceProvider;
        var noCollection = await Run(ChunkNavigation.GetChunk(sp, "", "file.md", 0));
        AssertNotAChunk(noCollection, "missing collectionPath");

        var noFile = await Run(ChunkNavigation.GetChunk(sp, $"{TestPartition}/coll", "", 0));
        AssertNotAChunk(noFile, "missing filePath");
    }

    [Fact(Timeout = 60000)]
    public async Task GetChunk_NotIndexedFile_ReturnsFailureEnvelope()
    {
        var sp = Mesh.ServiceProvider;
        var result = await Run(ChunkNavigation.GetChunk(sp, $"{TestPartition}/coll", $"{Unique("missing")}.md", 7));
        AssertNotAChunk(result, "a file with no indexed chunks");
    }

    [Fact(Timeout = 60000)]
    public async Task SearchChunks_NoMatches_ReturnsEmptyEnvelopeWithMessage()
    {
        var sp = Mesh.ServiceProvider;
        var result = await Run(ChunkNavigation.SearchChunks(sp, Unique("zzz nonsense query"), TestPartition));
        using var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("count").GetInt32().Should().Be(0,
            "a chunk search with no matches (or no store) must return count:0, never a phantom hit");
    }

    /// <summary>A get_chunk failure envelope: parseable JSON that is NOT a real chunk (found:false / no text)
    /// and carries an explanatory message — whether the store is absent (NotAvailable) or the input is bad.</summary>
    private static void AssertNotAChunk(string result, string because)
    {
        using var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;
        var isFound = root.TryGetProperty("found", out var f) && f.ValueKind == JsonValueKind.True;
        isFound.Should().BeFalse($"get_chunk for {because} must not report a found chunk");
        root.TryGetProperty("text", out _).Should().BeFalse($"get_chunk for {because} must not return chunk text");
    }
}
