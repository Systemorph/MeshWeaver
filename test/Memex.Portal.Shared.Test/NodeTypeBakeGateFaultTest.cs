using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Memex.Portal.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>A POD THAT COULD NOT ENUMERATE A SINGLE NodeType MUST NOT REPORT READY.</b>
///
/// <para><b>The defect.</b> <c>DynamicTypePreWarmer.WarmDynamicTypes</c> caught an enumeration
/// failure and returned <c>Observable.Empty</c>, and
/// <c>DynamicTypePreWarmerHostedService</c>'s error handler then called
/// <c>gate.MarkComplete("warm-up stream faulted — gate released")</c>. So a pod whose enumeration
/// threw arrived at <see cref="BakePhase.Complete"/> → <c>Healthy</c> and took traffic having
/// verified nothing. The chain it satisfied is real and armed in production
/// (<c>PreWarm__GateReadiness=true</c> in <c>deploy/aks/values.aks.yaml</c> →
/// <see cref="NodeTypeBakeHealthCheck"/> → <c>/health</c> as the <c>startupProbe</c> →
/// <c>maxSurge:1 / maxUnavailable:0</c>), which made this the one way a pod could pass a gate that
/// otherwise works.</para>
///
/// <para><b>Why it is a defect and not the documented policy.</b> The policy on
/// <see cref="NodeTypeBakeHealthCheck"/> reads <i>"fail CLOSED on a regression, fail OPEN on 'not
/// running'"</i> — and every clause of it is scoped to <see cref="BakePhase.NotStarted"/>, i.e.
/// the sweep is DISABLED ("if this check is registered while the sweep is not enabled, that is a
/// configuration mistake"). The same switch already reports <see cref="BakePhase.Running"/> —
/// a pod that has verified strictly MORE than a faulted one — as Unhealthy, so "fail open on
/// anything unproven" was never the rule. The retired pre-run bake Job carried the counterpart
/// guard explicitly (<i>"FINDING NOTHING IS NOT PASSING … a gate that certifies 'I verified
/// nothing' is worse than no gate"</i>, exit 3, escape hatch <c>Bake:AllowEmpty</c>) and named THIS
/// <c>Catch</c> as the reason it had to; #1357 retired the Job without porting the guard.</para>
///
/// <para><b>The pair below is the whole deliverable</b> — the two states that must stay
/// distinguishable, asserted through the real sweep, the real hosted service, the real gate and
/// the real health check, i.e. literally "would Kubernetes route traffic to this pod?":</para>
/// <list type="number">
///   <item><b>Zero types because enumeration THREW</b> ⇒ NOT ready. The pod learned nothing.</item>
///   <item><b>Zero types because none exist</b> ⇒ READY. Emptiness is a legitimate answer, and
///     refusing readiness for it would black-hole a fresh or genuinely empty mesh. This is the half
///     that keeps the fix from reversing the documented policy — the retired Job DID gate on
///     emptiness, and porting that rule verbatim is what would have been wrong.</item>
/// </list>
/// </summary>
public class NodeTypeBakeGateFaultTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder)
            .ConfigureServices(services => services
                // Mesh-scoped singleton — no static state; it dies with the mesh.
                .AddSingleton<BlindEnumerationQueryProvider>()
                .AddSingleton<IMeshQueryProvider>(sp =>
                    sp.GetRequiredService<BlindEnumerationQueryProvider>()));

    /// <summary>
    /// 🚨 FAIL-BEFORE / PASS-AFTER, HALF ONE: the enumeration throws, and the pod must NOT be ready.
    ///
    /// <para>Before the fix this test fails at the last assertion and only there — which is exactly
    /// what made the defect invisible: the sweep "completes", the summary line prints, the gate
    /// reports <c>Complete</c>, the health check returns <c>Healthy</c>, and every observable signal
    /// says the bake passed.</para>
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task EnumerationThrows_TheBakeIsNotProven_AndThePodRefusesReadiness()
    {
        Mesh.ServiceProvider.GetRequiredService<BlindEnumerationQueryProvider>().Blind = true;

        var (gate, settlement) = await RunTheRealPreWarmHostedService();

        // 1. The SOURCE distinction. A faulted enumeration must reach the subscriber AS a fault —
        //    this is the link the old `.Catch(… => Observable.Empty)` broke, and every downstream
        //    assertion here is only reachable because it now propagates.
        settlement.Should().Be(PreWarmSettlement.Faulted,
            "a sweep whose enumeration threw must settle as Faulted — laundering it into an empty "
            + "completion is what let a pod certify a bake that never ran");

        // 2. The gate records "not proven" — deliberately NOT Complete, and deliberately not
        //    NotStarted either (this pod was armed and measuring; it just failed to).
        gate.Phase.Should().Be(BakePhase.Faulted,
            "the sweep errored, so nothing was verified — Complete is a claim about a sweep that RAN");
        gate.Detail.Should().Contain("NOT PROVEN",
            "the health payload must say the bake was never proven, not merely that it ended");

        // 3. The verdict that actually decides whether traffic arrives.
        var health = await new NodeTypeBakeHealthCheck(gate)
            .CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        Output.WriteLine($"{gate.Phase} → {health.Status}: {health.Description}");
        health.Status.Should().Be(HealthStatus.Unhealthy,
            "a pod that could not enumerate a single NodeType has verified nothing and must not "
            + "take traffic — with maxUnavailable:0 this stalls the rollout and the previous image "
            + "keeps serving, which is the entire purpose of the gate");
    }

    /// <summary>
    /// 🚨 FAIL-BEFORE / PASS-AFTER, HALF TWO: the enumeration succeeds and legitimately finds
    /// nothing — and the pod MUST be ready.
    ///
    /// <para>This is the half that pins the policy boundary. The retired bake Job treated a ZERO
    /// result as failure (<c>exitCode = 3</c>) because, from outside the process, "found nothing"
    /// and "could not look" were genuinely indistinguishable — it could only see the empty list the
    /// swallowed <c>Catch</c> handed it. Making the two states distinguishable AT THE SOURCE is
    /// what lets the surviving path gate on the one that matters and leave the other alone.
    /// Porting the Job's emptiness rule verbatim WOULD have reversed the documented policy; this
    /// test is what stops that happening later by accident.</para>
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task EnumerationFindsNothing_TheBakeIsComplete_AndThePodServes()
    {
        // Not blinded: the query answers normally. This mesh declares no dynamic NodeTypes, so the
        // sweep enumerates successfully and has nothing to build — the fresh/empty-mesh case.
        Mesh.ServiceProvider.GetRequiredService<BlindEnumerationQueryProvider>().Blind = false;

        var (gate, settlement) = await RunTheRealPreWarmHostedService();

        settlement.Should().Be(PreWarmSettlement.Completed,
            "the enumeration answered — an empty answer is still an answer");
        gate.Phase.Should().Be(BakePhase.Complete,
            "finding no dynamic NodeTypes is a legitimate result, not a failure to measure");

        var health = await new NodeTypeBakeHealthCheck(gate)
            .CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        Output.WriteLine($"{gate.Phase} → {health.Status}: {health.Description}");
        health.Status.Should().Be(HealthStatus.Healthy,
            "a genuinely empty mesh must serve — gating on emptiness would black-hole every fresh "
            + "install, which is why the retired Job's exit-3-on-empty rule was NOT ported");
    }

    /// <summary>
    /// The operator escape hatch — the successor to the retired Job's <c>Bake:AllowEmpty</c>.
    /// Deliberately narrower: emptiness never gates any more, so the only thing left to override is
    /// "I could not find out".
    ///
    /// <para>🚨 It relaxes the VERDICT, never the RECORD. The phase still reads
    /// <see cref="BakePhase.Faulted"/> and the payload still says the bake was not proven —
    /// a flag that rewrote the state would recreate the original defect one level up, where nothing
    /// could see it.</para>
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task AllowUnprovenBake_ServesAFaultedSweep_ButStillReportsItAsUnproven()
    {
        Mesh.ServiceProvider.GetRequiredService<BlindEnumerationQueryProvider>().Blind = true;

        var (gate, settlement) = await RunTheRealPreWarmHostedService(allowUnprovenBake: true);

        settlement.Should().Be(PreWarmSettlement.Faulted);
        gate.Phase.Should().Be(BakePhase.Faulted,
            "the override must not rewrite what happened — only what is done about it");

        var health = await new NodeTypeBakeHealthCheck(gate)
            .CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        Output.WriteLine($"{gate.Phase} → {health.Status}: {health.Description}");
        health.Status.Should().Be(HealthStatus.Healthy,
            "an operator who explicitly accepts an unproven bake may roll forward");
        health.Description.Should().Contain("NOT proven",
            "…but the payload must keep saying it was never proven — a silent override is how a "
            + "waived gate becomes an assumed one");
    }

    /// <summary>
    /// Runs the REAL <see cref="DynamicTypePreWarmerHostedService"/> against the REAL mesh, with the
    /// gate armed, and returns the state it left behind. Nothing about the sweep is re-implemented
    /// here: the service is constructed as the host constructs it and driven through the same
    /// <c>ApplicationStarted</c> callback, so the error handling under test is the shipped one.
    /// </summary>
    private async Task<(NodeTypeBakeGateState Gate, PreWarmSettlement Settlement)>
        RunTheRealPreWarmHostedService(bool allowUnprovenBake = false)
    {
        var gate = new NodeTypeBakeGateState
        {
            GatesReadiness = true,
            AllowUnprovenBake = allowUnprovenBake,
        };
        using var bake = new PreWarmCompletion();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [DynamicTypePreWarmerHostedService.EnabledConfigKey] = "true",
                // Small budget: no type here should need a compile, and a budget that cannot
                // elapse would let a wedge masquerade as a pass.
                [DynamicTypePreWarmerHostedService.PerTypeBudgetConfigKey] = "00:00:30",
            })
            .Build();

        // A composition root for the hosted service, not a stand-in for anything it measures:
        // the mesh hub is the REAL one, so the enumeration it runs is the real query against the
        // real query engine.
        var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddSingleton<IMessageHub>(Mesh)
            .AddSingleton(gate)
            .AddSingleton(bake)
            .BuildServiceProvider();

        var lifetime = new TestApplicationLifetime();
        using var service = new DynamicTypePreWarmerHostedService(
            services, lifetime, NullLogger<DynamicTypePreWarmerHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);

        // Wait on the actual terminal — the bake's own settlement signal — never a delay.
        var settled = bake.Settled.Take(1).Timeout(TimeSpan.FromSeconds(120)).ToTask();
        lifetime.NotifyStarted();
        var settlement = await settled;

        await service.StopAsync(CancellationToken.None);
        return (gate, settlement);
    }
}

