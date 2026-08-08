#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.AI;
using MeshWeaver.AI.Persistence;
using MeshWeaver.Data;
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
/// distributed setups â€” either way, a real regression signal.
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

    // Share Mesh/ServiceProvider across all [Fact]s in this class â€” saves the
    // ~190 MiB native heap that would otherwise leak per test method.
    protected override bool ShareMeshAcrossTests => true;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => builder
            .UseMonolithMesh()
            .AddFileSystemPersistence(TestDataPath)
            .AddGraph()
            .AddAI()
            .AddMeshNodes(new MeshNode(TestNodeType)
            {
                Name = "Test Product",
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

    [Fact]
    public async Task Get_AfterCreate_SeesFreshContent()
    {
        var plugin = CreatePlugin();
        var id = $"gric-{Guid.NewGuid():N}";
        var create = await plugin.Create(SeedJson(id, "Original", 1.00m, 1));
        create.Should().StartWith("Created:");

        // Immediate read â€” no artificial delay. A query-based read path would
        // sometimes return "Not found" here because the read-side index hasn't
        // caught up with the write yet. GetRemoteStream goes to the owning hub's
        // workspace so the read always reflects the write.
        var got = await plugin.Get($"@ACME/{id}");
        got.Should().Contain(id);
        got.Should().Contain("Original");
    }

    [Fact]
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

    [Fact]
    public async Task Get_NonExistentPath_ReturnsNotFoundWithinTimeout()
    {
        var plugin = CreatePlugin();
        var id = $"grim-{Guid.NewGuid():N}";

        // GetRemoteStream is a live subscription â€” without a Timeout guard it
        // would hang forever on a non-existent node. This test pins the
        // 10-second budget the production code sets.
        var got = await plugin.Get($"@ACME/{id}");
        got.Should().Contain("Not found", because: "missing node must surface, not hang");
    }

    // ---- Patch ----------------------------------------------------------------

    [Fact]
    public async Task Patch_ImmediatelyAfterCreate_MergesWithFreshContent()
    {
        var plugin = CreatePlugin();
        var id = $"pric-{Guid.NewGuid():N}";
        await plugin.Create(SeedJson(id, "Original", 1.00m, 1));

        // The critical window: Patch reads current content to merge with new
        // fields. A query-based read here races the Create write â€” with stale
        // data it would fail "node not found" or overwrite with an old version.
        var patched = await plugin.Patch($"@ACME/{id}", "{\"icon\":\"<svg/>\"}");
        patched.Should().StartWith("Patched:",
            because: "Patch must see the just-Created node; query-lagged reads would 404");
    }

    [Fact]
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

    [Fact]
    public async Task Patch_NonExistentPath_ReturnsNotFound()
    {
        var plugin = CreatePlugin();
        var id = $"prim-{Guid.NewGuid():N}";

        var patched = await plugin.Patch($"@ACME/{id}", "{\"name\":\"X\"}");
        patched.Should().Contain("not found",
            because: "missing-node error must surface, not hang on the live stream");
    }

    // ---- Update ---------------------------------------------------------------

    [Fact]
    public async Task Update_AfterCreate_VersionBumps()
    {
        var plugin = CreatePlugin();
        var id = $"upri-{Guid.NewGuid():N}";
        await plugin.Create(SeedJson(id, "Original", 1.00m, 1));

        // Fetch the created node as the agent would â€” then send it back with
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

    [Fact]
    public async Task GetDiagnostics_ForNodeOnRegisteredType_ReturnsStatusJson()
    {
        var plugin = CreatePlugin();
        // TestProduct is the NodeType; an instance of it exercises the "node has a
        // NodeType â†’ resolve its diagnostics" path which is the real agent use case.
        var id = $"diag-{Guid.NewGuid():N}";
        await plugin.Create(SeedJson(id, "Diag", 1m, 1));

        var result = await plugin.GetDiagnostics($"@ACME/{id}");
        result.Should().Contain("nodeTypePath",
            because: "GetDiagnostics on an instance resolves to its NodeType â€” refactor must preserve this shape");
    }

    [Fact]
    public async Task GetDiagnostics_NonExistentPath_ReturnsUnknown()
    {
        var plugin = CreatePlugin();
        var result = await plugin.GetDiagnostics($"@ACME/doesnotexist-{Guid.NewGuid():N}");
        result.Should().Contain("Unknown",
            because: "missing-node diagnostics must surface, not hang");
    }

    // ---- ExecuteScript --------------------------------------------------------

    /// <summary>
    /// End-to-end guard for the #912 fix on the FILE-SYSTEM backend: an <c>IsExecutable</c> Code
    /// node must survive the persistence round-trip and dispatch.
    ///
    /// <para>This class persists via <c>AddFileSystemPersistence</c>, and
    /// <c>CSharpFileParser</c> used to claim EVERY <c>CodeConfiguration</c> and write only the
    /// bare code — the node read back <c>IsExecutable = false</c> and the run was (truthfully)
    /// refused; this test was the labelled characterization of that loss. <c>CanSerialize</c> now
    /// declines configurations a bare <c>.cs</c> file cannot represent, so this node falls
    /// through to the lossless whole-node JSON form and the flag survives.</para>
    ///
    /// <para>The in-memory happy path (activity reaches a terminal status) is
    /// <c>ExecuteScriptDispatchDiagnosticsTest.Dispatch_Succeeds_NamesAnActivityThatReachesATerminalStatus</c>;
    /// this test pins the persistence-backend half: dispatch is ACCEPTED after a file-system
    /// round-trip, with a real activity path.</para>
    /// </summary>
    [Fact]
    public async Task ExecuteScript_ForIsExecutableCodeNode_DispatchesAfterFileSystemRoundTrip()
    {
        // Seed the script directly via IMeshService (the "created through
        // IMeshService" path per our testing rule â€” script nodes skip the MCP
        // plugin Create so we're not tangling the test with Create semantics).
        var id = $"exec-{Guid.NewGuid():N}";
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        await meshService.CreateNode(
            new MeshNode(id, "Scripts")
            {
                Name = "Hello Script",
                NodeType = "Code",
                Content = new CodeConfiguration
                {
                    Code = "Console.WriteLine(\"hello from test\"); 1+1",
                    IsExecutable = true
                }
            }).Should().Emit();

        var ops = new MeshOperations(Mesh);
        var result = await ops.ExecuteScript($"@Scripts/{id}", timeoutSeconds: 30)
            .Should().Within(35.Seconds()).Emit();

        var verdict = JsonDocument.Parse(result).RootElement;

        // The routing worked (we got a real verdict from the owning hub, not a timeout or a
        // "not found"), and the round-trip preserved IsExecutable — the hub accepts the run.
        verdict.GetProperty("status").GetString().Should().Be("Dispatched", result);
        verdict.GetProperty("activityPath").GetString().Should().Contain("/_Activity/",
            "a dispatched run names the real activity node the caller can poll");
    }

    // NOTE: There used to be an `ExecuteScript_ForBrokenScript_ReportsErrorStatus`
    // test here â€” it was removed because the premise didn't match the API contract.
    // ExecuteScriptResponse only carries dispatch status (Success/Failure of the
    // kernel handing off the work). Runtime exceptions inside the script land in
    // the OUTPUT AREA, not in the response. A test for runtime-error surfacing must
    // subscribe to OutputAreaReference and assert on the rendered control there.

    /// <summary>
    /// A script that targets a node which is not flagged executable must be rejected
    /// up-front (no kernel dispatch, no silent success). Reject is a synchronous
    /// path: HandleExecuteScript reads the local workspace, sees IsExecutable=false,
    /// and posts the error response â€” should be milliseconds. Anything slower means
    /// an await/deadlock has slipped into the read path.
    ///
    /// <para>Pre-#841 this test asserted the DEFECT: ExecuteScript was fire-and-forget, so
    /// the refusal went to a caller that never observed it, the reply still said
    /// "Dispatched", and the only way to detect the rejection was to poll the (guessed)
    /// activity path and confirm nothing ever appeared. The dispatch now waits for the
    /// owning hub's verdict, so the refusal is asserted where it belongs — in the reply —
    /// and the "sleep then check nothing happened" probe is gone.</para>
    /// </summary>
    // Timeout includes the cold class init for the first [Fact] in this
    // ShareMeshAcrossTests class â€” building the SP + AddGraph + AddAI is
    // ~3-7s alone.
    [Fact]
    public async Task ExecuteScript_ForNonExecutableCodeNode_ReportsError()
    {
        var id = $"noexec-{Guid.NewGuid():N}";
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        await meshService.CreateNode(
            new MeshNode(id, "Scripts")
            {
                Name = "Non-Executable",
                NodeType = "Code",
                Content = new CodeConfiguration
                {
                    Code = "Console.WriteLine(\"should not run\");",
                    IsExecutable = false
                }
            });

        var ops = new MeshOperations(Mesh);
        var result = await ops.ExecuteScript($"@Scripts/{id}", timeoutSeconds: 30)
            .FirstAsync().ToTask();

        var verdict = JsonDocument.Parse(result).RootElement;
        verdict.GetProperty("status").GetString().Should().Be("Error", result);
        verdict.GetProperty("message").GetString().Should().Contain("IsExecutable");
        verdict.TryGetProperty("activityPath", out _).Should().BeFalse(
            "non-executable Code nodes must NOT spawn an ActivityLog, so the caller must not "
            + "be handed a path to poll — the refusal itself is the answer.");
    }

    // ---- Delete + Get --------------------------------------------------------

    [Fact]
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
    /// Minimal IAgentChat stub â€” MeshPlugin only reads ExecutionContext + Context,
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
