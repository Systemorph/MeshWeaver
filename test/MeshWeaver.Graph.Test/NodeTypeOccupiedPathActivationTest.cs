using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>A PROVEN NON-DECLARATION IS A TERMINAL STATE, NOT SOMETHING TO WAIT OUT</b> —
/// Systemorph/MeshWeaver#5008 / #2231, the activation half.
///
/// <para>#2245 taught the existence PROBE to detect the collision (a Store plugin root at the bare
/// path <c>Feedback</c>, whose declaration is <c>Feedback/Feedback</c>) and refuse by name. But the
/// probe is an optimisation, and two routes go round it: an <c>Indeterminate</c> outcome — the 3 s
/// lookup not answering on a busy replica, which is the ordinary state during a post-roll recompile
/// wave — and a host that registers no <c>IMeshQueryCore</c> at all. On those routes the slow path
/// did not wait at all: <c>IsCompileSettled</c>'s first arm admits a node that is not a
/// <c>NodeTypeDefinition</c> "in ANY readable shape" as SETTLED, and <c>ApplyStreamResult</c> then
/// "applies the default config deliberately" — so the instance bound the bare default chain with no
/// type, no areas and no diagnostic, and the only trace of the cause was a bare
/// <c>As&lt;NodeTypeDefinition&gt; for Feedback: value is PluginContent</c> line that a predicate on
/// the way emitted at Error, naming neither the instance nor the reason.</para>
///
/// <para>🚨 <b>This test therefore pins a REVERSAL of a deliberate branch</b>, and the ground is
/// that the other route through the same method already decided the opposite. Measured with the
/// change disabled: the activation settles in ~3 s — the probe's budget, not
/// <c>SlowPathTimeout</c> — with a NULL <c>HubConfiguration</c>. That reading is what this test's
/// first assertion fails on.</para>
///
/// <para>That line is why #2231 could never go quiet: it is the fingerprint's own text, so every
/// activation of an already-stranded instance reopened the ticket. Fixing the write boundary alone
/// would have left it firing forever.</para>
///
/// <para>🚨 <b>This test exists as a SEPARATE class because the monolith cannot reach the route.</b>
/// A <c>MonolithMeshTestBase</c> mesh answers the existence probe, so <c>ProbeCollision</c> catches
/// every collision before the slow path is entered and an assertion there would pass with or
/// without the change — measured: it did. The shape that reaches it is
/// <see cref="NeverAnsweringQueryCore"/>, the same one
/// <c>IndeterminateProbeReResolutionTest</c> uses for the same reason.</para>
/// </summary>
public class NodeTypeOccupiedPathActivationTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string NodeTypePath = "OccupiedSpace/Catalog";
    private const string InstancePath = "OccupiedSpace/Instance";

    /// <summary>
    /// The busy-mesh shape: the <c>path:{nodeType}</c> lookup never answers, so the probe reports
    /// <c>Indeterminate</c> and the chain falls through to the slow path — the route
    /// <c>ProbeCollision</c> does not cover. It counts its calls so the test can prove the probe
    /// really ran; a test that accidentally skipped it would pass vacuously.
    /// </summary>
    private sealed class NeverAnsweringQueryCore : IMeshQueryCore
    {
        private int calls;
        public int Calls => System.Threading.Volatile.Read(ref calls);

        public IObservable<QueryResultChange<T>> Query<T>(
            MeshQueryRequest request, JsonSerializerOptions options)
        {
            System.Threading.Interlocked.Increment(ref calls);
            return Observable.Never<QueryResultChange<T>>();
        }
    }

    /// <summary>Serves the occupant as the NodeType path's stream — what the slow path binds to.</summary>
    private sealed class TypeNodeStreamCache(IObservable<MeshNode> typeNodeStream) : IMeshNodeStreamCache
    {
        public IObservable<MeshNode> GetStream(string path) => typeNodeStream;
        public IObservable<MeshNode> GetStream(string path, JsonSerializerOptions options)
            => GetStream(path);
        public IObservable<MeshNode> Update(string path, Func<MeshNode, MeshNode> update)
            => Observable.Never<MeshNode>();
        public IObservable<MeshNode> Update(string path, Func<MeshNode, MeshNode> update, JsonSerializerOptions options)
            => Update(path, update);
        public IObservable<MeshNode> Overwrite(string path, MeshNode node, JsonSerializerOptions options)
            => Observable.Never<MeshNode>();
        public void Invalidate(string path) { }
        public bool ReleaseIfUnwatched(string path) => false;
        public IObservable<IEnumerable<MeshNode>>? GetQuery(object id) => null;
        public IObservable<IEnumerable<MeshNode>> GetQuery(object id, JsonSerializerOptions options, params string[] queries)
            => Observable.Never<IEnumerable<MeshNode>>();
    }

    /// <summary>
    /// The pin. Probe never answers, so the slow path binds the occupant's own stream; the
    /// activation must end on an overlay that NAMES the occupant, and it must do so instead of
    /// spending the compile budget.
    ///
    /// <para>🚨 No assertion here is a TIMING one. Before the change the activation produced a
    /// null <c>HubConfiguration</c> — the bare default chain, applied in the same ~3 s — so the
    /// outcomes differ in KIND, not in speed. The elapsed time is printed beside the verdict as
    /// context, never as evidence.</para>
    /// </summary>
    [HubFact]
    public async Task AnOccupiedNodeTypePath_EndsOnAnOverlayThatNamesTheOccupant()
    {
        // The occupant: a non-empty NodeType that is not "NodeType", carrying content typed as
        // something else entirely. This is the Store/Plugin root's shape — `PluginContent` in the
        // incident — with a record this hub already knows so the conviction does not depend on a
        // registry the test would have to arrange.
        var occupant = new MeshNode("Catalog", "OccupiedSpace")
        {
            Name = "Catalog",
            NodeType = "Markdown",
            Content = new MarkdownContent { Content = "# a plugin root, not a declaration" },
        };

        var typeNodeStream = Observable.Return(occupant).Replay(1);
        using var _ = typeNodeStream.Connect();

        var probe = new NeverAnsweringQueryCore();
        var meshHub = Mesh.GetHostedHub(new Address("occupiedmesh", "1"), c => c
            .AddData()
            .WithPostingIdentity(PostingIdentity.System)
            .WithServices(services => services
                .AddSingleton<IMeshNodeStreamCache>(new TypeNodeStreamCache(typeNodeStream))
                .AddSingleton<IMeshQueryCore>(probe)));

        var instance = MeshNode.FromPath(InstancePath) with { NodeType = NodeTypePath };

        var started = Stopwatch.StartNew();
        var enriched = await NodeTypeEnrichmentHelpers
            .EnrichWithNodeType(meshHub, new MeshConfiguration(Array.Empty<MeshNode>()),
                compilationService: null, instance)
            .Take(1)
            .Should().Within(25.Seconds()).Emit(
                "the enrichment must reach a verdict — the deadline bounds the test, it is not "
                + "the thing under test");
        started.Stop();
        Output.WriteLine($"activation settled after {started.ElapsedMilliseconds} ms "
                         + $"(SlowPathTimeout is {NodeTypeEnrichmentHelpers.SlowPathTimeout.TotalSeconds:0} s)");

        probe.Calls.Should().BeGreaterThan(0,
            "this test is about the route that BYPASSES ProbeCollision — if the probe never ran, "
            + "it is measuring the probe's branch instead and proves nothing about the slow path");

        enriched.HubConfiguration.Should().NotBeNull(
            "🚨 THIS is the assertion the change moves. Before it, IsCompileSettled admitted the "
            + "occupant as settled ('a plain node at a path used as a type … ApplyStreamResult "
            + "applies the default config deliberately') and the instance came back with NO "
            + "configuration at all — the bare default chain, no type, no areas, and nothing "
            + "anywhere naming the cause. Measured: null, after 3029 ms");

        var applied = enriched.HubConfiguration!(
            new MessageHubConfiguration(null, new Address("node", "occupied-instance")));
        var nack = applied.Get<UnhandledMessageNack>();
        nack.Should().NotBeNull(
            "the overlay NACKs typed requests instead of parking them — that is the "
            + "WithCompilationErrorOverlay contract");
        nack!.Reason.Should().Contain(occupant.Path).And.Contain("Markdown",
            "ONE fact, ONE sentence, whichever route found it: the operator must learn WHICH node "
            + "is in the way and WHAT it is. Before this, the slow path produced no sentence at "
            + "all, and the only trace of the truth was a bare As<NodeTypeDefinition> line naming "
            + "neither instance nor cause");
        nack.Reason.Should().Contain(InstancePath,
            "and which instance is stranded by it");
    }
}
