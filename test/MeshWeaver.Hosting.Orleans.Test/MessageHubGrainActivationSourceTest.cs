using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using MeshWeaver.Mesh;
using Microsoft.Reactive.Testing;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Pins the activation-source composition of <see cref="MessageHubGrain"/>: the mesh-node-cache
/// branch reads THIS grain's OWN address, so its upstream <c>SubscribeRequest</c> is routed back
/// to this grain and parks until the activation it feeds has completed. It may hand over a warm
/// VALUE; its TERMINAL says nothing about the node and must never fault the activation.
///
/// <para>The prod defect (memex.systemorph.com, 2026-08-09/10): <c>Observable.Merge</c> forwarded
/// that branch's terminal, so the cache hub's 60 s request budget became a hard ceiling on TOTAL
/// activation and surfaced as <c>"[ACTIVATE] Grain Edu: activation faulted"</c> /
/// <c>"No response received in hub cache/… for request SubscribeRequest → target Edu"</c>. Every
/// faulting grain was a <c>nodeType:Store/Plugin</c> node whose NodeType sat at
/// <c>CompilationStatus = Error</c>, i.e. exactly the case where enrichment legitimately runs long:
/// <c>NodeTypeEnrichmentHelpers.WaitForCompileSettled</c> DISARMS its wall clock while a compile is
/// in flight, and <c>BuildEnrichmentChain</c> falls back to a visible compilation-error overlay so
/// the hub activates anyway. The self-loop pre-empted both, every time, and Orleans retried
/// forever.</para>
///
/// <para>Virtual time throughout — the 60 s budget is driven by a <see cref="TestScheduler"/>, not
/// the wall clock, so the repro is deterministic and instant.</para>
/// </summary>
public class MessageHubGrainActivationSourceTest
{
    private static readonly TimeSpan FirstNodeBudget = TimeSpan.FromSeconds(30);

    /// <summary>The verbatim prod failure the cache branch injects at its 60 s request budget.</summary>
    private static TimeoutException OwnAddressSubscribeTimeout() => new(
        "No response received in hub cache/tYwK_RNWZUSCQGvEPNvvzg within 00:01:00 for request " +
        "SubscribeRequest (id=Hxehbnok-kG7064Sy_Ga9g) → target Edu. The request may have been " +
        "undeliverable or the target hub was not found.");

    private static MeshNode Node(string path, string? nodeType = null) =>
        new(path) { NodeType = nodeType };

    /// <summary>
    /// Asserts the activation did not fault, and NAMES the exception when it did — a bare
    /// <c>Assert.Empty</c> reports only "Collection was not empty", which hides the very signature
    /// this suite exists to pin.
    /// </summary>
    private static void AssertActivationDidNotFault(List<Exception> errors) =>
        Assert.True(errors.Count == 0,
            errors.Count == 0
                ? string.Empty
                : $"Activation faulted, but no branch of it had anything to fault ON. " +
                  $"{errors[0].GetType().Name}: {errors[0].Message}");

    /// <summary>
    /// Runs the grain's real activation chain in virtual time and reports what the subscriber saw.
    /// Mirrors <c>OnActivateAsync</c>: compose the source, bound the first emission, enrich, Take(1).
    /// </summary>
    private static (List<MeshNode> Nodes, List<Exception> Errors, int Completions) RunActivation(
        TestScheduler scheduler,
        IObservable<MeshNode> pathResolverStream,
        IObservable<MeshNode> ownAddressCacheStream,
        Func<MeshNode, IObservable<MeshNode>> enrich,
        List<Exception>? loggedCacheFaults = null)
    {
        var nodes = new List<MeshNode>();
        var errors = new List<Exception>();
        var completions = 0;

        var source = MessageHubGrain.ComposeActivationSource(
            pathResolverStream,
            ownAddressCacheStream,
            ex => loggedCacheFaults?.Add(ex));

        using var _ = MessageHubGrain
            .BuildActivationChain(source, "Edu", FirstNodeBudget, enrich, scheduler)
            .Subscribe(nodes.Add, errors.Add, () => completions++);

        scheduler.Start();
        return (nodes, errors, completions);
    }

