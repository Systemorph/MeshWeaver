#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
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
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Regression coverage for the 2026-04-14 cached-display incident: agent reported a
/// successful Patch on FinalReport, persistence committed, but the in-memory workspace
/// stream kept emitting the stale node — Blazor views displayed the old content until
/// grain deactivation / circuit close re-loaded from persistence.
///
/// Root cause was in <c>HandleUpdateNodeRequest</c> (<c>MeshExtensions.cs:679</c>) —
/// the workspace-refresh <c>DataChangeRequest</c> was fire-and-forget, and the
/// <c>UpdateNodeResponse.Ok</c> went out before the workspace observed the change.
/// The fix uses Post + RegisterCallback inline (no TCS, no await) so Ok is sent only
/// after the workspace acks — and DataChangeStatus.Failed / DeliveryFailure paths
/// surface as <c>UpdateNodeResponse.Fail</c> so the caller sees actual errors.
///
/// These tests also stress concurrent patches: a deadlock in the plugin layer would
/// have been caught here under load, since N parallel Patches all want to await
/// hub-backed operations on a single hub scheduler.
/// </summary>
public class PatchWorkspaceAckTest : MonolithMeshTestBase
{
    private const string TestNodeType = nameof(TestProduct);
    private static readonly string TestDataPath = Path.Combine(AppContext.BaseDirectory, "TestData");

    public PatchWorkspaceAckTest(ITestOutputHelper output) : base(output) { }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => builder
            .UseMonolithMesh()
            .AddFileSystemPersistence(TestDataPath)
            .AddGraph()
            .AddAI()
            .AddMeshNodes(new MeshNode(TestNodeType)
            {
                Name = "Test Product",
                AssemblyLocation = typeof(PatchWorkspaceAckTest).Assembly.Location,
                HubConfiguration = config => config
                    .AddMeshDataSource(source => source.WithContentType<TestProduct>())
                    .AddDefaultLayoutAreas()
            });

    private MeshPlugin CreatePlugin() => new(Mesh, new MinimalChat());

    private async Task<string> SeedAsync(MeshPlugin plugin, string id) =>
        await plugin.Create(JsonSerializer.Serialize(new
        {
            id,
            @namespace = "ACME",
            name = "Original",
            nodeType = TestNodeType,
            content = new { name = "Widget", price = 1.00m, quantity = 1 }
        })) is var seed && seed.StartsWith("Created:")
            ? $"ACME/{id}"
            : throw new Xunit.Sdk.XunitException($"Seed failed: {seed}");

    /// <summary>
    /// The cache-bug regression: after Patch returns Ok, the next Get must reflect the
    /// new state immediately. Before the HandleUpdateNodeRequest fix this would race —
    /// Get could read the stale workspace cache because Ok was returned before the
    /// DataChangeRequest fan-out had been observed by the workspace.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Patch_AfterOk_GetReturnsNewState()
    {
        var plugin = CreatePlugin();
        var id = $"ack-{Guid.NewGuid():N}";
        var path = await SeedAsync(plugin, id);

        var patched = await plugin.Patch($"@{path}", "{\"name\":\"Renamed by patch\"}");
        patched.Should().StartWith("Patched:", because: "valid patch must succeed");

        // Immediately after Ok, Get must see the new state — no fresh page-load required.
        var got = await plugin.Get($"@{path}");
        got.Should().Contain("Renamed by patch",
            because: "the workspace must already reflect the patch when Ok is returned " +
                     "(otherwise we have the cached-display bug)");
    }

    /// <summary>
    /// Workspace remote-stream view of the same fix: subscribing to the workspace
    /// stream right after Patch should observe the updated node.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Patch_AfterOk_WorkspaceStreamReflectsNewState()
    {
        var plugin = CreatePlugin();
        var id = $"ws-{Guid.NewGuid():N}";
        var path = await SeedAsync(plugin, id);

        var patched = await plugin.Patch($"@{path}", "{\"name\":\"Updated via stream test\"}");
        patched.Should().StartWith("Patched:");

        // Workspace stream of the node hub must observe the new name within a short window.
        var nodeHub = Mesh.GetHostedHub(new Address(path));
        var workspace = nodeHub.GetWorkspace();
        var stream = workspace.GetStream<MeshNode>();
        stream.Should().NotBeNull("the hub must expose a MeshNode stream");

        var observedName = await stream!
            .Where(nodes => nodes != null && nodes.Any(n => n.Path == path))
            .Select(nodes => nodes!.First(n => n.Path == path).Name)
            .Where(name => name == "Updated via stream test")
            .Timeout(5.Seconds())
            .FirstAsync();

        observedName.Should().Be("Updated via stream test");
    }

