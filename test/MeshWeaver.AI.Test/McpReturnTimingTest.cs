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
using MeshWeaver.Messaging;
using Microsoft.Extensions.AI;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Regression coverage for the blocking-MCP-methods class of failure:
/// every <see cref="MeshPlugin"/> method must return within a bounded time.
/// The original incident was <c>Patch</c> timing out at 30 s because an
/// exception in the Subscribe callback left the TCS unresolved. This test
/// suite guards every public method from that pattern — the test itself
/// asserts a much shorter deadline than a hang, so any regression fails
/// the build instead of sitting on a 30 s xUnit timeout.
///
/// Covers: <c>Get</c>, <c>Search</c>, <c>Create</c>, <c>Update</c>,
/// <c>Patch</c>, <c>Delete</c>, <c>Move</c>, <c>Copy</c>,
/// <c>GetDiagnostics</c>, <c>Recycle</c>. <c>NavigateTo</c> / <c>GetBaseUrl</c>
/// are trivial string returns — no hub traffic — so not covered here.
/// <c>ExecuteScript</c> needs an actual kernel and lives in
/// OrleansKernelProgressTest.
///
/// Each test uses <see cref="Task.WhenAny"/> with a short budget so a
/// regression that hangs forever shows up quickly and isn't hidden by the
/// xUnit <c>[Fact(Timeout=...)]</c>.
/// </summary>
public class McpReturnTimingTest : MonolithMeshTestBase
{
    private const string TestNodeType = nameof(TestProduct);
    private const int PerCallBudgetMs = 10_000;
    private static readonly string TestDataPath = Path.Combine(AppContext.BaseDirectory, "TestData");

    public McpReturnTimingTest(ITestOutputHelper output) : base(output) { }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => builder
            .UseMonolithMesh()
            .AddFileSystemPersistence(TestDataPath)
            .AddGraph()
            .AddAI()
            .AddMeshNodes(new MeshNode(TestNodeType)
            {
                Name = "Test Product",
                AssemblyLocation = typeof(McpReturnTimingTest).Assembly.Location,
                HubConfiguration = config => config
                    .AddMeshDataSource(source => source.WithContentType<TestProduct>())
                    .AddDefaultLayoutAreas()
            });

    private MeshPlugin CreatePlugin() => new(Mesh, new MinimalChat());

    /// <summary>Bounds a call to <paramref name="budgetMs"/> ms; on timeout the test fails with a useful message.</summary>
    private static async Task<string> BoundedAsync(Task<string> call, string methodName, int budgetMs = PerCallBudgetMs)
    {
        var completed = await Task.WhenAny(call, Task.Delay(budgetMs));
        if (completed != call)
            throw new Xunit.Sdk.XunitException(
                $"{methodName} did not return within {budgetMs} ms — suspect an unresolved TaskCompletionSource " +
                $"or an un-awaited Subscribe callback in the MCP plugin. See PatchWorkspaceAckTest's " +
                $"cached-display incident for the canonical pattern.");
        return await call;
    }

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

    [Fact(Timeout = 30_000)]
    public async Task Get_ReturnsWithinBudget()
    {
        var plugin = CreatePlugin();
        var id = $"get-{Guid.NewGuid():N}";
        var path = await SeedAsync(plugin, id);

        var result = await BoundedAsync(plugin.Get($"@{path}"), nameof(plugin.Get));
        result.Should().Contain(id);
    }

    [Fact(Timeout = 30_000)]
    public async Task Search_ReturnsWithinBudget()
    {
        var plugin = CreatePlugin();
        // Narrow search to a specific path scope so we don't enumerate the whole
        // test mesh — the budget guarantee is about the Search API itself returning,
        // not about how fast it can scan every provider in a large cluster.
        var id = $"search-{Guid.NewGuid():N}";
        await SeedAsync(plugin, id);

        var result = await BoundedAsync(
            plugin.Search($"path:ACME/{id}"), nameof(plugin.Search));
        result.Should().NotBeNull();
    }

