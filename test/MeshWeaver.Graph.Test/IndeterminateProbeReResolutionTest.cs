using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Regression guard for the memex 2026-08-09 <c>@Store</c> incident: a node whose data was
/// intact answered <c>Unavailable: read reached no verdict within 10s</c>, and its value
/// rendered empty, because the type-registration PROBE lost a race and its INDETERMINATE
/// answer was treated as final.
///
/// <para>The mechanism, end to end:</para>
/// <list type="number">
///   <item>Every 6-hourly self-update roll changes the framework hash, so all ~240 dynamic
///     NodeTypes rebuild sequentially (~10 min of Roslyn on the Compile pool).</item>
///   <item>Under that load the 3 s existence probe in
///     <see cref="NodeTypeEnrichmentHelpers.EnrichWithNodeType"/> does not answer →
///     <c>ProbeOutcome.Indeterminate</c>.</item>
///   <item>Indeterminate used to return a TERMINAL overlay configuration. The NodeType's own
///     configuration — the one that calls <c>WithContentType&lt;T&gt;()</c> — therefore never
///     ran, so the content <c>$type</c> discriminator was registered on NO registry.</item>
///   <item><c>MeshNodeTypeSource.ResolveJsonElementContent</c> then degraded the row's Content
///     to an untyped <see cref="JsonElement"/> — and, because enrichment binds ONCE per grain,
///     it stayed that way long after the type finished compiling (the type's Release existed
///     at 01:10:16, the compile logged green at 01:19:26, the content never re-typed).</item>
/// </list>
///
/// <para>The fix is NOT a longer probe budget — that only moves the race. An unanswered lookup
/// is not a verdict, so it now falls through to the same reactive chain a REGISTERED outcome
/// takes: the NodeType's own mesh-node stream, on which registration/compile completion is an
/// emission. This test pins exactly that: the probe never answers, the type shows up LATE, and
/// the instance must still end up bound to the type's own content-type-registering
/// configuration — i.e. with typed content rather than a permanently untyped JsonElement.</para>
/// </summary>
public class IndeterminateProbeReResolutionTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <summary>
    /// Stand-in for a dynamically compiled NodeType's content type. What matters is that it is
    /// resolvable ONLY if the NodeType's own hub configuration was applied.
    /// </summary>
    private sealed record ProbeLateContent
    {
        public string? Title { get; init; }
    }

    private const string NodeTypePath = "ProbeSpace/Catalog";
    private const string InstancePath = "ProbeSpace/Instance";

    /// <summary>
    /// When the NodeType shows up on its own stream, measured from the START of the test. Past
    /// <c>NodeTypeEnrichmentHelpers.NodeTypeProbeTimeout</c> (3 s), so the registration lookup
    /// has provably already given up when it lands — that is the state under test. The stream is
    /// made HOT (Replay + Connect) so this really is "the type registers 5 s into the run",
    /// independent of when anything subscribes.
    /// </summary>
    private static readonly TimeSpan LateRegistration = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The busy-mesh shape: the <c>path:{nodeType}</c> lookup never answers, so the probe times
    /// out and reports <c>Indeterminate</c>. It counts its calls so the test can prove the probe
    /// really ran (a test that accidentally skipped it would pass vacuously).
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

    /// <summary>
    /// Serves the NodeType's mesh-node stream — the observable on which "the type is registered
    /// now" actually arrives, and therefore the re-resolution signal the fix relies on.
    /// </summary>
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
    /// The pin. Probe never answers (Indeterminate); the NodeType registers 5 s into the run; the
    /// enriched instance must carry the TYPE'S OWN configuration, which is what registers the
    /// content discriminator and therefore what makes the node's content typed.
    ///
    /// <para>Before the fix this fails: the Indeterminate branch returned the overlay
    /// configuration immediately at 3 s and never looked at the NodeType stream again, so
    /// <c>ProbeLateContent</c> was registered nowhere and the row's content stayed an untyped
    /// JsonElement for the grain's whole lifetime.</para>
    /// </summary>
    [HubFact]
    public async Task IndeterminateProbe_ThenLateRegistration_EndsWithTypedContent()
    {
        // The NodeType's REAL configuration: the delegate that registers the content type.
        // Production spells this `c => c.WithContentType<T>()`, which bottoms out in exactly
        // this TypeRegistry.WithType call (MeshDataSource.WithContentType) — minus the
        // data-source plumbing a bare configuration cannot build.
        Func<MessageHubConfiguration, MessageHubConfiguration> typeOwnConfiguration =
            c => c.WithType<ProbeLateContent>(nameof(ProbeLateContent));

        var typeNode = new MeshNode("Catalog", "ProbeSpace")
        {
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition(),
            HubConfiguration = typeOwnConfiguration,
        };

        // HOT: the type registers at LateRegistration from NOW, whether or not anyone is
        // watching — so at the moment the probe gives up (3 s) the type genuinely is not there
        // yet, and it appears afterwards on its own schedule. Replay(1) so the enrichment chain,
        // which only subscribes after the probe has failed, still sees it.
        var typeNodeStream = Observable.Timer(LateRegistration).Select(_ => typeNode).Replay(1);
        using var _ = typeNodeStream.Connect();

        var probe = new NeverAnsweringQueryCore();
        var meshHub = Mesh.GetHostedHub(new Address("probemesh", "1"), c => c
            .AddData()
            .WithPostingIdentity(PostingIdentity.System)
            .WithServices(services => services
                .AddSingleton<IMeshNodeStreamCache>(new TypeNodeStreamCache(typeNodeStream))
                .AddSingleton<IMeshQueryCore>(probe)));

        var instance = MeshNode.FromPath(InstancePath) with { NodeType = NodeTypePath };

        var enriched = await NodeTypeEnrichmentHelpers
            .EnrichWithNodeType(meshHub, new MeshConfiguration(Array.Empty<MeshNode>()),
                compilationService: null, instance)
            .Take(1)
            .Should().Within(20.Seconds()).Emit();

        probe.Calls.Should().BeGreaterThan(0,
            "the existence probe must still run — this test is about what happens when it does " +
            "NOT answer, not about removing it");

        enriched.HubConfiguration.Should().BeSameAs(typeOwnConfiguration,
            "an INDETERMINATE probe is not a verdict: the instance must end up bound to the " +
            "NodeType's OWN configuration once the type shows up on its stream. Binding the " +
            "fallback overlay instead is the one-way degradation — the type's " +
            "WithContentType<T>() never runs, so its content discriminator is registered " +
            "nowhere and the node renders empty even after the type compiles green.");

        // The load-bearing consequence, stated directly: apply the resolved configuration and
        // the content discriminator resolves — which is exactly what
        // MeshNodeTypeSource.ResolveJsonElementContent needs to type the row instead of
        // degrading it to a bare JsonElement.
        var hubConfiguration = enriched.HubConfiguration!(
            new MessageHubConfiguration(null, new Address("node", "probe-instance")));
        hubConfiguration.TypeRegistry.TryGetType(nameof(ProbeLateContent), out var typeDefinition)
            .Should().BeTrue(
                "the content '$type' discriminator must resolve on the instance hub — that is " +
                "the difference between typed content and the untyped JsonElement that renders " +
                "empty and makes reactive waits time out");
        typeDefinition!.Type.Should().Be(typeof(ProbeLateContent));
    }
}