    /// <summary>
    /// 🚨 THE REPRO. The path resolver has already handed over a perfectly good node, and enrichment
    /// is legitimately still running (a NodeType compile in flight — the case
    /// <c>WaitForCompileSettled</c> deliberately does NOT bound). At 60 s the self-referential cache
    /// read times out. The activation must survive that and complete on the enriched node.
    ///
    /// <para>Before the fix (<c>Observable.Merge</c> forwarding the branch's terminal) this test
    /// fails with the TimeoutException surfacing as the activation's error at 60 s — the exact prod
    /// signature.</para>
    /// </summary>
    [Fact]
    public void OwnAddressCacheTimeout_DoesNotFaultActivation_WhileEnrichmentIsStillRunning()
    {
        var scheduler = new TestScheduler();
        var resolved = Node("Edu", "Store/Plugin");
        var enriched = Node("Edu", "Store/Plugin");
        var loggedFaults = new List<Exception>();

        // Path resolver answers at 2 s and completes. Cache self-loop errors at its 60 s request
        // budget. Enrichment (compile in flight) settles only at 90 s — past the cache's budget.
        var (nodes, errors, completions) = RunActivation(
            scheduler,
            pathResolverStream: Observable.Return(resolved)
                .Delay(TimeSpan.FromSeconds(2), scheduler),
            ownAddressCacheStream: Observable.Throw<MeshNode>(OwnAddressSubscribeTimeout())
                .Delay(TimeSpan.FromSeconds(60), scheduler),
            enrich: _ => Observable.Return(enriched).Delay(TimeSpan.FromSeconds(90), scheduler),
            loggedCacheFaults: loggedFaults);

        AssertActivationDidNotFault(errors);
        Assert.Same(enriched, Assert.Single(nodes));
        Assert.Equal(1, completions);

        // Not swallowed: the operator still gets the exception, in full.
        Assert.Contains("SubscribeRequest", Assert.Single(loggedFaults).Message);
    }

    /// <summary>
    /// The instant-replay variant of the same defect. Once the first activation faulted, the cache's
    /// transient-streak breaker replays the cached TimeoutException to the NEXT subscriber without
    /// re-opening an upstream — so every Orleans retry faulted in milliseconds (the 0 s/1 s/2 s/3 s
    /// burst measured at 03:57Z). A branch that fails before the path resolver has even answered
    /// must still not decide the activation.
    /// </summary>
    [Fact]
    public void OwnAddressCacheReplaysCachedFault_ActivationStillResolvesFromPathResolver()
    {
        var scheduler = new TestScheduler();
        var resolved = Node("Edu", "Store/Plugin");

        var (nodes, errors, _) = RunActivation(
            scheduler,
            // Path resolver is slower than the breaker's instant replay.
            pathResolverStream: Observable.Return(resolved).Delay(TimeSpan.FromSeconds(5), scheduler),
            ownAddressCacheStream: Observable.Throw<MeshNode>(OwnAddressSubscribeTimeout()),
            enrich: Observable.Return);

        AssertActivationDidNotFault(errors);
        Assert.Same(resolved, Assert.Single(nodes));
    }

    /// <summary>
    /// The accelerator still works: a warm cache entry that replays the node before the path
    /// resolver reaches storage drives the activation, so a reactivation skips the storage read.
    /// </summary>
    [Fact]
    public void WarmOwnAddressCacheValue_DrivesActivation()
    {
        var scheduler = new TestScheduler();
        var warm = Node("Edu", "Store/Plugin");
        var fromStorage = Node("Edu", "Store/Plugin");

        var (nodes, errors, _) = RunActivation(
            scheduler,
            pathResolverStream: Observable.Return(fromStorage).Delay(TimeSpan.FromSeconds(3), scheduler),
            ownAddressCacheStream: Observable.Return(warm),
            enrich: Observable.Return);

        AssertActivationDidNotFault(errors);
        Assert.Same(warm, Assert.Single(nodes));
    }