    [Fact(Timeout = 30_000)]
    public async Task Create_ReturnsWithinBudget()
    {
        var plugin = CreatePlugin();
        var id = $"create-{Guid.NewGuid():N}";
        var call = plugin.Create(JsonSerializer.Serialize(new
        {
            id,
            @namespace = "ACME",
            name = "Created",
            nodeType = TestNodeType,
            content = new { name = "Widget", price = 1.00m, quantity = 1 }
        }));

        var result = await BoundedAsync(call, nameof(plugin.Create));
        result.Should().StartWith("Created:");
    }

    [Fact(Timeout = 30_000)]
    public async Task Update_ReturnsWithinBudget()
    {
        var plugin = CreatePlugin();
        var id = $"update-{Guid.NewGuid():N}";
        var path = await SeedAsync(plugin, id);

        var call = plugin.Update(JsonSerializer.Serialize(new object[] { new
        {
            id,
            @namespace = "ACME",
            name = "Updated",
            nodeType = TestNodeType,
            content = new { name = "Widget Deluxe", price = 2.00m, quantity = 5 }
        }}));

        var result = await BoundedAsync(call, nameof(plugin.Update));
        result.Should().NotContain("Error");
    }

    [Fact(Timeout = 30_000)]
    public async Task Patch_ReturnsWithinBudget()
    {
        var plugin = CreatePlugin();
        var id = $"patch-{Guid.NewGuid():N}";
        var path = await SeedAsync(plugin, id);

        var call = plugin.Patch($"@{path}", "{\"name\":\"Patched\"}");

        var result = await BoundedAsync(call, nameof(plugin.Patch));
        result.Should().StartWith("Patched:");
    }

    [Fact(Timeout = 30_000)]
    public async Task Delete_ReturnsWithinBudget()
    {
        var plugin = CreatePlugin();
        var id = $"delete-{Guid.NewGuid():N}";
        var path = await SeedAsync(plugin, id);

        var call = plugin.Delete(JsonSerializer.Serialize(new[] { path }));

        var result = await BoundedAsync(call, nameof(plugin.Delete));
        result.Should().NotContain("Error");
    }

    [Fact(Timeout = 30_000)]
    public async Task Move_ReturnsWithinBudget()
    {
        var plugin = CreatePlugin();
        var id = $"move-{Guid.NewGuid():N}";
        var path = await SeedAsync(plugin, id);

        // Move into a sub-namespace. Budget holds even if the move ultimately
        // fails on validation; we're only asserting the method returns.
        var call = plugin.Move($"@{path}", $"@ACME/moved/{id}");
        var result = await BoundedAsync(call, nameof(plugin.Move));
        result.Should().NotBeNull();
    }

    [Fact(Timeout = 30_000)]
    public async Task Copy_ReturnsWithinBudget()
    {
        var plugin = CreatePlugin();
        var id = $"copy-{Guid.NewGuid():N}";
        var path = await SeedAsync(plugin, id);

        var call = plugin.Copy($"@{path}", "@ACME/copies");
        var result = await BoundedAsync(call, nameof(plugin.Copy));
        result.Should().NotBeNull();
    }

    [Fact(Timeout = 30_000)]
    public async Task GetDiagnostics_ReturnsWithinBudget()
    {
        var plugin = CreatePlugin();
        // Non-existent path is valid input — we're testing the timing, not the result.
        var call = plugin.GetDiagnostics($"@ACME/doesnotexist-{Guid.NewGuid():N}");
        var result = await BoundedAsync(call, nameof(plugin.GetDiagnostics));
        result.Should().NotBeNull();
    }

    [Fact(Timeout = 30_000)]
    public async Task Recycle_ReturnsWithinBudget()
    {
        var plugin = CreatePlugin();
        var id = $"recycle-{Guid.NewGuid():N}";
        var path = await SeedAsync(plugin, id);

        var call = plugin.Recycle($"@{path}");
        var result = await BoundedAsync(call, nameof(plugin.Recycle));
        result.Should().NotBeNull();
    }

    /// <summary>
    /// Minimal IAgentChat stub — MeshPlugin only reads ExecutionContext + Context,
    /// so we can return nulls without breaking anything. Duplicated locally instead
    /// of shared across tests so each test file is self-contained.
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