    /// <summary>
    /// Negative scenario: patching a node that doesn't exist must return a clean
    /// "not found" error string, not crash and not silently succeed.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task Patch_NonExistentPath_ReturnsNotFound()
    {
        var plugin = CreatePlugin();
        var result = await plugin.Patch(
            "@ACME/definitely-does-not-exist-" + Guid.NewGuid().ToString("N"),
            "{\"name\":\"x\"}");

        result.Should().Contain("Error");
        result.Should().Contain("not found",
            because: "agent must be told the path is bad so it can re-Search and retry");
    }

    /// <summary>
    /// Negative scenario: patching with an empty name (which existing validator rejects)
    /// must surface as a "Error: cannot patch ... 'name' is empty" error — not silent
    /// success, not a deadlock, not a stale workspace.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task Patch_EmptyName_ReturnsValidatorError()
    {
        var plugin = CreatePlugin();
        var id = $"empty-{Guid.NewGuid():N}";
        var path = await SeedAsync(plugin, id);

        var result = await plugin.Patch($"@{path}", "{\"name\":\"\"}");

        result.Should().Contain("Error");
        result.Should().Contain("name",
            because: "validator must call out which field was rejected");
    }

    /// <summary>
    /// Negative scenario: patching with explicit null content must be rejected with
    /// the schema embedded so the agent can correct on the next call.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task Patch_NullContent_ReturnsErrorWithSchema()
    {
        var plugin = CreatePlugin();
        var id = $"nullc-{Guid.NewGuid():N}";
        var path = await SeedAsync(plugin, id);

        var result = await plugin.Patch($"@{path}", "{\"content\":null}");

        result.Should().Contain("Error");
        result.Should().Contain("content");
    }

    /// <summary>
    /// Stress / deadlock regression: 10 concurrent Patch calls on independent nodes
    /// must all complete within the timeout window. Before the no-await refactor of
    /// the plugin layer (commit d165533c8) the await hub.AwaitResponse pattern would
    /// deadlock the hub scheduler under any concurrent load — this test would hang
    /// past its method timeout.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task Patch_ConcurrentUpdates_NoDeadlock()
    {
        var plugin = CreatePlugin();
        const int concurrency = 10;

        // Seed N independent nodes
        var paths = await Task.WhenAll(Enumerable.Range(0, concurrency)
            .Select(i => SeedAsync(plugin, $"conc-{i:00}-{Guid.NewGuid():N}")));

        // Fire all patches in parallel
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var results = await Task.WhenAll(paths.Select((path, i) =>
            plugin.Patch($"@{path}", $"{{\"name\":\"renamed-{i}\"}}")));
        sw.Stop();

        results.Should().AllSatisfy(r => r.Should().StartWith("Patched:",
            because: "every concurrent patch must complete successfully — a deadlock would manifest as timeout"));
        sw.Elapsed.Should().BeLessThan(45.Seconds(),
            because: "10 trivial patches in parallel should finish well under the timeout; if it's close, we likely have lock contention even without full deadlock");
    }

    /// <summary>
    /// Minimal IAgentChat stub — MeshPlugin only reads ExecutionContext and Context.
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
        public async IAsyncEnumerable<ChatMessage> GetResponseAsync(IReadOnlyCollection<ChatMessage> messages,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        { await Task.CompletedTask; yield break; }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IReadOnlyCollection<ChatMessage> messages,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        { await Task.CompletedTask; yield break; }
        public void SetThreadId(string threadId) { }
        public void DisplayLayoutArea(LayoutAreaControl layoutAreaControl) { }
    }
}