/// <summary>
/// Makes the sweep's ENUMERATION query fail for real, at the real extension point the mesh's query
/// engine composes — the same technique <c>SourceDiscoveryUnavailableTest</c> uses to reproduce
/// cross-silo starvation. Nothing is mocked: this is an additional
/// <see cref="IMeshQueryProvider"/> in the mesh, and the fault it raises is the one production
/// actually produces (a <see cref="TimeoutException"/> out of a query that never answers).
///
/// <para>🚨 Scoped to the sweep's own query — <c>nodeType:NodeType</c>, matched EXACTLY, which is
/// what <c>DynamicTypePreWarmer</c> issues. Every other read on this mesh keeps working, so node
/// creation and single-node reads are unaffected and the test can never pass because the mesh as a
/// whole was broken.</para>
/// </summary>
public sealed class BlindEnumerationQueryProvider : IMeshQueryProvider
{
    /// <summary>The enumeration query, exactly as <c>DynamicTypePreWarmer.WarmDynamicTypes</c> builds it.</summary>
    private static readonly string EnumerationQuery = $"nodeType:{MeshNode.NodeTypePath}";

    /// <summary>
    /// Whether the enumeration query starves. An INSTANCE field on a mesh-scoped singleton, never
    /// static: it dies with the mesh, so it cannot bleed into another test.
    /// </summary>
    public bool Blind { get; set; }

