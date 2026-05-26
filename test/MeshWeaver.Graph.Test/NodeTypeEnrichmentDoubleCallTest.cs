using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Repro for the prod outage where instance hubs of dynamic NodeTypes never
/// reach <c>_hubReady</c> in time. Trace from App Insights:
///
/// <code>
/// 06:33:20.7624  [ACTIVATE] Grain Systemorph/Events activating
/// 06:33:20.7626  [ACTIVATE] Grain Systemorph/Events: no static node with HubConfig, reading from catalog
/// 06:33:50.7665  [ACTIVATE] Grain Systemorph/Events: source emitted node=...
/// </code>
///
/// 30 s from "activating" to "source emitted" — the catalog already ran
/// <see cref="NodeTypeEnrichmentHelpers.EnrichWithNodeType"/> via
/// <c>INodeConfigurationResolver</c> and hit the slow-path 30 s timeout
/// (compile never settled), then returned a node carrying the
/// <see cref="NodeTypeEnrichmentHelpers.WithCompilationErrorOverlay"/>
/// HubConfiguration but <b>no</b> <c>AssemblyLocation</c>.
///
/// <para>The pre-enriched node is then handed to
/// <see cref="MeshNodeHubFactory.ResolveHubConfiguration"/>, which calls
/// <see cref="NodeTypeEnrichmentHelpers.EnrichWithNodeType"/> again. The
/// fast-path at line 39 requires <em>both</em> <c>HubConfiguration</c> and
/// <c>AssemblyLocation</c>, so the second call drops to the slow path and
/// waits another 30 s. By the time the second timeout fires, the calling
/// Orleans grain has long since broken its <c>WaitAsync(30s)</c> at
/// <c>MessageHubGrain.cs:248</c>.</para>
///
/// <para>The fix: short-circuit the fast path when the node already carries a
/// <c>HubConfiguration</c> — re-enriching an already-enriched node cannot
/// produce a different result within the same slow-path window.</para>
/// </summary>
public class NodeTypeEnrichmentDoubleCallTest
{
    /// <summary>
    /// Slow-path timeout inside <see cref="NodeTypeEnrichmentHelpers"/>. Mirrored
    /// here so the assertion budgets stay in lock-step with the production
    /// constant.
    /// </summary>
    private static readonly TimeSpan SlowPathTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// What we tolerate per single slow-path invocation: the timeout itself plus
    /// observation slack. Kept tight so a regression shows up immediately rather
    /// than as a "slow test".
    /// </summary>
    private static readonly TimeSpan SingleSlowPathBudget = SlowPathTimeout + TimeSpan.FromSeconds(5);

    /// <summary>
    /// Stand-in <see cref="IMeshNodeStreamCache"/> whose <see cref="GetStream"/>
    /// returns <see cref="Observable.Never{T}"/> — a NodeType MeshNode that
    /// never lands a settled compile state, exactly the prod symptom.
    /// </summary>
    private sealed class HangingStreamCache : IMeshNodeStreamCache
    {
        public int GetStreamCalls { get; private set; }
        public IObservable<MeshNode> GetStream(string path)
        {
            GetStreamCalls++;
            return Observable.Never<MeshNode>();
        }
        public IObservable<MeshNode> Update(string path, Func<MeshNode, MeshNode> update)
            => Observable.Never<MeshNode>();
        public IObservable<MeshNode> GetStream(string path, System.Text.Json.JsonSerializerOptions options)
            => GetStream(path);
        public IObservable<MeshNode> Update(string path, Func<MeshNode, MeshNode> update, System.Text.Json.JsonSerializerOptions options)
            => Update(path, update);
        public void Invalidate(string path) { }
    }

    private static (IMessageHub Hub, HangingStreamCache Cache) BuildMeshHub()
    {
        var cache = new HangingStreamCache();
        var services = new ServiceCollection()
            .AddSingleton<IMeshNodeStreamCache>(cache)
            .BuildServiceProvider();
        var hub = Substitute.For<IMessageHub>();
        hub.ServiceProvider.Returns(services);
        return (hub, cache);
    }

    private static MeshConfiguration EmptyMeshConfiguration() =>
        new(Array.Empty<MeshNode>());

