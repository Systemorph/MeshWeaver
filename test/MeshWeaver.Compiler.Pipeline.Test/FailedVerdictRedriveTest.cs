using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

using MeshWeaver.Compiler;
namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 Issue #1793 — a NodeType that has NEVER compiled successfully and settles at
/// <see cref="CompilationStatus.Error"/> must be reachable by an automatic re-drive.
///
/// <para><b>The hole.</b> <c>ApplyCompileFailure</c> writes no assembly coordinates and no
/// framework stamp, so for such a type <see cref="NodeTypeDefinition.LatestAssemblyCollection"/>,
/// <see cref="NodeTypeDefinition.LatestAssemblyPath"/> and
/// <see cref="NodeTypeDefinition.CompiledFrameworkVersion"/> are null forever — and every
/// automatic path keys off one of them or off a state only a first success produces. The
/// framework-stale kickoff's own filter reads <c>Ok or Error</c>, so it INTENDS to cover a failed
/// type; it cannot, because <see cref="NodeTypeCompilationHelpers.HasStaleFrameworkBuild(NodeTypeDefinition, string?)"/>
/// delegates to coordinates a failure never writes.</para>
///
/// <para>These are the PURE pins of the fix: the verdict-inputs token, the re-drive predicate
/// built on it, the stamps that keep it truthful, and the ledger that bounds and exposes a
/// re-drive which fails to converge. The end-to-end behaviour (a broken type actually recovering
/// on a real mesh, and NOT looping while it stays broken) is pinned by
/// <c>NeverCompiledFailureRedriveTest</c> in MeshWeaver.Hosting.Monolith.Test.</para>
/// </summary>
public class FailedVerdictRedriveTest
{
    private const string TypePath = "P/T";

    private static NodeTypeDefinition NeverCompiledFailure(
        string? failedInputs = null,
        IReadOnlyDictionary<string, long>? currentSources = null,
        CompilationStatus status = CompilationStatus.Error) => new()
    {
        Configuration = "config => config",
        Sources = ["namespace:Source scope:subtree"],
        CompilationStatus = status,
        CompilationError = "CS0103: The name 'Nope' does not exist in the current context",
        CurrentSourceVersions = currentSources,
        FailedBuildInputs = failedInputs,
    };

    private static ImmutableDictionary<string, long> Sources(params (string Path, long Ticks)[] entries)
    {
        var map = ImmutableDictionary<string, long>.Empty;
        foreach (var (path, ticks) in entries)
            map = map.SetItem(path, ticks);
        return map;
    }

    // ── The trigger fires exactly where the framework-stale twin cannot ──────────────────────

    /// <summary>
    /// The migration case, and the population #1786 measured: a node imported (or left behind) at
    /// <c>Error</c> with NO verdict-inputs stamp at all. A null stamp differs from every live
    /// token, so it earns exactly one automatic attempt — which is the whole point of the fix.
    /// </summary>
    [Fact]
    public void NeverCompiledError_WithNoStamp_IsReDrivable()
    {
        var def = NeverCompiledFailure(currentSources: Sources(("P/T/Source/a", 1)));

        NodeTypeCompilationHelpers.HasStaleFailureVerdict(def, modulesHash: null)
            .Should().BeTrue(
                "a failure verdict that records nothing about the inputs it was formed under must "
                + "not be treated as already-attempted on this deployment");

        // …and none of the paths that exist today can see it — the reason the fix is needed at all.
        def.CompilationStatus.Should().NotBeNull("the first-build kickoff needs a null status");
        def.CompilationStatus.Should().NotBe(CompilationStatus.Compiling, "the recovery kickoff needs Compiling");
        NodeTypeCompilationHelpers.HasStaleFrameworkBuild(def, modulesHash: null)
            .Should().BeFalse(
                "the framework-stale kickoff delegates to assembly coordinates a failed compile "
                + "never stamps — that is precisely why it could not cover this node");
    }

