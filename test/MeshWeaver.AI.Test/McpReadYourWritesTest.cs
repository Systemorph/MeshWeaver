#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.AI;
using MeshWeaver.AI.Persistence;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Read-your-writes coverage for every MCP method that returns node content.
/// Each test writes something, then immediately reads back through the same
/// plugin surface, asserting the read sees the fresh state.
///
/// These tests exist to catch the staleness / wrong-read-path bug class that
/// <see cref="McpReturnTimingTest"/> could not detect: returning in time is
/// not the same as returning correct content. If `MeshOperations` ever reverts
/// to a query-based content read (which goes through a lagged read-side index),
/// these tests catch it deterministically in single-process and flake in
/// distributed setups — either way, a real regression signal.
///
/// Covers: Get (existing node + post-Create + post-Patch + not-found),
/// Patch (merges with current content, not cached / indexed value),
/// Update (full replacement sees latest version),
/// GetDiagnostics (reads current NodeType metadata).
/// Also covers a couple of back-to-back operations that specifically
/// exercised the lost-update risk in the query-based Patch.
/// </summary>
public class McpReadYourWritesTest : MonolithMeshTestBase
{
    private const string TestNodeType = nameof(TestProduct);
    private static readonly string TestDataPath = Path.Combine(AppContext.BaseDirectory, "TestData-rwy");

    public McpReadYourWritesTest(ITestOutputHelper output) : base(output) { }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => builder
            .UseMonolithMesh()
            .AddFileSystemPersistence(TestDataPath)
            .AddGraph()
            .AddAI()
            .AddMeshNodes(new MeshNode(TestNodeType)
            {
                Name = "Test Product",
                AssemblyLocation = typeof(McpReadYourWritesTest).Assembly.Location,
                HubConfiguration = config => config
                    .AddMeshDataSource(source => source.WithContentType<TestProduct>())
                    .AddDefaultLayoutAreas()
            });

    private MeshPlugin CreatePlugin() => new(Mesh, new MinimalChat());

    private static string SeedJson(string id, string name, decimal price, int qty) =>
        JsonSerializer.Serialize(new
        {
            id,
            @namespace = "ACME",
            name,
            nodeType = TestNodeType,
            content = new { name = "Widget", price, quantity = qty }
        });

    // ---- Get ------------------------------------------------------------------

    [Fact(Timeout = 30_000)]
    public async Task Get_AfterCreate_SeesFreshContent()
    {
        var plugin = CreatePlugin();
        var id = $"gric-{Guid.NewGuid():N}";
        var create = await plugin.Create(SeedJson(id, "Original", 1.00m, 1));
        create.Should().StartWith("Created:");

        // Immediate read — no artificial delay. A query-based read path would
        // sometimes return "Not found" here because the read-side index hasn't
        // caught up with the write yet. GetRemoteStream goes to the owning hub's
        // workspace so the read always reflects the write.
        var got = await plugin.Get($"@ACME/{id}");
        got.Should().Contain(id);
        got.Should().Contain("Original");
    }

    [Fact(Timeout = 30_000)]
    public async Task Get_AfterPatch_SeesUpdatedName()
    {
        var plugin = CreatePlugin();
        var id = $"grip-{Guid.NewGuid():N}";
        await plugin.Create(SeedJson(id, "Before", 1.00m, 1));

        var patched = await plugin.Patch($"@ACME/{id}", "{\"name\":\"After\"}");
        patched.Should().StartWith("Patched:");

        var got = await plugin.Get($"@ACME/{id}");
        got.Should().Contain("After");
        got.Should().NotContain("\"name\":\"Before\"",
            because: "Get must return current content, not an indexed snapshot from before the Patch");
    }

    [Fact(Timeout = 30_000)]
    public async Task Get_NonExistentPath_ReturnsNotFoundWithinTimeout()
    {
        var plugin = CreatePlugin();
        var id = $"grim-{Guid.NewGuid():N}";

        // GetRemoteStream is a live subscription — without a Timeout guard it
        // would hang forever on a non-existent node. This test pins the
        // 10-second budget the production code sets.
        var got = await plugin.Get($"@ACME/{id}");
        got.Should().Contain("Not found", because: "missing node must surface, not hang");
    }