    /// <inheritdoc />
    public string Name => nameof(BlindEnumerationQueryProvider);

    /// <inheritdoc />
    public bool Matches(IReadOnlyList<string> queryNamespaces) => true;

    /// <inheritdoc />
    public IObservable<QueryResultChange<T>> Query<T>(
        MeshQueryRequest request, JsonSerializerOptions options)
        => Blind && request.EffectiveQueries.Any(q =>
                string.Equals(q?.Trim(), EnumerationQuery, StringComparison.OrdinalIgnoreCase))
            ? Observable.Throw<QueryResultChange<T>>(new TimeoutException(
                "No response received for the NodeType enumeration within 00:00:30 (test stand-in "
                + "for a query that cannot answer — an unreachable store, a starved peer)"))
            : Observable.Return(new QueryResultChange<T>
            {
                ChangeType = QueryChangeType.Initial,
                Items = Array.Empty<T>(),
                Timestamp = DateTimeOffset.UtcNow,
            });

    /// <inheritdoc />
    public IObservable<IReadOnlyCollection<QueryResult>> Autocomplete(
        string basePath, string prefix, JsonSerializerOptions options,
        AutocompleteMode mode = AutocompleteMode.RelevanceFirst,
        int limit = 10,
        string? contextPath = null,
        string? context = null)
        => Observable.Return((IReadOnlyCollection<QueryResult>)Array.Empty<QueryResult>());

    /// <inheritdoc />
    public IObservable<T?> Select<T>(string path, string property, JsonSerializerOptions options)
        => Observable.Return(default(T?));
}

/// <summary>
/// Minimal <see cref="IHostApplicationLifetime"/> so the test can fire <c>ApplicationStarted</c>
/// itself — the callback the pre-warm hosted service registers its sweep on. A host-lifetime shim,
/// not a stand-in for any mesh interface.
/// </summary>
internal sealed class TestApplicationLifetime : IHostApplicationLifetime, IDisposable
{
    private readonly CancellationTokenSource started = new();
    private readonly CancellationTokenSource stopping = new();
    private readonly CancellationTokenSource stopped = new();

    public CancellationToken ApplicationStarted => started.Token;
    public CancellationToken ApplicationStopping => stopping.Token;
    public CancellationToken ApplicationStopped => stopped.Token;

    public void NotifyStarted() => started.Cancel();
    public void StopApplication() => stopping.Cancel();

    public void Dispose()
    {
        started.Dispose();
        stopping.Dispose();
        stopped.Dispose();
    }
}
