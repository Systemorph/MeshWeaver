using System.Linq;
using System.Reactive.Linq;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Pins that an Agent node CREATED AFTER the roster is already being watched reaches that roster
/// through the live query path — the companion to <see cref="AgentRosterContentShapeTest"/> for
/// issue #1853.
///
/// <para>🚨 <b>This test PASSES against the unfixed code, deliberately.</b> It is the experiment
/// that disproved the issue's stated diagnosis, kept as a regression pin rather than thrown away.
/// #1853 was reported as a cache-invalidation defect — "the synced agent cache reacts to the
/// recycle broadcast but not to node CREATE" — and that is not what was happening. A create DOES
/// publish a change-feed event, the relevance gate DOES admit it, the query DOES re-run, and the
/// new node IS in the snapshot. This test demonstrates all of that. The agent went missing one
/// step later, in the projection, which <see cref="AgentRosterContentShapeTest"/> covers and which
/// does fail against the unfixed code.</para>
///
/// <para>Every pre-existing agent-roster test seeds the agent BEFORE subscribing, so none of them
/// exercises a live create at all. That gap is worth closing on its own merits: the roster stream
/// is cached for the process lifetime (<c>MeshNodeStreamCache._queries</c>,
/// <c>Replay(1).AutoConnect(1)</c>, evicted only on a terminal error), so if a live delta ever did
/// stop arriving, the roster would freeze at whatever the first subscriber saw and no existing test
/// would notice.</para>
///
/// <para>The agent query's distinguishing shape is a NAMESPACE ALTERNATION with no <c>path:</c>
/// term — <c>namespace:{user}/Agent|{space}/Agent|Agent nodeType:Agent</c> — which the parser
/// resolves to a single <c>namespace IN (…)</c> filter. Pinning the live behaviour of that
/// particular shape is the point.</para>
/// </summary>
public class AgentRosterLiveCreateTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder).AddAI();

    private const string UserPath = "rbuergi";

    private static MeshNode VoiceAgent() =>
        MeshNode.FromPath($"{UserPath}/{AgentPickerProjection.AgentSubNamespace}/Voice") with
        {
            Name = "Voice",
            NodeType = AgentNodeType.NodeType,
            Content = new AgentConfiguration
            {
                Id = "Voice",
                Description = "A voice agent created while the roster was already being watched.",
                Instructions = "Speak.",
            }
        };

    /// <summary>
    /// Subscribe to the agent registry query, THEN create the agent, and require it to show up
    /// without any recycle / restart. Bounded wait — a hang here is the bug, not slowness.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task AgentCreatedAfterSubscription_ReachesTheRoster_WithoutARecycle()
    {
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var query = AgentPickerProjection.BuildAgentQuery(userPath: UserPath);

        // Hot + replayed so the assertion below sees every event, not just those after it attaches.
        var roster = mesh.Query<MeshNode>(MeshQueryRequest.FromQuery(query)).Replay();
        using var connection = roster.Connect();

        // The roster is now live and Voice is NOT in it — this is the state the portal sits in.
        var initial = await roster.Should().Within(30.Seconds())
            .Match(c => c.ChangeType == QueryChangeType.Initial);
        initial.Items.Should().NotContain(n => n.Id == "Voice",
            "the agent has not been created yet — if it is already here the test is not " +
            "exercising a LIVE create and would pass against the broken code");

        await mesh.CreateNode(VoiceAgent()).Should().Emit();

        await roster.Should().Within(60.Seconds())
            .Match(c => c.Items.Any(n => n.Path == $"{UserPath}/{AgentPickerProjection.AgentSubNamespace}/Voice"));
    }

    /// <summary>
    /// The control: created BEFORE the subscription, the agent is in the Initial snapshot. This is
    /// the shape every pre-existing roster test has. Kept so a future reader can see that both
    /// orderings are covered rather than only the convenient one.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task AgentCreatedBeforeSubscription_IsInTheInitialSnapshot()
    {
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        await mesh.CreateNode(VoiceAgent()).Should().Emit();

        var query = AgentPickerProjection.BuildAgentQuery(userPath: UserPath);
        await mesh.Query<MeshNode>(MeshQueryRequest.FromQuery(query))
            .Should().Within(30.Seconds())
            .Match(c => c.Items.Any(n => n.Path == $"{UserPath}/{AgentPickerProjection.AgentSubNamespace}/Voice"));
    }
}
