#pragma warning disable CS1591

using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Pins the chat composer's combobox-switch flow against the silo — switching the
/// harness/agent/model in the chat input persists onto the <see cref="ThreadComposer"/> node
/// through <see cref="IMeshNodeStreamCache.Update(string, Func{MeshNode, MeshNode}, System.Text.Json.JsonSerializerOptions)"/>
/// (the exact path <c>ThreadChatView</c> takes on a selection change).
///
/// <para><b>Why this exists.</b> Switching the model combobox while the composer's ThreadComposer
/// node did not exist at an activatable path took the silo down: the write to the missing
/// owner hub failed at routing, and the WRITE path had no storm-breaker (reads did), so it
/// re-enqueued a doomed <c>PatchDataRequest</c> forever (<c>[UpdateQueue] FAILED seq 16,17,18…</c>)
/// → resubscribe storm → silo death. The invariant: <b>a combobox switch must persist when the
/// node exists, and FAIL FAST without storming/wedging when it doesn't.</b></para>
/// </summary>
public class OrleansThreadComposerComboboxTest : IClassFixture<TwoSiloCacheUpdateFixture>
{
    private readonly TwoSiloCacheUpdateFixture _fixture;

    public OrleansThreadComposerComboboxTest(TwoSiloCacheUpdateFixture fixture) => _fixture = fixture;

    /// <summary>
    /// Switching the harness/agent/model combobox persists onto the ThreadComposer node and reads
    /// back — the happy path of a selection change. Mirrors <c>ThreadChatView.PersistSelection
    /// → WriteTemplate → cache.Update(chatInputPath, …)</c>.
    /// </summary>
    [Fact]
    public async Task SwitchCombobox_OnExistingThreadComposer_PersistsAndIsReadable()
    {
        var ct = new CancellationTokenSource(45.Seconds()).Token;
        var siloHub = _fixture.PrimarySiloMeshHub;
        var user = $"chatuser-{Guid.NewGuid():N}";
        // Per-(node,user) composer shape: {node}/_Thread/{user}/ThreadComposer — the activatable
        // cell-like form (owner segment present), NOT the bare {node}/_Thread/ThreadComposer.
        var ns = $"{user}/_Thread/{user}";
        var chatInputPath = $"{ns}/ThreadComposer";

        // 1. Create the ThreadComposer composer node.
        var createResp = await siloHub.Observe(
                new CreateNodeRequest(new MeshNode("ThreadComposer", ns)
                {
                    NodeType = ThreadComposerNodeType.NodeType,
                    Name = "Chat Input",
                    State = MeshNodeState.Active,
                    Content = new ThreadComposer(),
                }),
                o => o.WithTarget(siloHub.Address))
            .FirstAsync().ToTask(ct);
        createResp.Message.Success.Should().BeTrue(createResp.Message.Error ?? "");
        createResp.Message.Node!.Path.Should().Be(chatInputPath);

        // 2. Switch the combobox — persist harness/agent/model via cache.Update (the selection-
        //    change write). Caller options carry the ThreadComposer $type (AddAI on both silos).
        var cache = siloHub.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();
        await cache.Update(chatInputPath, node => node with
            {
                Content = (node.Content as ThreadComposer ?? new ThreadComposer()) with
                {
                    Harness = "MeshWeaver",
                    AgentName = "Assistant",
                    ModelName = "claude-sonnet-4-6",
                }
            }, siloHub.JsonSerializerOptions)
            .Take(1).Timeout(30.Seconds()).ToTask(ct);

        // 3. Read back through the authoritative single-node primitive — selection persisted.
        var workspace = siloHub.GetWorkspace();
        var persisted = await Observable
            .Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => workspace.GetMeshNodeStream(chatInputPath).Take(1))
            .Where(n => (n?.Content as ThreadComposer)?.ModelName == "claude-sonnet-4-6")
            .FirstAsync().Timeout(20.Seconds()).ToTask(ct);

        var cfg = persisted!.Content.Should().BeOfType<ThreadComposer>().Subject;
        cfg.ModelName.Should().Be("claude-sonnet-4-6", "the combobox model selection must persist");
        cfg.AgentName.Should().Be("Assistant");
        cfg.Harness.Should().Be("MeshWeaver");
    }

    /// <summary>
    /// Switching the combobox while the composer's ThreadComposer node does NOT exist must FAIL FAST
    /// for every repeat and leave the mesh responsive — never the resubscribe-storm that killed
    /// the silo. The second write proves the write-side storm-breaker engaged (fast-fail via the
    /// negative cache, no re-enqueue). Every op is bounded with a timeout, so a storm/wedge
    /// surfaces as a deterministic <see cref="TimeoutException"/> (RED) rather than a freeze.
    /// </summary>
    [Fact]
    public async Task SwitchCombobox_OnMissingThreadComposer_FastFails_DoesNotStormOrWedge()
    {
        var ct = new CancellationTokenSource(60.Seconds()).Token;
        var siloHub = _fixture.PrimarySiloMeshHub;
        var user = $"chatuser-{Guid.NewGuid():N}";

        // Provision the partition with a real node (baseline; routing resolves the ancestor).
        var probe = await siloHub.Observe(
                new CreateNodeRequest(new MeshNode("probe", user)
                    { Name = "Probe", NodeType = "Markdown", State = MeshNodeState.Active }),
                o => o.WithTarget(siloHub.Address))
            .FirstAsync().ToTask(ct);
        probe.Message.Success.Should().BeTrue(probe.Message.Error ?? "");

        // The missing composer — the exact crash shape: the partition ancestor resolves, the
        // leaf doesn't exist, so the owner hub at this address can't activate.
        var missingThreadComposer = $"{user}/_Thread/ThreadComposer";
        var cache = siloHub.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();

        Func<int, Func<Task>> switchCombobox = boundSeconds => () => cache.Update(missingThreadComposer, node => node with
            {
                Content = (node.Content as ThreadComposer ?? new ThreadComposer()) with { ModelName = "claude-haiku-4-5" }
            }, siloHub.JsonSerializerOptions)
            .Take(1).Timeout(boundSeconds.Seconds()).ToTask();

        // 1. First switch against the missing node: a proper exception FAST, never a storm/hang.
        var first = await switchCombobox(30).Should().ThrowAsync<Exception>(
            "a write to a non-existent ThreadComposer owner must surface a proper exception, not storm");
        first.Which.Should().NotBeOfType<TimeoutException>(
            "a TimeoutException means the write HUNG/STORMED past 30s — the silo-killing defect");

        // 2. Second switch (negative-cache window now open): MUST fast-fail — the storm-breaker
        //    throws the cached failure WITHOUT re-enqueueing a doomed PatchDataRequest. Tight 5s
        //    bound: if the breaker is broken the write re-enqueues and trips the bound → Timeout (RED).
        var second = await switchCombobox(5).Should().ThrowAsync<Exception>();
        second.Which.Should().NotBeOfType<TimeoutException>(
            "the write-side storm-breaker must fast-fail the repeat write, not re-enqueue and hang");

        // 3. The mesh must stay RESPONSIVE — a real node create still works promptly. No wedge.
        var after = await siloHub.Observe(
                new CreateNodeRequest(new MeshNode($"probe2-{Guid.NewGuid():N}", user)
                    { Name = "Probe2", NodeType = "Markdown", State = MeshNodeState.Active }),
                o => o.WithTarget(siloHub.Address))
            .FirstAsync().ToTask().WaitAsync(15.Seconds());
        after.Message.Success.Should().BeTrue(
            "the mesh must stay responsive after missing-node combobox writes — no storm/wedge");
    }
}