    /// <summary>The self-limiting property, stated directly: the token the re-drive stamps is the
    /// token the trigger compares against, so the trigger is false the instant the re-drive
    /// fires. A reconcile that can re-arm its own trigger is the #223 write-storm shape.</summary>
    [Fact]
    public void StampingTheLiveInputs_MakesTheTriggerFalse()
    {
        var sources = Sources(("P/T/Source/a", 1), ("P/T/Source/b", 2));
        var def = NeverCompiledFailure(currentSources: sources);
        NodeTypeCompilationHelpers.HasStaleFailureVerdict(def, "mod-1").Should().BeTrue();

        var stamped = def with
        {
            FailedBuildInputs = NodeTypeCompilationHelpers.BuildInputsToken("mod-1", sources),
        };

        NodeTypeCompilationHelpers.HasStaleFailureVerdict(stamped, "mod-1")
            .Should().BeFalse("the re-drive's own bookkeeping must not schedule another pass");
    }

    /// <summary>A new framework (the deploy that carries the fix) re-opens the attempt — one per
    /// framework, which is what lets a shipped fix reach the nodes it was written for.</summary>
    [Fact]
    public void AVerdictFromAnotherFramework_IsReDrivable()
    {
        var sources = Sources(("P/T/Source/a", 1));
        var fromAnotherImage = NeverCompiledFailure(
            failedInputs: $"fw=0ldfr4me;mod=(none);src={TokenSourcePart("mod-x", sources)}",
            currentSources: sources);

        NodeTypeCompilationHelpers.HasStaleFailureVerdict(fromAnotherImage, modulesHash: null)
            .Should().BeTrue("a new framework has never had its attempt at this type");
    }

    /// <summary>A module-only update moves the compile surface without moving the framework MVID —
    /// the same reasoning <c>HasUsableBuild</c>'s modules-hash join encodes for successes.</summary>
    [Fact]
    public void AVerdictFromAnotherModuleSet_IsReDrivable()
    {
        var sources = Sources(("P/T/Source/a", 1));
        var def = NeverCompiledFailure(
            failedInputs: NodeTypeCompilationHelpers.BuildInputsToken("mod-OLD", sources),
            currentSources: sources);

        NodeTypeCompilationHelpers.HasStaleFailureVerdict(def, "mod-OLD").Should().BeFalse();
        NodeTypeCompilationHelpers.HasStaleFailureVerdict(def, "mod-NEW")
            .Should().BeTrue("a module update can be exactly the fix the failing compile needed");
    }

    /// <summary>
    /// 🚨 "A fix to the failing code" — the case the issue names and the one the in-memory park
    /// registry cannot cover, because a failure that predates this PROCESS is not in it.
    /// </summary>
    [Fact]
    public void AnEditedSource_IsReDrivable_EvenWhenTheFrameworkDidNotMove()
    {
        var broken = Sources(("P/T/Source/a", 1));
        var def = NeverCompiledFailure(
            failedInputs: NodeTypeCompilationHelpers.BuildInputsToken(null, broken),
            currentSources: broken);
        NodeTypeCompilationHelpers.HasStaleFailureVerdict(def, null).Should().BeFalse();

        var fixedUp = def with { CurrentSourceVersions = Sources(("P/T/Source/a", 2)) };
        NodeTypeCompilationHelpers.HasStaleFailureVerdict(fixedUp, null)
            .Should().BeTrue("an edit that may BE the fix must earn a fresh attempt");

        var added = def with { CurrentSourceVersions = broken.SetItem("P/T/Source/b", 9) };
        NodeTypeCompilationHelpers.HasStaleFailureVerdict(added, null)
            .Should().BeTrue("an added source changes the compile unit");

        var removed = def with { CurrentSourceVersions = ImmutableDictionary<string, long>.Empty };
        NodeTypeCompilationHelpers.HasStaleFailureVerdict(removed, null)
            .Should().BeTrue("a removed source changes the compile unit too");
    }