    /// <summary>
    /// Prod-shape repro. A dynamic NodeType whose compile never settles drives
    /// the slow path; the catalog enriches the instance once (returns an
    /// already-enriched node with a compilation-error overlay HubConfiguration
    /// but no AssemblyLocation), then the per-grain factory enriches it a
    /// second time. Total wall time MUST stay within a single slow-path window
    /// — a second 30 s timeout pushes the activation past the
    /// <c>MessageHubGrain.DeliverMessage</c> WaitAsync(30 s) and looks like a
    /// dead grain to every caller (which is exactly the prod symptom).
    /// </summary>
    [Fact(Timeout = 90_000)]
    public async Task DoubleEnrichment_StaysWithinOneSlowPathTimeout()
    {
        var (hub, _) = BuildMeshHub();
        var cfg = EmptyMeshConfiguration();
        var ct = new CancellationTokenSource(TimeSpan.FromSeconds(80)).Token;

        var bareInstance = new MeshNode("Events", "Systemorph")
        {
            NodeType = "Systemorph/EventCalendar",
        };

        var sw = Stopwatch.StartNew();

        // Pass 1 — catalog-side enrichment via INodeConfigurationResolver. Slow
        // path fires (custom NodeType, no static fast path), times out at 30 s,
        // returns the compilation-error overlay node.
        var afterCatalog = await NodeTypeEnrichmentHelpers
            .EnrichWithNodeType(hub, cfg, compilationService: null, bareInstance)
            .Take(1)
            .ToTask(ct);

        var afterPass1 = sw.Elapsed;
        afterCatalog.Should().NotBeNull("the slow path must always emit — fall back to overlay");
        afterCatalog.HubConfiguration.Should().NotBeNull(
            "WithCompilationErrorOverlay always sets HubConfiguration so callers can build the hub");

        // Pass 2 — MessageHubGrain hands the catalog-enriched node back into
        // ResolveHubConfigurationObservable → EnrichWithNodeType. The bug: the
        // line-39 fast path requires AssemblyLocation, the overlay sets none,
        // and the slow path runs a SECOND 30 s window.
        await NodeTypeEnrichmentHelpers
            .EnrichWithNodeType(hub, cfg, compilationService: null, afterCatalog)
            .Take(1)
            .ToTask(ct);

        sw.Stop();

        sw.Elapsed.Should().BeLessThan(
            SingleSlowPathBudget,
            "double enrichment must not double the wall time — re-enriching an already-enriched node " +
            $"is what makes the prod grain miss its DeliverMessage WaitAsync({SlowPathTimeout.TotalSeconds:0}s) deadline. " +
            $"Pass 1 alone took {afterPass1.TotalSeconds:0.0}s.");
    }

    /// <summary>
    /// Direct probe of the fast-path semantic the prod fix turns on: an
    /// already-enriched node (HubConfiguration set, AssemblyLocation null —
    /// the WithCompilationErrorOverlay shape) re-entered into
    /// <see cref="NodeTypeEnrichmentHelpers.EnrichWithNodeType"/> must NOT
    /// touch the stream cache. If it does, a hung NodeType compile turns every
    /// downstream re-enrichment into a fresh 30 s wait — the bug.
    /// </summary>
    [Fact(Timeout = 10_000)]
    public async Task PreEnrichedNode_DoesNotReEnterSlowPath()
    {
        var (hub, cache) = BuildMeshHub();
        var cfg = EmptyMeshConfiguration();

        var preEnriched = new MeshNode("Events", "Systemorph")
        {
            NodeType = "Systemorph/EventCalendar",
            // The shape WithCompilationErrorOverlay produces: HubConfiguration set
            // (so the caller can instantiate a hub), but no NodeTypeDefinition
            // Content because no DLL was actually emitted.
            HubConfiguration = c => c,
        };

        var sw = Stopwatch.StartNew();
        var result = await NodeTypeEnrichmentHelpers
            .EnrichWithNodeType(hub, cfg, compilationService: null, preEnriched)
            .Take(1)
            .ToTask(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        sw.Stop();

        sw.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(500),
            "an already-enriched node must short-circuit synchronously — anything else " +
            "means EnrichWithNodeType is going to redo work whose answer can't change inside " +
            "this slow-path window, the very pattern that caused the prod 60 s activation hang.");
        result.HubConfiguration.Should().NotBeNull(
            "the fast path must preserve the HubConfiguration the caller already resolved");
        cache.GetStreamCalls.Should().Be(0,
            "the slow path must not be entered for a node that already carries a HubConfiguration");
    }
}
