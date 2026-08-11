using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Part 1 of the fresh-pod compile-race hardening: the startup pre-warm
/// (<see cref="DynamicTypePreWarmer"/>) activates every dynamic NodeType's hub so its
/// Roslyn compile is front-loaded rather than firing on a user's first request.
///
/// <para>The contract this pins: the warm-up ENUMERATES and drives ALL dynamic types,
/// a type that fails to compile does NOT block a good one (both are reported, the good
/// one reaches a usable build), and the whole thing COMPLETES within budget — it never
/// hangs, so it could never wedge a readiness gate.</para>
/// </summary>
public class DynamicTypePreWarmerTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Partition = "PreWarmTest";
    private const string GoodPath = $"{Partition}/GoodType";
    private const string BrokenPath = $"{Partition}/BrokenType";

    [Fact(Timeout = 120_000)]
    public async Task WarmDynamicTypes_DrivesGoodType_AndBrokenTypeDoesNotBlockIt()
    {
        // A good dynamic type — a trivial identity Configuration compiles to a usable
        // assembly (HasUsableBuild → true).
        await NodeFactory.CreateNode(new MeshNode("GoodType", Partition)
        {
            Name = "Good Type",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Description = "Compiles cleanly.",
                Configuration = "config => config"
            }
        }).Should().Within(30.Seconds()).Emit();

        // A broken dynamic type — invalid C#; its compile settles at Error.
        await NodeFactory.CreateNode(new MeshNode("BrokenType", Partition)
        {
            Name = "Broken Type",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Description = "Never compiles.",
                Configuration = "config => this is not valid C# at all (("
            }
        }).Should().Within(30.Seconds()).Emit();

        var logger = Mesh.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("PreWarmTest");

        // Wait only for OUR two types among the outcomes (Take(2) + Timeout on the
        // ASSERT). The broken type must not stop the good one from being reported — if
        // a failure blocked the Merge, we'd never receive both and would time out.
        var outcomes = await DynamicTypePreWarmer
            .WarmDynamicTypes(Mesh, logger, perTypeBudget: TimeSpan.FromSeconds(90))
            .Where(o => o.TypePath == GoodPath || o.TypePath == BrokenPath)
            .Take(2)
            .ToList()
            .Timeout(TimeSpan.FromSeconds(100))
            .ToTask();

        var good = outcomes.Single(o => o.TypePath == GoodPath);
        var broken = outcomes.Single(o => o.TypePath == BrokenPath);

        Output.WriteLine($"Good  → {good.Status} {good.Detail}");
        Output.WriteLine($"Broken → {broken.Status} {broken.Detail}");

        // ReachedUsableBuild, not == Compiled: the type is equally fine whether THIS sweep compiled
        // it or found it already on the shared assembly store (AlreadyBaked) — which is exactly what
        // happens when the create-time compile landed before the sweep reached it. Asserting the
        // status verbatim would make this test a race against the store probe.
        good.ReachedUsableBuild.Should().BeTrue(
            "the pre-warmer must leave a healthy dynamic type with a usable build");
        broken.Status.Should().Be(PreWarmStatus.CompileError,
            "a non-compiling type surfaces a bounded CompileError — never a hang, never blocking the good type");
    }

    /// <summary>
    /// 🚨 THE STORM GUARD. The warm-up must never activate several cold NodeType hubs at once.
    ///
    /// <para>It shipped with a concurrency knob defaulting to 4, and 4 is measurably harmful: on
    /// 2026-07-28 04:05 four compiles fired at memex in quick succession — the identical load shape
    /// — and within minutes SIX plugin roots fell to the "did not settle" overlay and needed a
    /// scale-to-zero. That is the storm this class's own summary cites as the reason it ships
    /// disabled, and the same one behind the 2026-07-22 "told I am dead" restart loop.</para>
    ///
    /// <para>The knob is now GONE rather than set to 1: the sweep runs through <c>Concat</c> in
    /// dependency order, so concurrency is structurally impossible instead of merely defaulted
    /// away — a dependency order cannot be honoured while its members run in parallel. What is
    /// still a tunable value, and so still worth pinning, is the PACING between types: the damage
    /// only shows at production scale, where no test runs.</para>
    /// </summary>
    [Fact]
    public void TypesArePaced_AndThereIsNoConcurrencyKnobToTurnUp()
    {
        DynamicTypePreWarmer.BetweenTypes.Should().BeGreaterThan(TimeSpan.Zero,
            "back-to-back cold activations still read as a burst even when strictly sequential");
        DynamicTypePreWarmer.BetweenTypes.Should().BeLessThan(TimeSpan.FromSeconds(30),
            "…but the sweep must still finish in a sensible time on a mesh with many types");

        typeof(DynamicTypePreWarmer).GetField("DefaultMaxConcurrency").Should().BeNull(
            "re-introducing a concurrency knob would silently re-enable the storm — the sweep is "
            + "sequential by construction now");
        typeof(DynamicTypePreWarmer)
            .GetMethod(nameof(DynamicTypePreWarmer.WarmDynamicTypes))!
            .GetParameters().Should().NotContain(p => p.ParameterType == typeof(int),
                "no int knob on the warm entry point — concurrency is not a dial any more");
    }

    /// <summary>
    /// FAIL GRACEFULLY DOWNSTREAM — exercised through the warmer itself, not a re-implementation of
    /// its loop in the test.
    ///
    /// <para>A type that draws sources from a BROKEN type cannot build: its assembly would be
    /// missing exactly the sources the upstream owns. It must therefore be reported immediately as
    /// <see cref="PreWarmStatus.UpstreamFailed"/>, NAMING the blocker, instead of being attempted
    /// and burning its whole per-type budget on a guaranteed failure. And the blast radius must
    /// stay local: an unrelated healthy type still compiles.</para>
    ///
    /// <para>This closes the gap the pure graph tests cannot: they pin
    /// <c>FirstBlockedBy</c> and the topological order, but the propagation only holds because the
    /// warmer adds each SKIPPED type to the failed set as it goes. That wiring lives here.</para>
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task WarmDynamicTypes_SkipsDependentOfABrokenType_AndNamesTheBlocker()
    {
        const string upstream = $"{Partition}Down/BrokenUpstream";
        const string dependent = $"{Partition}Down/Dependent";
        const string unrelated = $"{Partition}Down/Unrelated";

        await NodeFactory.CreateNode(new MeshNode("BrokenUpstream", $"{Partition}Down")
        {
            Name = "Broken Upstream",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Description = "Never compiles — the upstream everything downstream waits on.",
                Configuration = "config => this is not valid C# at all (("
            }
        }).Should().Within(30.Seconds()).Emit();

        // Draws source OUT of the broken type's subtree — that is the dependency edge.
        await NodeFactory.CreateNode(new MeshNode("Dependent", $"{Partition}Down")
        {
            Name = "Dependent",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Description = "Compiles the upstream's source into its own assembly.",
                Configuration = "config => config",
                Sources = ["namespace:Source scope:subtree", $"shared=@{upstream}/Source"]
            }
        }).Should().Within(30.Seconds()).Emit();

        await NodeFactory.CreateNode(new MeshNode("Unrelated", $"{Partition}Down")
        {
            Name = "Unrelated",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Description = "Depends on nothing.",
                Configuration = "config => config"
            }
        }).Should().Within(30.Seconds()).Emit();

        var logger = Mesh.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("PreWarmDownstream");

        var outcomes = await DynamicTypePreWarmer
            .WarmDynamicTypes(Mesh, logger, perTypeBudget: TimeSpan.FromSeconds(90))
            .Where(o => o.TypePath == upstream || o.TypePath == dependent || o.TypePath == unrelated)
            .Take(3)
            .ToList()
            .Timeout(TimeSpan.FromSeconds(110))
            .ToTask();

        foreach (var o in outcomes)
            Output.WriteLine($"{o.TypePath} → {o.Status} {o.Detail}");

        outcomes.Single(o => o.TypePath == upstream).Status
            .Should().Be(PreWarmStatus.CompileError);

        var blocked = outcomes.Single(o => o.TypePath == dependent);
        blocked.Status.Should().Be(PreWarmStatus.UpstreamFailed,
            "a dependent of a broken type must be skipped up front, not attempted for a full "
            + "per-type budget on a build that cannot succeed");
        blocked.Detail.Should().Contain(upstream,
            "the outcome must NAME the blocker — 'something upstream failed' is not actionable");

        outcomes.Single(o => o.TypePath == unrelated).ReachedUsableBuild
            .Should().BeTrue(
                "a broken type contains its blast radius to its own dependents; unrelated types "
                + "must still end up with a usable build");
    }

    /// <summary>
    /// 🚨 "I don't know" must propagate as "I don't know" — the wiring behind the readiness gate's
    /// timeout leniency.
    ///
    /// <para><see cref="NodeTypeBakeGateState"/> refuses to gate a rollout on a type that merely
    /// TIMED OUT, because a cross-silo <c>SubscribeRequest</c> timeout (core #694) says nothing
    /// about whether the type builds. That leniency was worth nothing while it stopped at depth 1:
    /// the unevaluated upstream still turned every previously-healthy DEPENDENT into
    /// <see cref="PreWarmStatus.UpstreamFailed"/>, which DOES gate — so the false regression that
    /// stalled memex-cloud on 2026-08-02 simply reappeared one hop downstream.</para>
    ///
    /// <para>A budget too small for any real compile makes the upstream time out deterministically,
    /// with no dependence on machine speed in the direction that matters: the assertion is that the
    /// dependent is reported <see cref="PreWarmStatus.UpstreamUnevaluated"/> — NOT
    /// <see cref="PreWarmStatus.UpstreamFailed"/> — and so cannot stall a roll.</para>
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task WarmDynamicTypes_DependentOfAnUnevaluatedUpstream_IsUnevaluatedNotFailed()
    {
        const string upstream = $"{Partition}Slow/SlowUpstream";
        const string dependent = $"{Partition}Slow/SlowDependent";

        // Perfectly VALID — the point is that the sweep never gets an answer about it, not that
        // it is broken. With the sub-millisecond budget below it can only time out.
        await NodeFactory.CreateNode(new MeshNode("SlowUpstream", $"{Partition}Slow")
        {
            Name = "Slow Upstream",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Description = "Compiles fine — but not within the budget this sweep allows.",
                Configuration = "config => config"
            }
        }).Should().Within(30.Seconds()).Emit();

        await NodeFactory.CreateNode(new MeshNode("SlowDependent", $"{Partition}Slow")
        {
            Name = "Slow Dependent",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Description = "Draws source out of the slow upstream's subtree.",
                Configuration = "config => config",
                Sources = ["namespace:Source scope:subtree", $"shared=@{upstream}/Source"]
            }
        }).Should().Within(30.Seconds()).Emit();

        var logger = Mesh.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("PreWarmUnevaluated");

        var outcomes = await DynamicTypePreWarmer
            .WarmDynamicTypes(Mesh, logger, perTypeBudget: TimeSpan.FromMilliseconds(1))
            .Where(o => o.TypePath == upstream || o.TypePath == dependent)
            .Take(2)
            .ToList()
            .Timeout(TimeSpan.FromSeconds(110))
            .ToTask();

        foreach (var o in outcomes)
            Output.WriteLine($"{o.TypePath} → {o.Status} {o.Detail}");

        outcomes.Single(o => o.TypePath == upstream).Status
            .Should().Be(PreWarmStatus.TimedOut,
                "the budget is far too small for a real compile — this pins the premise of the test");

        var blocked = outcomes.Single(o => o.TypePath == dependent);
        blocked.Status.Should().Be(PreWarmStatus.UpstreamUnevaluated,
            "a dependent of a type the sweep never evaluated is itself unevaluated — reporting it "
            + "as UpstreamFailed would gate the rollout on a timeout, which is exactly the false "
            + "regression the direct-timeout leniency exists to prevent");
        blocked.Detail.Should().Contain(upstream,
            "the outcome must NAME the blocker — 'something upstream' is not actionable");

        // The gate is the consumer that matters: an unevaluated cascade must not hold readiness.
        var gate = new NodeTypeBakeGateState();
        gate.MarkRunning("go");
        foreach (var o in outcomes)
            gate.MarkOutcome(o);
        gate.MarkComplete("done");

        gate.Phase.Should().Be(BakePhase.Complete,
            "a sweep that only failed to get answers has proved nothing bad — it must not stall the roll");
        gate.Regressions.Should().BeEmpty();
    }

    // =============================================================================================
    // TORN SNAPSHOT (issue #1214): the bake can compile a HALF-APPLIED content update and report
    // it as an image regression. memex-cloud, 2026-08-11 11:04–11:12 UTC — the plugins repo had
    // reverted a branch and the portal's plugin auto-update was applying that revert WHILE the bake
    // compiled: `Store/Plugin/Source/Localizer` landed at 11:04:59 with two members removed, its
    // callers only at 11:06:57. The bake (10:47→11:06) compiled inside that two-minute gap, got
    // CS0117 on members that no source references any more, and refused readiness for four
    // `Store/*` types. Nothing was wrong with the image; the content converged seconds later and
    // the platform recompiled every one of them green — but BakePhase.Regressed is sticky, so the
    // pod never went Ready and the rollout hung until a human intervened.
    // =============================================================================================

    /// <summary>
    /// 🚨 THE #1214 CONTRACT, both halves in one mesh so the negative half has a real
    /// synchronisation point rather than a sleep.
    ///
    /// <para><b>Positive.</b> A previously-healthy type is broken by the FIRST write of a two-write
    /// content update (the helper loses a member its caller still calls — `Localizer` at 11:04:59),
    /// the gate records the regression and refuses readiness, and then the SECOND write lands (the
    /// caller stops calling it — 11:06:57). Nothing else happens: no deliberate retry, no restart,
    /// no human. The platform's own park registry un-parks and recompiles the type on the source
    /// change, and the gate must FOLLOW it back to Complete.</para>
    ///
    /// <para><b>Negative.</b> A second type is genuinely broken on a source set that nobody
    /// touches. Its regression must STAND — the retraction is driven by the type reaching a usable
    /// build, which a broken type never does. By the time the first type has been retracted the
    /// watch machinery has demonstrably run, so "still gated" here is evidence, not latency.</para>
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task RegressedType_ThatRebuildsAfterTheContentConverges_IsRetracted_ButARealBreakStillGates()
    {
        var workspace = Mesh.GetWorkspace();
        const string ns = $"{Partition}Torn";
        const string tornPath = $"{ns}/TornType";
        const string helperPath = $"{ns}/TornType/Source/helper";
        const string callerPath = $"{ns}/TornType/Source/caller";
        const string brokenPath = $"{ns}/StaysBroken";
        const string brokenSourcePath = $"{ns}/StaysBroken/Source/code";

        // ── The torn type: ONE package update rewrites TWO source nodes, and the bake compiles
        //    between the two writes. Helper defines the member; Caller uses it.
        await NodeFactory.CreateNode(new MeshNode("helper", $"{tornPath}/Source")
        {
            Name = "Helper",
            NodeType = "Code",
            Content = new CodeConfiguration
            {
                Code = "public static class TornHelper { public static int Answer() => 42; }",
                Language = "csharp"
            }
        }).Should().Within(30.Seconds()).Emit();

        await NodeFactory.CreateNode(new MeshNode("caller", $"{tornPath}/Source")
        {
            Name = "Caller",
            NodeType = "Code",
            Content = new CodeConfiguration
            {
                Code = "public static class TornCaller { public static int Get() => TornHelper.Answer(); }",
                Language = "csharp"
            }
        }).Should().Within(30.Seconds()).Emit();

        await NodeFactory.CreateNode(new MeshNode("TornType", ns)
        {
            Name = "Torn Type",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Description = "Broken by a half-applied two-write content update, then repaired.",
                Configuration = "config => config"
            }
        }).Should().Within(30.Seconds()).Emit();

        // ── The genuinely broken type: invalid C# in a source nobody will touch.
        await NodeFactory.CreateNode(new MeshNode("code", $"{brokenPath}/Source")
        {
            Name = "Code",
            NodeType = "Code",
            Content = new CodeConfiguration { Code = "this is not valid C#;", Language = "csharp" }
        }).Should().Within(30.Seconds()).Emit();

        await NodeFactory.CreateNode(new MeshNode("StaysBroken", ns)
        {
            Name = "Stays Broken",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Description = "A real compile error on a source set that never changes.",
                Configuration = "config => config"
            }
        }).Should().Within(30.Seconds()).Emit();

        // Healthy on the way in — the WasHealthyBeforeBake baseline a real sweep takes when it
        // starts (the torn type was Ok at 10:47). The broken one settles at Error by itself.
        await Precompile(tornPath, d => d.CompilationStatus == CompilationStatus.Ok);
        var reallyBroken = await WhenDefinition(brokenPath,
            d => d.CompilationStatus == CompilationStatus.Error, TimeSpan.FromSeconds(120));

        // ── 11:04:59. The revert lands on the HELPER: the member is gone, the caller still calls
        //    it. The bake DRIVES the compile that samples this state (the sweep activates and
        //    compiles each type), so the trigger here is explicit, exactly as it was in production.
        await workspace.GetMeshNodeStream(helperPath).Update(curr =>
                curr with
                {
                    Content = new CodeConfiguration
                    {
                        Code = "public static class TornHelper { }", Language = "csharp"
                    }
                })
            .Should().Within(30.Seconds()).Emit();
        await workspace.GetMeshNodeStream(tornPath).Update(curr =>
            {
                if (curr?.Content is not NodeTypeDefinition def) return curr!;
                return curr with
                {
                    Content = def with
                    {
                        RequestedReleaseAt = DateTimeOffset.UtcNow,
                        RequestedReleaseForce = true
                    }
                };
            })
            .Should().Within(30.Seconds()).Emit();

        var tornFailure = await WhenDefinition(tornPath,
            d => d.CompilationStatus == CompilationStatus.Error, TimeSpan.FromSeconds(120));
        Output.WriteLine($"{tornPath} → Error: {tornFailure.CompilationError}");
        tornFailure.CompilationError.Should().MatchRegex(@"CS\d{4}",
            "the premise of this test is a REAL Roslyn diagnostic produced against a source set "
            + "that is being replaced — an infrastructure abort would prove nothing");

        // ── The bake's verdict. Both types were previously healthy as far as this gate is
        //    concerned, so both failures read as regressions and the pod refuses readiness.
        var logger = Mesh.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("TornSnapshot");
        var gate = new NodeTypeBakeGateState { GatesReadiness = true };
        gate.MarkRunning("baking");
        gate.MarkOutcome(new PreWarmOutcome(
                tornPath, PreWarmStatus.CompileError, tornFailure.CompilationError)
            { WasHealthyBeforeBake = true })
            .Should().BeTrue();
        gate.MarkOutcome(new PreWarmOutcome(
                brokenPath, PreWarmStatus.CompileError, reallyBroken.CompilationError)
            { WasHealthyBeforeBake = true })
            .Should().BeTrue();
        gate.MarkComplete("baked in 00:19:00");
        gate.Phase.Should().Be(BakePhase.Regressed, "two previously-healthy types failed to build");

        // The production wiring: every type the gate condemned is WATCHED for a recovery.
        using var tornWatch = DynamicTypePreWarmer.WatchForRecovery(Mesh, gate, tornPath, logger);
        using var brokenWatch = DynamicTypePreWarmer.WatchForRecovery(Mesh, gate, brokenPath, logger);

        // 🚨 THE TRAP THIS PINS. A failed compile KEEPS the previous build's assembly coordinates
        // and framework stamp — ApplyCompileFailure clears only the status, the error text and
        // CompiledSources — so `HasUsableBuild` is still TRUE for a type that has just failed. A
        // recovery watch that matched on it would retract every regression the instant it was
        // recorded and quietly disable the entire gate. (The first draft of this change did exactly
        // that, and this assertion is what caught it.) Nothing has driven a compile since the
        // failure, so the watch must be sitting still.
        gate.Retracted.Should().BeEmpty(
            "the torn type still carries the assembly of its LAST GOOD build — only a compile that "
            + "is demonstrably NEWER than the failure may retract a regression");
        gate.Phase.Should().Be(BakePhase.Regressed);

        // ── 11:06:57. The update's SECOND write lands and the content is self-consistent again.
        //    NOTHING ELSE HAPPENS — no compile trigger, no recycle, no restart.
        await workspace.GetMeshNodeStream(callerPath).Update(curr =>
                curr with
                {
                    Content = new CodeConfiguration
                    {
                        Code = "public static class TornCaller { public static int Get() => 7; }",
                        Language = "csharp"
                    }
                })
            .Should().Within(30.Seconds()).Emit();

        // The platform recompiles the parked type on its own (the park registry's
        // "retry only if the sources changed" path) — and the GATE must let go of it.
        await WhenGate(gate, g => g.Retracted.ContainsKey(tornPath), TimeSpan.FromSeconds(150));

        Output.WriteLine($"gate → {gate.Phase}: {gate.Detail}");
        gate.Regressions.Keys.Should().NotContain(tornPath,
            "the type rebuilt on THIS image, so the earlier failure was never evidence against it");
        gate.Detail.Should().Contain("retracted",
            "a regression that healed still happened — it must stay visible in the health payload");

        // …and it was a FRESH build that retracted it, not the stale one the failure kept. This is
        // the deterministic half of the same guard as the Retracted-is-empty check above.
        var rebuilt = await WhenDefinition(tornPath,
            d => d.CompilationStatus == CompilationStatus.Ok, TimeSpan.FromSeconds(30));
        (rebuilt.LastCompileSucceededAt > tornFailure.LastCompileSucceededAt).Should().BeTrue(
            "the retraction must be driven by a compile that ran AFTER the regression was recorded "
            + "— the record's pre-failure success timestamp must never be enough. Failure carried "
            + $"{tornFailure.LastCompileSucceededAt:O}, rebuild {rebuilt.LastCompileSucceededAt:O}");

        // ── The negative half. The watch machinery has now demonstrably run (it retracted the
        //    torn type), so the genuinely broken type still being held is evidence, not latency:
        //    a real compile error on a source set nobody touched STILL GATES, and the pod stays
        //    out of rotation — which is the entire point of the gate and must not have been
        //    weakened by any of the above.
        gate.Retracted.Keys.Should().NotContain(brokenPath,
            "a genuinely broken type never reaches a usable build, so nothing may retract it");
        gate.Regressions.Keys.Should().Equal(brokenPath);
        gate.Phase.Should().Be(BakePhase.Regressed,
            "one real compile error on a stable source set is enough to refuse readiness — "
            + "retracting the torn type must not have released the gate");
    }

    /// <summary>
    /// Waits (level-triggered) until the gate satisfies <paramref name="predicate"/>. The gate is a
    /// deliberately non-reactive, lock-free singleton — the readiness probe pulls it synchronously
    /// — so there is no stream to filter on and the sanctioned interval re-read applies
    /// (WritingTests.md → "Polling loops around QueryAsync"); never a fixed <c>Task.Delay</c>.
    /// </summary>
    private static Task WhenGate(
        NodeTypeBakeGateState gate, Func<NodeTypeBakeGateState, bool> predicate, TimeSpan timeout)
        => Observable.Interval(TimeSpan.FromMilliseconds(100)).StartWith(0L)
            .Select(_ => gate)
            .Where(predicate)
            .FirstAsync()
            .Timeout(timeout)
            .ToTask();

    // =============================================================================================
    // Batch bake (issue #1207): the initial-bake mode drives the ONE compiler directly — batched
    // source discovery, no per-type hub activation, no compile-watcher settle — and must produce
    // the same artifacts (per-type assemblies on the share, compile-state stamps on the type
    // node) and the same PreWarmOutcome vocabulary as the activation-driven sweep.
    // =============================================================================================

    /// <summary>Waits (level-triggered) until the type at <paramref name="path"/> satisfies
    /// <paramref name="predicate"/> on the OWNER's live stream — the same read surface the GUI
    /// and the enrichment use, so for a batch-stamped type this also proves the storage-level
    /// stamp reconciled into the live per-node hub via the adapter's change feed.</summary>
    private Task<NodeTypeDefinition> WhenDefinition(
        string path, Func<NodeTypeDefinition, bool> predicate, TimeSpan timeout)
        => Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Select(n => n.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions))
            .Where(d => d is not null && predicate(d!))
            .Select(d => d!)
            .Take(1)
            .Timeout(timeout)
            .ToTask();

    /// <summary>
    /// Waits until the PERSISTED node at <paramref name="path"/> satisfies
    /// <paramref name="predicate"/> — a re-query poll against the storage adapter, which is the
    /// input the sweep's enumeration reads. The own-hub stream settles FIRST and the durable
    /// write lags it (the owner's debounced persistence sampler), so a test that stages state
    /// for the sweep after watching only the live stream races the very read the sweep makes:
    /// the probe would see a pre-stamp record and re-bake a type the test believed was settled.
    ///
    /// <para>🚨 A storage poll STAGES NOTHING — it only observes. See <see cref="Precompile"/>:
    /// polling storage for a compile nobody triggered waits forever.</para>
    /// </summary>
    private Task<NodeTypeDefinition> WhenPersisted(
        string path, Func<NodeTypeDefinition, bool> predicate, TimeSpan timeout)
    {
        var storage = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
        return Observable.Interval(TimeSpan.FromMilliseconds(100)).StartWith(0L)
            .SelectMany(_ => storage.Read(path, Mesh.JsonSerializerOptions))
            .Select(n => n.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions))
            .Where(d => d is not null && predicate(d!))
            .Select(d => d!)
            .FirstAsync()
            .Timeout(timeout)
            .ToTask();
    }

    /// <summary>
    /// 🚨 Drives a NodeType's FIRST compile the only way the framework ever drives one — by
    /// ACTIVATING its own hub — and returns once that compile has settled BOTH on the live
    /// stream and in storage.
    ///
    /// <para>There is no such thing as a "create-time compile". Creating a NodeType node writes
    /// a record; the compile watcher and its first-build kickoff are installed by
    /// <c>NodeTypeCompilationHelpers.InstallCompileWatcher</c> from the per-NodeType hub's
    /// initialization hook, so they exist only once that hub is ACTIVATED — which is what
    /// subscribing to the node's stream does (the same activation
    /// <see cref="DynamicTypePreWarmer"/>'s activation-driven sweep performs, and the very one
    /// #1207's batch mode removes). <see cref="WhenPersisted"/> reads the storage adapter
    /// directly and therefore activates nothing: polling it for a compile no one triggered can
    /// only time out, which is exactly how these tests used to hang before reaching the batch
    /// path at all.</para>
    ///
    /// <para>So: subscribe to the LIVE stream first (that is the trigger AND the settle), then
    /// wait for the durable record, because the sweep under test enumerates persistence and the
    /// owner's debounced persistence sampler lags the stream.</para>
    /// </summary>
    private async Task<NodeTypeDefinition> Precompile(
        string path, Func<NodeTypeDefinition, bool> settled)
    {
        await WhenDefinition(path, settled, TimeSpan.FromSeconds(60));
        return await WhenPersisted(path, settled, TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// Empties the shared assembly store this test class compiles into, so a type this test
    /// has not itself compiled CANNOT come back <see cref="PreWarmStatus.AlreadyBaked"/>.
    ///
    /// <para>The store root is per test CLASS (see <c>MonolithMeshTestBase.AssemblyStoreRoot</c>)
    /// and its keys are <c>(typePath, version)</c> under the live framework tag — so a sibling
    /// <c>[Fact]</c>, or a previous process that happened to reuse this pid, can leave bytes
    /// that make the probe report a type baked before this test compiled anything. Clearing is
    /// a PRECONDITION, not a stage: it makes "the batch really built this" deterministic.</para>
    /// </summary>
    private void ClearAssemblyStore()
    {
        if (Directory.Exists(AssemblyStoreRoot))
            Directory.Delete(AssemblyStoreRoot, recursive: true);
        Directory.CreateDirectory(AssemblyStoreRoot);
    }

    /// <summary>
    /// The headline #1207 contract: a batch bake over three types with a real cross-type source
    /// dependency (B compiles A's Code into its own assembly) compiles ALL of them, dependencies
    /// first, writes per-type assemblies to the share, and stamps each type node with the same
    /// compile-state field-set the activation path writes — including the batched discovery's
    /// source snapshot (B's <see cref="NodeTypeDefinition.CompiledSources"/> names A's file).
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task BatchBake_CompilesPendingTypes_InDependencyOrder_AndStampsTheRecords()
    {
        const string ns = $"{Partition}Batch";
        const string upstream = $"{ns}/Upstream";
        const string dependent = $"{ns}/Dependent";
        const string unrelated = $"{ns}/Unrelated";

        // A real shared source file: the dependent's compile must pull THIS Code node out of the
        // upstream's subtree — that is the dependency edge the topological order honours.
        await NodeFactory.CreateNode(new MeshNode("Helper", $"{upstream}/Source")
        {
            Name = "Helper",
            NodeType = CodeNodeType.NodeType,
            Content = new CodeConfiguration
            {
                Code = "public static class BatchBakeHelper { public const int Answer = 42; }"
            }
        }).Should().Within(30.Seconds()).Emit();

        await NodeFactory.CreateNode(new MeshNode("Upstream", ns)
        {
            Name = "Upstream",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition { Configuration = "config => config" }
        }).Should().Within(30.Seconds()).Emit();

        await NodeFactory.CreateNode(new MeshNode("Dependent", ns)
        {
            Name = "Dependent",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Configuration = "config => config",
                Sources = ["namespace:Source scope:subtree", $"shared=@{upstream}/Source"]
            }
        }).Should().Within(30.Seconds()).Emit();

        await NodeFactory.CreateNode(new MeshNode("Unrelated", ns)
        {
            Name = "Unrelated",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition { Configuration = "config => config" }
        }).Should().Within(30.Seconds()).Emit();

        // 🚨 NOTHING has compiled these types, and nothing will until something activates their
        // hubs — which is precisely the state an INITIAL BAKE finds and precisely what #1207 is
        // about. So there is no create-time compile to "let settle": every type is NeverBuilt,
        // i.e. pending AND previously-healthy (BakeState.NeverBuilt ⇒ WasHealthy), and the batch
        // must build all three without activating anything. Emptying the share makes that
        // airtight — no sibling [Fact]'s bytes can make one of them report AlreadyBaked.
        ClearAssemblyStore();

        var logger = Mesh.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("BatchBake");
        var outcomes = await DynamicTypePreWarmer
            .WarmDynamicTypes(Mesh, logger, perTypeBudget: TimeSpan.FromSeconds(90), batchBake: true)
            .Where(o => o.TypePath is upstream or dependent or unrelated)
            .Take(3)
            .ToList()
            .Timeout(TimeSpan.FromSeconds(100))
            .ToTask();

        foreach (var o in outcomes)
            Output.WriteLine($"{o.TypePath} → {o.Status} {o.Detail}");

        // All compiled — genuinely: the share was empty, so AlreadyBaked is impossible.
        outcomes.Should().OnlyContain(o => o.Status == PreWarmStatus.Compiled,
            "with the share cleared every type needs a real build, and the batch must deliver one per type");
        outcomes.Should().OnlyContain(o => o.WasHealthyBeforeBake,
            "a never-built type is not damaged goods — nothing here may read as a regression that gates a roll");

        // Dependencies first: the type whose source the dependent consumes builds before it.
        var emissionOrder = outcomes.Select(o => o.TypePath).ToList();
        emissionOrder.IndexOf(upstream).Should().BeLessThan(emissionOrder.IndexOf(dependent),
            "B compiles A's Code into its own assembly, so A must be built (and reported) first");

        var store = Mesh.ServiceProvider.GetRequiredService<IAssemblyStore>();
        foreach (var path in new[] { upstream, dependent, unrelated })
        {
            // The stamp is the activation path's field-set, written storage-level and read back
            // here through the OWNER's live stream — so this also proves a hub that activates
            // after a batch bake hydrates the batch's record rather than a pre-bake one.
            var stamped = await WhenDefinition(path,
                d => d.CompilationStatus == CompilationStatus.Ok
                    && d.LastCompileSucceededAt is not null,
                TimeSpan.FromSeconds(30));
            stamped.CompiledFrameworkVersion.Should().Be(NodeTypeCompilationHelpers.FrameworkVersion,
                "lazy activations gate on HasUsableBuild, which compares this to the live framework");
            stamped.LastCompiledVersion.Should().NotBeNull();
            stamped.CompilationError.Should().BeNull();

            // Per-type assemblies exactly as today: the share holds bytes at the stamped key.
            var assemblyPath = await store
                .TryGetAssemblyPath(path, stamped.LastCompiledVersion!.Value)
                .Take(1)
                .Timeout(TimeSpan.FromSeconds(10))
                .ToTask();
            assemblyPath.Should().NotBeNullOrEmpty(
                $"the batch bake must leave {path}'s assembly on the share at the stamped version");
        }

        // The batched discovery resolved the CROSS-TYPE source: the dependent's compile consumed
        // the upstream's Code node, and the stamp records exactly what was compiled.
        var dependentDef = await WhenDefinition(dependent,
            d => d.CompiledSources is { Count: > 0 }, TimeSpan.FromSeconds(10));
        dependentDef.CompiledSources!.ContainsKey($"{upstream}/Source/Helper").Should().BeTrue(
            "the dependent declares shared=@Upstream/Source, so the batch discovery must hand its "
            + "compile the upstream's file — this is the 'one linked build' of #1207");
    }

    /// <summary>
    /// A broken type mid-graph must not abort the batch: it reports
    /// <see cref="PreWarmStatus.CompileError"/>, its dependents inherit
    /// <see cref="PreWarmStatus.UpstreamFailed"/> NAMING the blocker (same cascade as the
    /// activation sweep), and unrelated types still reach a usable build.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task BatchBake_BrokenTypeMidGraph_YieldsCompileErrorAndUpstreamFailed_WithoutAbortingTheBatch()
    {
        const string ns = $"{Partition}BatchDown";
        const string broken = $"{ns}/Broken";
        const string dependent = $"{ns}/Dependent";
        const string unrelated = $"{ns}/Unrelated";

        await NodeFactory.CreateNode(new MeshNode("Broken", ns)
        {
            Name = "Broken",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Configuration = "config => this is not valid C# at all (("
            }
        }).Should().Within(30.Seconds()).Emit();

        await NodeFactory.CreateNode(new MeshNode("Dependent", ns)
        {
            Name = "Dependent",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Configuration = "config => config",
                Sources = ["namespace:Source scope:subtree", $"shared=@{broken}/Source"]
            }
        }).Should().Within(30.Seconds()).Emit();

        await NodeFactory.CreateNode(new MeshNode("Unrelated", ns)
        {
            Name = "Unrelated",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition { Configuration = "config => config" }
        }).Should().Within(30.Seconds()).Emit();

        // The share holds nothing for these paths, so `dependent` and `unrelated` are simply
        // NeverBuilt — pending, and previously healthy. `broken`, though, has to arrive at the
        // sweep ALREADY at CompilationStatus.Error: that is the whole point of
        // WasHealthyBeforeBake, and a type nobody ever compiled is not "already broken". Drive
        // exactly that one compile through the hub activation the framework uses (Precompile),
        // and only that one — the batch sweep is otherwise the first thing to touch these types.
        ClearAssemblyStore();
        await Precompile(broken, d => d.CompilationStatus == CompilationStatus.Error);

        var logger = Mesh.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("BatchBakeDown");
        var outcomes = await DynamicTypePreWarmer
            .WarmDynamicTypes(Mesh, logger, perTypeBudget: TimeSpan.FromSeconds(90), batchBake: true)
            .Where(o => o.TypePath is broken or dependent or unrelated)
            .Take(3)
            .ToList()
            .Timeout(TimeSpan.FromSeconds(100))
            .ToTask();

        foreach (var o in outcomes)
            Output.WriteLine($"{o.TypePath} → {o.Status} {o.Detail}");

        var brokenOutcome = outcomes.Single(o => o.TypePath == broken);
        brokenOutcome.Status.Should().Be(PreWarmStatus.CompileError,
            "the batch drives the compiler directly, and Roslyn's verdict must surface as the "
            + "same CompileError the activation path reports");
        brokenOutcome.WasHealthyBeforeBake.Should().BeFalse(
            "the type was already broken before the sweep — the gate must not read it as a regression");

        var blocked = outcomes.Single(o => o.TypePath == dependent);
        blocked.Status.Should().Be(PreWarmStatus.UpstreamFailed,
            "a dependent of a genuinely broken type is skipped with a verdict, not attempted");
        blocked.Detail.Should().Contain(broken,
            "the outcome must NAME the blocker");

        outcomes.Single(o => o.TypePath == unrelated).Status
            .Should().Be(PreWarmStatus.Compiled,
                "one broken type must not abort the batch — unrelated types still build");
    }

    /// <summary>
    /// A type whose bytes are already on the share is reported
    /// <see cref="PreWarmStatus.AlreadyBaked"/> by the batch sweep and is NOT rebuilt — the
    /// share probe short-circuits before any compile, exactly like the activation sweep. This is
    /// what keeps the bake restartable and a second replica cheap.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task BatchBake_TypeAlreadyOnTheShare_ReportsAlreadyBaked_AndIsNotRebuilt()
    {
        const string ns = $"{Partition}BatchWarm";
        const string warm = $"{ns}/WarmType";

        await NodeFactory.CreateNode(new MeshNode("WarmType", ns)
        {
            Name = "Warm Type",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition { Configuration = "config => config" }
        }).Should().Within(30.Seconds()).Emit();

        // Put the bytes on the share the ordinary ("usual compile") way — activate the type's
        // own hub and let its compile watcher build + upload — and settle on the PERSISTED
        // record, because the sweep's enumeration reads persistence and the durable write lags
        // the own-hub stream.
        var settled = await Precompile(warm,
            d => d.CompilationStatus == CompilationStatus.Ok
                && !string.IsNullOrEmpty(d.LatestAssemblyCollection)
                && d.LastCompiledVersion is not null);
        var baseline = settled.LastCompileSucceededAt;

        var logger = Mesh.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("BatchBakeWarm");
        var outcome = await DynamicTypePreWarmer
            .WarmDynamicTypes(Mesh, logger, perTypeBudget: TimeSpan.FromSeconds(90), batchBake: true)
            .Where(o => o.TypePath == warm)
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(100))
            .ToTask();

        Output.WriteLine($"{outcome.TypePath} → {outcome.Status} {outcome.Detail}");
        outcome.Status.Should().Be(PreWarmStatus.AlreadyBaked,
            "the share already holds this build — the batch must not spend a compile on it");

        var after = await WhenDefinition(warm,
            d => d.CompilationStatus == CompilationStatus.Ok, TimeSpan.FromSeconds(10));
        after.LastCompileSucceededAt.Should().Be(baseline,
            "AlreadyBaked means NOT rebuilt — the record must be untouched by the sweep");
    }
}