    /// <summary><see cref="CompilationStatus.Unavailable"/> records that the compile never reached
    /// a verdict at all — even less of a reason to stop trying than an Error.</summary>
    [Fact]
    public void AnUndeterminedVerdict_IsReDrivenTheSameWay()
    {
        var def = NeverCompiledFailure(
            currentSources: Sources(("P/T/Source/a", 1)), status: CompilationStatus.Unavailable);
        NodeTypeCompilationHelpers.HasStaleFailureVerdict(def, null).Should().BeTrue();
    }

    /// <summary>
    /// 🚨 Never re-drive from a source set nobody established. On a cold activation the sources
    /// watcher has not written <see cref="NodeTypeDefinition.CurrentSourceVersions"/> yet, and
    /// "not known yet" is not "no sources": compiling there forms a verdict from evidence the
    /// mesh does not have (#1216), and the watcher's first write would immediately change the
    /// token and burn a second attempt for free.
    /// </summary>
    [Fact]
    public void AnUnestablishedSourceSet_Waits_RatherThanReDriving()
    {
        var unseeded = NeverCompiledFailure(currentSources: null);
        NodeTypeCompilationHelpers.HasStaleFailureVerdict(unseeded, null)
            .Should().BeFalse("the source set is not known yet — this is waiting, not declining");

        // …and the moment the watcher seeds it — even with the EMPTY map, which is a real answer —
        // the re-drive is due.
        var seededEmpty = unseeded with { CurrentSourceVersions = ImmutableDictionary<string, long>.Empty };
        NodeTypeCompilationHelpers.HasStaleFailureVerdict(seededEmpty, null)
            .Should().BeTrue("an established (even empty) source set is an answer, so the attempt is due");
    }

    // ── …and nowhere else ───────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData(CompilationStatus.Ok)]
    [InlineData(CompilationStatus.Pending)]
    [InlineData(CompilationStatus.Compiling)]
    public void OnlySettledFailures_AreReDriven(CompilationStatus? status)
    {
        var def = NeverCompiledFailure() with { CompilationStatus = status };
        NodeTypeCompilationHelpers.HasStaleFailureVerdict(def, null)
            .Should().BeFalse("re-driving a healthy or in-flight type is a recompile storm, not a recovery");
    }

    /// <summary>
    /// The predicate is the strict COMPLEMENT of the framework-stale twin: a type carrying
    /// assembly coordinates is that twin's business (and the enrichment self-heal's), and having
    /// two kickoffs claim the same state is how a rebuild loop starts.
    /// </summary>
    [Fact]
    public void AFailureLayeredOnAPriorGoodBuild_IsLeftToTheFrameworkStaleKickoff()
    {
        var def = NeverCompiledFailure() with
        {
            LatestAssemblyCollection = "assemblies",
            LatestAssemblyPath = "P_T/v7.dll",
            CompiledFrameworkVersion = "0ldfr4me",
        };

        NodeTypeCompilationHelpers.HasStaleFailureVerdict(def, null)
            .Should().BeFalse("coordinates exist, so this is the framework-stale kickoff's case");
        NodeTypeCompilationHelpers.HasStaleFrameworkBuild(def, modulesHash: null)
            .Should().BeTrue("…and that kickoff does cover it");
    }

    // ── The token ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheToken_IsOrderInsensitive_AndSeparatesUnseededFromEmpty()
    {
        var a = NodeTypeCompilationHelpers.BuildInputsToken(
            "m", Sources(("b", 2), ("a", 1)));
        var b = NodeTypeCompilationHelpers.BuildInputsToken(
            "m", Sources(("a", 1), ("b", 2)));
        a.Should().Be(b, "dictionary enumeration order must never decide whether a type is re-driven");

        NodeTypeCompilationHelpers.BuildInputsToken("m", null)
            .Should().NotBe(NodeTypeCompilationHelpers.BuildInputsToken(
                "m", ImmutableDictionary<string, long>.Empty),
                "'the sources watcher has not seeded yet' and 'this type has no sources' are "
                + "different facts — collapsing them would let an unseeded node read as source-less");

        a.Should().Contain(NodeTypeCompilationHelpers.FrameworkVersion,
            "an operator reading a stuck node must be able to see WHICH framework formed the verdict");
    }

