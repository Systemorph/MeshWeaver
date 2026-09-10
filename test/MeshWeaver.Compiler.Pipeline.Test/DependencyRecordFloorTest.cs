#pragma warning disable CS1591

using System.Collections.Immutable;
using MeshWeaver.Compiler;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Pins the MODULE FLOOR (#3934): a dependency record's module entry says "I need at least X",
/// not "I need exactly this build".
///
/// <para><b>The measured defect.</b> <c>CreateIdResolver</c> resolved a module as
/// <c>mvid:&lt;guid&gt;</c> — an exact build — and an MVID moves on every compilation BY
/// CONSTRUCTION, including a rebuild of identical source from a different absolute path. So a
/// publication composed from more than one Roslyn workspace carried two builds of one module name,
/// and every NodeType binding it was declined at adoption on both replicas, each recompiled
/// locally into <c>latestAssemblyCollection: "local"</c>, and each invalidated the other. Measured
/// on memex 2026-09-10: <c>SocialMedia/Post</c> lost <c>Preview</c>, <c>Write</c> and
/// <c>PostCard</c>, <c>/Posts/SavThankYou</c> returned HTTP 200 rendering nothing all day, and the
/// NodeType's own record read <c>compilationStatus: Ok</c> throughout.</para>
///
/// <para><b>Both directions are controlled here, deliberately.</b> A test that only proves the new
/// acceptance is not a control — "accept everything" would pass it. Every relaxation test below is
/// paired with the falsification arm that must still DECLINE, and the four things the floor
/// deliberately does NOT relax (the toolchain proxy, the content key, platform surfaces, and an
/// absent dependency) each have their own arm.</para>
/// </summary>
public class DependencyRecordFloorTest
{
    private const string Toolchain = "mvid:toolchain-1";
    private const string Collab = "MeshWeaver.Markdown.Collaboration";

    private static Func<string, string?> Resolver(params (string Name, string Id)[] pairs)
        => name => pairs.FirstOrDefault(p => p.Name == name).Id;

    private static ImmutableSortedDictionary<string, string> RecordBinding(
        string name, string id, string toolchain = Toolchain, string? digest = null)
        => CompiledDependencies.Compute([name], Resolver((name, id)), toolchain, digest);

    // ── The resolver: what a module's id IS now ─────────────────────────────────────────────────

    [Fact]
    public void IdResolver_AModuleWithAVersionResolvesAFloor_WithoutOneItStillPinsTheBuild()
    {
        var resolver = CompiledDependencies.CreateIdResolver(
            surfaceByName: new Dictionary<string, string>(),
            moduleMvidByName: new Dictionary<string, string>
            {
                [Collab] = "798f92a0",
                ["Legacy.Module"] = "deadbeef",
            },
            implMvidOf: _ => null,
            moduleVersionOf: name => name == Collab ? "3.0.0" : null);

        resolver(Collab).Should().Be("min:3.0.0",
            "a module that states a version records a FLOOR over it");
        resolver("Legacy.Module").Should().Be("mvid:deadbeef",
            "a module that states no version has no floor to offer, so the exact pin stays — "
            + "inconclusive takes the rebuild side");
    }

    [Fact]
    public void IdResolver_TheThreeArgumentOverloadIsTheNoVersionCase()
    {
        // The pre-#3934 overload is not a second notion of a module id: it is this one with a
        // version resolver that answers nothing. A caller that cannot state a version must not
        // silently acquire a floor.
        var legacy = CompiledDependencies.CreateIdResolver(
            surfaceByName: new Dictionary<string, string>(),
            moduleMvidByName: new Dictionary<string, string> { [Collab] = "798f92a0" },
            implMvidOf: _ => null);

        legacy(Collab).Should().Be("mvid:798f92a0");
    }

    // ── THE RELAXATION, and its falsification arm ───────────────────────────────────────────────

    [Fact]
    public void AMovedBuildAtTheSameVersion_NowAdopts_WhereTheMvidPinDeclined()
    {
        // THE SavThankYou SHAPE, exactly: one module name, two builds from two workspaces of one
        // publication, therefore the same version and different MVIDs.
        var pinned = RecordBinding(Collab, "mvid:798f92a0");
        CompiledDependencies.FindMismatch(pinned, Resolver((Collab, "mvid:825581836a")), Toolchain)
            .Should().Be($"'{Collab}' built against mvid:798f92a0, live is mvid:825581836a",
                "the CONTROL that the old rule declined this — without it the test below proves "
                + "nothing about what changed");

        var floored = RecordBinding(Collab, "min:3.0.0");
        CompiledDependencies.Validate(floored, Resolver((Collab, "min:3.0.0")), Toolchain)
            .Should().Be(DependencyRecordOutcome.Satisfied,
                "two builds of one module assembly link fine — AssemblyVersion is synchronised "
                + "fleet-wide, so Assembly.LoadFrom returns the already-loaded copy");
    }

    [Fact]
    public void AHigherLiveVersionSatisfiesTheFloor_AndOrdersNumerically()
    {
        var floored = RecordBinding(Collab, "min:3.0.0-ci.900");

        CompiledDependencies.FindMismatch(floored, Resolver((Collab, "min:3.0.0-ci.3758")), Toolchain)
            .Should().BeNull("ci.3758 is ABOVE ci.900 — SemVer, never string order");
        CompiledDependencies.FindMismatch(floored, Resolver((Collab, "min:3.0.0")), Toolchain)
            .Should().BeNull("a clean version outranks a prerelease of the same core");
        CompiledDependencies.FindMismatch(floored, Resolver((Collab, "min:3.0.0+abc123")), Toolchain)
            .Should().BeNull("build metadata is ignored for ordering, per SemVer");
    }

    /// <summary>
    /// 🚨 THE POSITIVE CONTROL. If this passed with the floor removed — i.e. if any module entry
    /// were accepted — the whole change would be "adopt everything", which is a different and much
    /// worse bug than the one it replaces.
    /// </summary>
    [Fact]
    public void ARecordBelowItsFloor_StillDeclines_AndTheSentenceNamesBothSides()
    {
        var floored = RecordBinding(Collab, "min:1.6.0");
        var outcome = CompiledDependencies.Validate(
            floored, Resolver((Collab, "min:1.5.0")), Toolchain);

        outcome.Status.Should().Be(DependencyRecordStatus.FloorNotMet);
        outcome.Entry.Should().Be(Collab);
        outcome.Problem.Should().Be(
            $"'{Collab}' needs at least 1.6.0 — this environment has 1.5.0, below the recorded floor");
        outcome.IsSatisfied.Should().BeFalse();
        CompiledDependencies.FindMismatch(floored, Resolver((Collab, "min:1.5.0")), Toolchain)
            .Should().Be(outcome.Problem, "the string API is a projection of the outcome, never a "
                + "second implementation that could disagree with it");
    }

    [Fact]
    public void AMajorVersionBelowTheFloorDeclines_SoTheOrderIsRealAndNotAPrefixMatch()
    {
        var floored = RecordBinding(Collab, "min:3.0.0");
        CompiledDependencies.Validate(floored, Resolver((Collab, "min:2.9.9")), Toolchain)
            .Status.Should().Be(DependencyRecordStatus.FloorNotMet);
    }

    // ── Nothing was checked is NEVER a clean bill ───────────────────────────────────────────────

    [Fact]
    public void AFloorAgainstAnEnvironmentThatReportsAnMvid_IsNotChecked_NotSatisfied()
    {
        var floored = RecordBinding(Collab, "min:3.0.0");
        var outcome = CompiledDependencies.Validate(
            floored, Resolver((Collab, "mvid:798f92a0")), Toolchain);

        outcome.Status.Should().Be(DependencyRecordStatus.NotChecked);
        outcome.IsSatisfied.Should().BeFalse("a status that established nothing must never read as "
            + "compatible — the whole reason the outcome carries a status at all");
        outcome.Problem.Should().Be(
            $"'{Collab}' recorded min:3.0.0 and this environment reports mvid:798f92a0 — the two "
            + "cannot be compared, so nothing was checked and the build is not adopted");
    }

    [Fact]
    public void ALegacyExactPinAgainstAVersionedEnvironment_IsAlsoNotChecked()
    {
        // The other direction, which is what every record stamped before #3934 hits once. It takes
        // the rebuild side, exactly like an inconclusive content key.
        var pinned = RecordBinding(Collab, "mvid:798f92a0");
        CompiledDependencies.Validate(pinned, Resolver((Collab, "min:3.0.0")), Toolchain)
            .Status.Should().Be(DependencyRecordStatus.NotChecked);
    }

    [Fact]
    public void AModuleTheEnvironmentDoesNotHaveAtAll_IsDrift_NotAnUnmetFloor()
    {
        // 'absent' is a measured fact about this environment, not an incomparable scheme: the
        // build binds something that is not here.
        var floored = RecordBinding(Collab, "min:3.0.0");
        var outcome = CompiledDependencies.Validate(floored, Resolver(), Toolchain);

        outcome.Status.Should().Be(DependencyRecordStatus.Drifted,
            "'nothing resolves this name here' is a measured fact with a definite answer, not an "
            + "inability to compare — a floor must not launder it into 'nothing was checked'");
        outcome.Problem.Should().Be(
            $"'{Collab}' built against min:3.0.0, live is {CompiledDependencies.AbsentId}");
    }

    [Fact]
    public void AnUntrustedRecordIsNotCheckedRatherThanDrifted()
    {
        var handAssembled = ImmutableSortedDictionary<string, string>.Empty
            .Add(Collab, "min:3.0.0");
        var outcome = CompiledDependencies.Validate(
            handAssembled, Resolver((Collab, "min:3.0.0")), Toolchain);

        outcome.Status.Should().Be(DependencyRecordStatus.NotChecked);
        outcome.Entry.Should().BeNull("the whole record is untrusted, not one entry of it");
        outcome.Problem.Should().Contain(CompiledDependencies.ToolchainKey);
    }

    // ── WHAT THE FLOOR DOES NOT RELAX ───────────────────────────────────────────────────────────

    [Fact]
    public void TheToolchainProxyIsStillExact()
    {
        var record = RecordBinding(Collab, "min:3.0.0");
        var outcome = CompiledDependencies.Validate(
            record, Resolver((Collab, "min:9.9.9")), "mvid:toolchain-2");

        outcome.Status.Should().Be(DependencyRecordStatus.Drifted);
        outcome.Entry.Should().Be(CompiledDependencies.ToolchainKey,
            "a module floor met with room to spare does not license a toolchain change");
    }

    [Fact]
    public void APlatformSurfaceIsStillExact_AFloorNeverAppliesToRefAsmIds()
    {
        var record = RecordBinding("MeshWeaver.Layout", "ref:aaa");
        CompiledDependencies.Validate(record, Resolver(("MeshWeaver.Layout", "ref:bbb")), Toolchain)
            .Status.Should().Be(DependencyRecordStatus.Drifted);

        // …and the scheme prefixes still never compare across lanes, floor or not.
        CompiledDependencies.Satisfies("ref:aaa", "min:9.9.9").Should().BeFalse();
        CompiledDependencies.Satisfies("min:1.0.0", "ref:aaa").Should().BeFalse();
        CompiledDependencies.Satisfies("mvid:a", "min:9.9.9").Should().BeFalse();
    }

    [Fact]
    public void TheContentKeyIsStillExact()
    {
        var record = CompiledDependencies.Compute(
            [Collab], Resolver((Collab, "min:3.0.0")), Toolchain, "g-input-1");
        record.Should().ContainKey(CompiledDependencies.ContentKey);

        CompiledDependencies.FindMismatch(
                record, Resolver((Collab, "min:3.0.0")), Toolchain, "i-something-else")
            .Should().Contain(CompiledDependencies.ContentKey,
                "a regenerated input that hashes differently is a direct observation, and no "
                + "version floor can excuse it");
    }

    // ── The content key keeps its meaning under floors ──────────────────────────────────────────

    [Fact]
    public void LiveContentKeyOf_ReproducesTheStampedKey_WhenAModuleMovedABOVEItsFloor()
    {
        // The demotion the content key licenses (#1976) must survive the floor. Folding the LIVE
        // value verbatim would move the key on every module rebuild — reintroducing exactly the
        // invalidation #3934 removes, one layer down.
        var record = CompiledDependencies.Compute(
            [Collab], Resolver((Collab, "min:3.0.0")), Toolchain, "g-input-1");
        var stamped = record[CompiledDependencies.ContentKey];

        CompiledDependencies.LiveContentKeyOf(
                record, Resolver((Collab, "min:3.0.1")), "g-input-1")
            .Should().Be(stamped, "the entry still HOLDS, so the key must still reproduce");

        CompiledDependencies.FindMismatchAfterReevaluation(
                record, Resolver((Collab, "min:3.0.1")), "mvid:toolchain-LATER",
                CompiledDependencies.LiveContentKeyOf(
                    record, Resolver((Collab, "min:3.0.1")), "g-input-1"))
            .Should().BeNull("the regenerated input proved the toolchain move irrelevant, and the "
                + "module is at or above its floor");
    }

    [Fact]
    public void LiveContentKeyOf_DoesNotReproduceIt_WhenTheModuleIsBELOWTheFloor()
    {
        var record = CompiledDependencies.Compute(
            [Collab], Resolver((Collab, "min:3.0.0")), Toolchain, "g-input-1");
        var stamped = record[CompiledDependencies.ContentKey];

        CompiledDependencies.LiveContentKeyOf(
                record, Resolver((Collab, "min:2.0.0")), "g-input-1")
            .Should().NotBe(stamped, "a non-satisfying entry folds its LIVE value, so nothing is "
                + "demoted and the toolchain entry keeps deciding");

        CompiledDependencies.FindMismatchAfterReevaluation(
                record, Resolver((Collab, "min:2.0.0")), "mvid:toolchain-LATER",
                CompiledDependencies.LiveContentKeyOf(
                    record, Resolver((Collab, "min:2.0.0")), "g-input-1"))
            .Should().NotBeNull();
    }

    // ── The adopt-time judges every reader goes through ─────────────────────────────────────────

    private static NodeTypeDefinition UsableDef(ImmutableSortedDictionary<string, string> record)
        => new()
        {
            LatestAssemblyCollection = "prebuilt",
            LatestAssemblyPath = "x/v1-abc.dll",
            CompiledFrameworkVersion = NodeTypeCompilationHelpers.FrameworkVersion,
            CompiledDependencies = record,
            LastCompiledVersion = 1,
        };

    private static readonly MeshNode Node = new("SocialMedia/Post", "Post");

    [Fact]
    public void EveryReaderHonoursTheFloor_AndEveryReaderStillDeclinesBelowIt()
    {
        var record = RecordBinding(Collab, "min:1.6.0");
        var above = new NodeTypeCompilationHelpers.BuildGuards(
            ModulesHash: "irrelevant-once-a-record-exists",
            DependencyIdOf: Resolver((Collab, "min:1.7.0")),
            ToolchainId: Toolchain);
        var below = above with { DependencyIdOf = Resolver((Collab, "min:1.5.0")) };

        // HasUsableBuild + its stale twin — the pair the per-instance activation reads.
        NodeTypeCompilationHelpers.HasUsableBuild(Node, UsableDef(record), above)
            .Should().BeTrue("a module ABOVE the floor is not a reason to rebuild");
        NodeTypeCompilationHelpers.HasStaleFrameworkBuild(UsableDef(record), above)
            .Should().BeFalse();

        NodeTypeCompilationHelpers.HasUsableBuild(Node, UsableDef(record), below)
            .Should().BeFalse("a module BELOW the floor must still decline");
        NodeTypeCompilationHelpers.HasStaleFrameworkBuild(UsableDef(record), below)
            .Should().BeTrue("the stale twin must re-drive the compile for the same condition");

        // The bake probe / sweep — the same record, the same answer.
        NodeTypeBakeStatus.Classify(
                UsableDef(record), storeHasBytes: true,
                NodeTypeCompilationHelpers.FrameworkVersion,
                above.DependencyIdOf, Toolchain)
            .Should().Be(BakeState.Baked);
        NodeTypeBakeStatus.Classify(
                UsableDef(record), storeHasBytes: true,
                NodeTypeCompilationHelpers.FrameworkVersion,
                below.DependencyIdOf, Toolchain)
            .Should().Be(BakeState.DependencyStale);
    }

    [Fact]
    public void TheBakeProbeReportsTheFloorSentence_NotAFixedOne()
    {
        var record = RecordBinding(Collab, "min:1.6.0");
        var (state, mismatch) = NodeTypeBakeStatus.ClassifyDetailed(
            UsableDef(record), storeHasBytes: true,
            NodeTypeCompilationHelpers.FrameworkVersion,
            Resolver((Collab, "min:1.5.0")), Toolchain);

        state.Should().Be(BakeState.DependencyStale);
        mismatch.Should().Be(
            $"'{Collab}' needs at least 1.6.0 — this environment has 1.5.0, below the recorded floor");
    }
}