    // ---- Patch ----------------------------------------------------------------

    [Fact(Timeout = 30_000)]
    public async Task Patch_ImmediatelyAfterCreate_MergesWithFreshContent()
    {
        var plugin = CreatePlugin();
        var id = $"pric-{Guid.NewGuid():N}";
        await plugin.Create(SeedJson(id, "Original", 1.00m, 1));

        // The critical window: Patch reads current content to merge with new
        // fields. A query-based read here races the Create write — with stale
        // data it would fail "node not found" or overwrite with an old version.
        var patched = await plugin.Patch($"@ACME/{id}", "{\"icon\":\"<svg/>\"}");
        patched.Should().StartWith("Patched:",
            because: "Patch must see the just-Created node; query-lagged reads would 404");
    }

    [Fact(Timeout = 30_000)]
    public async Task Patch_TwiceInARow_PreservesFirstPatchesChanges()
    {
        var plugin = CreatePlugin();
        var id = $"prit-{Guid.NewGuid():N}";
        await plugin.Create(SeedJson(id, "Original", 1.00m, 1));

        await plugin.Patch($"@ACME/{id}", "{\"icon\":\"<svg>A</svg>\"}");
        await plugin.Patch($"@ACME/{id}", "{\"category\":\"Widgets\"}");

        var got = await plugin.Get($"@ACME/{id}");
        // Lost-update smell test: both patches' changes must be present. If
        // the second Patch read stale data (pre-first-patch), its merge would
        // blow away the icon that the first patch set.
        // The serialised node JSON escapes < > so assert on the escaped / contained form.
        got.Should().Contain("svg", because: "first Patch's icon must survive the second Patch");
        got.Should().Contain("Widgets");
    }

    [Fact(Timeout = 30_000)]
    public async Task Patch_NonExistentPath_ReturnsNotFound()
    {
        var plugin = CreatePlugin();
        var id = $"prim-{Guid.NewGuid():N}";

        var patched = await plugin.Patch($"@ACME/{id}", "{\"name\":\"X\"}");
        patched.Should().Contain("not found",
            because: "missing-node error must surface, not hang on the live stream");
    }

    // ---- Update ---------------------------------------------------------------

    [Fact(Timeout = 30_000)]
    public async Task Update_AfterCreate_VersionBumps()
    {
        var plugin = CreatePlugin();
        var id = $"upri-{Guid.NewGuid():N}";
        await plugin.Create(SeedJson(id, "Original", 1.00m, 1));

        // Fetch the created node as the agent would — then send it back with
        // updated fields. Update's implementation does not read pre-state
        // (full replacement semantics), so it's here to assert the round-trip
        // still works end-to-end after our CQRS refactor.
        var fetched = await plugin.Get($"@ACME/{id}");
        var node = JsonDocument.Parse(fetched).RootElement;
        var updated = JsonSerializer.Serialize(new object[]
        {
            new
            {
                id,
                @namespace = "ACME",
                name = "Renamed",
                nodeType = TestNodeType,
                content = new { name = "Widget Deluxe", price = 9.99m, quantity = 5 },
                version = node.GetProperty("version").GetInt64()
            }
        });

        var result = await plugin.Update(updated);
        result.Should().StartWith("Updated:");

        var got = await plugin.Get($"@ACME/{id}");
        got.Should().Contain("Renamed");
        got.Should().Contain("Widget Deluxe");
    }

    // ---- GetDiagnostics -------------------------------------------------------

    [Fact(Timeout = 30_000)]
    public async Task GetDiagnostics_ForNodeOnRegisteredType_ReturnsStatusJson()
    {
        var plugin = CreatePlugin();
        // TestProduct is the NodeType; an instance of it exercises the "node has a
        // NodeType → resolve its diagnostics" path which is the real agent use case.
        var id = $"diag-{Guid.NewGuid():N}";
        await plugin.Create(SeedJson(id, "Diag", 1m, 1));

        var result = await plugin.GetDiagnostics($"@ACME/{id}");
        result.Should().Contain("nodeTypePath",
            because: "GetDiagnostics on an instance resolves to its NodeType — refactor must preserve this shape");
    }