    // ── The stamps stay truthful ────────────────────────────────────────────────────────────

    [Fact]
    public void ApplyCompileFailure_StampsTheInputsTheVerdictWasFormedFrom()
    {
        var consumed = ImmutableDictionary<string, long>.Empty.Add("P/T/Source/a", 5);
        var def = new NodeTypeDefinition
        {
            Configuration = "config => config",
            CurrentSourceVersions = Sources(("P/T/Source/a", 5)),
        };

        var stamped = NodeTypeCompilationHelpers.ApplyCompileFailure(
            def, new NodeCompilationResult(null, [], CompiledSources: consumed),
            new CompilationException(TypePath, "CS0103 …"), activityPath: null, modulesHash: "mod-1");

        stamped.FailedBuildInputs.Should().Be(
            NodeTypeCompilationHelpers.BuildInputsToken("mod-1", consumed));
        NodeTypeCompilationHelpers.HasStaleFailureVerdict(stamped, "mod-1")
            .Should().BeFalse("the failure just had its attempt under exactly these inputs");
    }

    /// <summary>The consumed set may differ from the node's live snapshot when an edit lands
    /// mid-compile; the stamp must describe what the compile actually saw, so the edit still
    /// earns its own attempt.</summary>
    [Fact]
    public void ApplyCompileFailure_FallsBackToTheLiveSnapshot_WhenTheResultResolvedNone()
    {
        var live = Sources(("P/T/Source/a", 5));
        var def = new NodeTypeDefinition { Configuration = "c", CurrentSourceVersions = live };

        var stamped = NodeTypeCompilationHelpers.ApplyCompileFailure(
            def, result: null, error: new InvalidOperationException("boom"),
            activityPath: null, modulesHash: null);

        stamped.FailedBuildInputs.Should().Be(NodeTypeCompilationHelpers.BuildInputsToken(null, live));
    }

    [Fact]
    public void ApplyCompileSuccess_ClearsTheFailureVerdict()
    {
        var def = NeverCompiledFailure(
            failedInputs: NodeTypeCompilationHelpers.BuildInputsToken(null, null));

        var stamped = NodeTypeCompilationHelpers.ApplyCompileSuccess(
            def,
            new NodeCompilationResult("/cache/T.dll", [], Collection: "assemblies",
                ContentPath: "P_T/v1.dll", Version: 1),
            currentNodeVersion: 1, activityPath: null, releasePath: null);

        stamped.FailedBuildInputs.Should().BeNull(
            "a stale token would make a LATER failure look like it had already had its attempt, "
            + "and the type would sit broken with nothing due to retry it");
    }

    // ── The bound, and its loud failure mode ────────────────────────────────────────────────

    /// <summary>
    /// The ledger is what turns non-convergence from silent into loud: re-driving the SAME inputs
    /// twice can only happen if the stamp did not stick, and the caller logs an ERROR naming the
    /// path on exactly that count.
    /// </summary>
    [Fact]
    public void TheLedger_CountsPerInputs_AndPerType()
    {
        var registry = new NodeTypeCompileParkRegistry();
        var first = NodeTypeCompilationHelpers.BuildInputsToken(null, Sources(("a", 1)));
        var second = NodeTypeCompilationHelpers.BuildInputsToken(null, Sources(("a", 2)));

        registry.RecordFailureRedrive(TypePath, first).Should().Be((1, 1));
        registry.RecordFailureRedrive(TypePath, second).Should().Be((1, 2),
            "a different input set is a legitimate fresh attempt, not a repeat");
        registry.RecordFailureRedrive(TypePath, first).Should().Be((2, 3),
            "re-driving the SAME inputs twice is the non-convergence signal");
        registry.GetFailureRedriveCount(TypePath).Should().Be(3);
        registry.GetFailureRedriveCount("P/Other").Should().Be(0,
            "the ledger is per type — one broken type must not spend another's budget");
    }