    /// <summary>
    /// A genuinely missing node must still be reported PROMPTLY. Collapsing the faulted cache branch
    /// to <c>Empty</c> (not <c>Never</c>) lets the merged source complete as soon as both branches
    /// are done, so <c>OnActivateAsync</c>'s "source completed with no usable node" handler fires at
    /// once instead of waiting out the 30 s first-emission budget.
    /// </summary>
    [Fact]
    public void NoNodeAnywhere_CompletesImmediately_RatherThanBurningTheBudget()
    {
        var scheduler = new TestScheduler();

        var (nodes, errors, completions) = RunActivation(
            scheduler,
            pathResolverStream: Observable.Empty<MeshNode>(),
            ownAddressCacheStream: Observable.Throw<MeshNode>(OwnAddressSubscribeTimeout()),
            enrich: Observable.Return);

        Assert.Empty(nodes);
        AssertActivationDidNotFault(errors);
        Assert.Equal(1, completions);
    }

    /// <summary>
    /// Guard against over-reach: only the SELF-REFERENTIAL branch is neutralised. The path resolver
    /// reads storage — its failure is real news about the node and must still fault the activation
    /// (the caller gets a deterministic NACK and the grain deactivates for retry-on-next-access).
    /// </summary>
    [Fact]
    public void PathResolverFailure_StillFaultsActivation()
    {
        var scheduler = new TestScheduler();
        var storageFailure = new InvalidOperationException("storage adapter unavailable");

        var (nodes, errors, _) = RunActivation(
            scheduler,
            pathResolverStream: Observable.Throw<MeshNode>(storageFailure),
            ownAddressCacheStream: Observable.Never<MeshNode>(),
            enrich: Observable.Return);

        Assert.Empty(nodes);
        Assert.Same(storageFailure, Assert.Single(errors));
    }

    /// <summary>
    /// The first-emission budget is intact: when NEITHER branch produces a node the activation still
    /// faults with the precise, actionable diagnostic — never a silent park.
    /// </summary>
    [Fact]
    public void NeitherBranchEmits_FaultsWithTheFirstNodeResolutionDiagnostic()
    {
        var scheduler = new TestScheduler();

        var (nodes, errors, _) = RunActivation(
            scheduler,
            pathResolverStream: Observable.Never<MeshNode>(),
            ownAddressCacheStream: Observable.Never<MeshNode>(),
            enrich: Observable.Return);

        Assert.Empty(nodes);
        var error = Assert.Single(errors);
        Assert.IsType<TimeoutException>(error);
        Assert.Contains("No MeshNode emitted for 'Edu'", error.Message);
    }

    /// <summary>
    /// And the budget bounds ONLY the first emission — once a node arrives, Amb commits to the
    /// source and a legitimately slow enrichment (cold compile) runs past the budget unharmed.
    /// </summary>
    [Fact]
    public void SlowEnrichment_IsNotCutShortByTheFirstNodeBudget()
    {
        var scheduler = new TestScheduler();
        var enriched = Node("Edu", "Store/Plugin");

        var (nodes, errors, _) = RunActivation(
            scheduler,
            pathResolverStream: Observable.Return(Node("Edu", "Store/Plugin"))
                .Delay(TimeSpan.FromSeconds(1), scheduler),
            ownAddressCacheStream: Observable.Never<MeshNode>(),
            enrich: _ => Observable.Return(enriched).Delay(TimeSpan.FromMinutes(4), scheduler));

        AssertActivationDidNotFault(errors);
        Assert.Same(enriched, Assert.Single(nodes));
    }
}