    [Fact(Timeout = 30_000)]
    public async Task GetDiagnostics_NonExistentPath_ReturnsUnknown()
    {
        var plugin = CreatePlugin();
        var result = await plugin.GetDiagnostics($"@ACME/doesnotexist-{Guid.NewGuid():N}");
        result.Should().Contain("Unknown",
            because: "missing-node diagnostics must surface, not hang");
    }

    // ---- ExecuteScript --------------------------------------------------------

    [Fact(Timeout = 60_000)]
    public async Task ExecuteScript_ForIsExecutableCodeNode_CompletesWithoutError()
    {
        // Seed the script directly via IMeshService (the "created through
        // IMeshService" path per our testing rule — script nodes skip the MCP
        // plugin Create so we're not tangling the test with Create semantics).
        var id = $"exec-{Guid.NewGuid():N}";
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        await meshService.CreateNodeAsync(
            new MeshNode(id, "Scripts")
            {
                Name = "Hello Script",
                NodeType = "Code",
                Content = new CodeConfiguration
                {
                    Code = "Console.WriteLine(\"hello from test\"); 1+1",
                    IsExecutable = true
                }
            });

        var ops = new MeshOperations(Mesh);
        var result = await ops.ExecuteScript($"@Scripts/{id}", timeoutSeconds: 30)
            .FirstAsync().ToTask();

        // Budget is 30s on the kernel completion callback. The key assertion is
        // that ExecuteScript does NOT hang beyond the budget AND doesn't return
        // "Not found" (which would signal the content-read path failed). Either
        // "Executed" (kernel actually ran) or "Timeout" (kernel took longer)
        // means the routing worked.
        result.Should().NotContain("\"status\":\"Error\"",
            because: "the content read of an existing Code node must succeed — Error here means the script wasn't found or wasn't recognised as executable");
    }

    // ---- Delete + Get --------------------------------------------------------

    [Fact(Timeout = 30_000)]
    public async Task Get_AfterDelete_ReturnsNotFound()
    {
        var plugin = CreatePlugin();
        var id = $"grid-{Guid.NewGuid():N}";
        await plugin.Create(SeedJson(id, "ToDelete", 1m, 1));

        var del = await plugin.Delete(JsonSerializer.Serialize(new[] { $"ACME/{id}" }));
        del.Should().StartWith("Deleted:");

        var got = await plugin.Get($"@ACME/{id}");
        got.Should().Contain("Not found",
            because: "Get after Delete must NOT return a stale cached version of the deleted node");
    }

    /// <summary>
    /// Minimal IAgentChat stub — MeshPlugin only reads ExecutionContext + Context,
    /// so nulls are fine. Duplicated from other tests so each file is self-contained.
    /// </summary>
    private sealed class MinimalChat : IAgentChat
    {
        public AgentContext? Context => null;
        public ThreadExecutionContext? ExecutionContext => null;
        public void SetContext(AgentContext? applicationContext) { }
        public void SetSelectedAgent(string? agentName) { }
        public Task ResumeAsync(ChatConversation conversation) => Task.CompletedTask;
        public Task<IReadOnlyList<AgentDisplayInfo>> GetOrderedAgentsAsync()
            => Task.FromResult<IReadOnlyList<AgentDisplayInfo>>(new List<AgentDisplayInfo>());
        public async IAsyncEnumerable<ChatMessage> GetResponseAsync(
            IReadOnlyCollection<ChatMessage> messages,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        { await Task.CompletedTask; yield break; }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IReadOnlyCollection<ChatMessage> messages,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        { await Task.CompletedTask; yield break; }
        public void SetThreadId(string threadId) { }
        public void DisplayLayoutArea(LayoutAreaControl layoutAreaControl) { }
    }
}