    [Fact]
    public void TheBudget_IsReturnedOnSuccess_AndOnADeliberateRetry()
    {
        var registry = new NodeTypeCompileParkRegistry();
        var inputs = NodeTypeCompilationHelpers.BuildInputsToken(null, null);

        for (var i = 0; i < NodeTypeCompileParkRegistry.MaxAutomaticFailureRedrives; i++)
            registry.RecordFailureRedrive(TypePath, inputs);
        registry.GetFailureRedriveCount(TypePath)
            .Should().Be(NodeTypeCompileParkRegistry.MaxAutomaticFailureRedrives);

        registry.OnCompileSucceeded(TypePath);
        registry.GetFailureRedriveCount(TypePath).Should().Be(0,
            "the re-drive converged, so a type that breaks again later starts with a full budget");

        for (var i = 0; i < NodeTypeCompileParkRegistry.MaxAutomaticFailureRedrives; i++)
            registry.RecordFailureRedrive(TypePath, inputs);
        registry.ResetFailureRedrives(TypePath);
        registry.GetFailureRedriveCount(TypePath).Should().Be(0,
            "a human asking for a build is the strongest signal that a give-up should be reconsidered");
    }

    /// <summary>The give-up bound is a real cap, not a hint — the caller stops at it, so a broken
    /// type cannot drive an unbounded sequence of Roslyn passes on the hub's action block.</summary>
    [Fact]
    public void TheBudget_IsFinite()
    {
        var registry = new NodeTypeCompileParkRegistry();
        var overBudget = 0;
        for (var i = 0; i < NodeTypeCompileParkRegistry.MaxAutomaticFailureRedrives + 3; i++)
        {
            // Each attempt uses DIFFERENT inputs — the structural bound cannot help here, so only
            // the cap stands between a churning source set and an unbounded re-drive.
            var (_, total) = registry.RecordFailureRedrive(
                TypePath, NodeTypeCompilationHelpers.BuildInputsToken(null, Sources(("a", i))));
            if (total > NodeTypeCompileParkRegistry.MaxAutomaticFailureRedrives)
                overBudget++;
        }
        overBudget.Should().Be(3, "past the cap every further attempt must report as over budget");
    }

    // ── The operational-member contract ─────────────────────────────────────────────────────

    /// <summary>
    /// The stamp is MESH-owned bookkeeping: export must strip it and an upsert must preserve the
    /// live node's value. An authored token matching the importing deployment's live inputs would
    /// suppress the one automatic retry this whole mechanism exists to grant.
    /// </summary>
    [Fact]
    public void TheStamp_IsOperationalState()
    {
        // Contains(), not the collection assertion: the set is OrdinalIgnoreCase and the member
        // is listed camelCased, so an equality-based collection check would miss it.
        NodeTypeOperationalContent.MemberNames
            .Contains(nameof(NodeTypeDefinition.FailedBuildInputs))
            .Should().BeTrue(
                "an authored failedBuildInputs would suppress the retry it is supposed to enable");

        var state = NodeTypeCompileState.FromDefinition(
            NeverCompiledFailure(failedInputs: "fw=x;mod=(none);src=0"));
        state!.FailedBuildInputs.Should().Be("fw=x;mod=(none);src=0",
            "the compile-state satellite must carry every masked member or it silently drops it");
        state.IsEmpty.Should().BeFalse();
        NodeTypeCompileState.FromDefinition(new NodeTypeDefinition())!.IsEmpty
            .Should().BeTrue("a definition with no compile state at all is still empty");
    }

    private static string TokenSourcePart(string? modulesHash, IReadOnlyDictionary<string, long>? sources)
    {
        var token = NodeTypeCompilationHelpers.BuildInputsToken(modulesHash, sources);
        return token[(token.IndexOf(";src=", StringComparison.Ordinal) + ";src=".Length)..];
    }
}
