using System.Collections.Generic;
using System.Collections.Immutable;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// 🚨 Systemorph/Memex#668 — a stale-but-serving build is BOUNDED. Measured on
/// memex.systemorph.com: <c>Hosting/InstanceAction</c> served an adopted build at module version
/// 1.29.7 over source at 1.56 — twenty-seven MINOR versions, across a change to how operations are
/// signed — because <see cref="ModuleVersionCompatibility"/> only refused a MAJOR bump. Every
/// dispatch the old build signed failed verification downstream, and nothing said how old the
/// serving build was. <c>Hosting/Deployment</c> sat the same way on control (adopted 1.57.0 over
/// 1.59, a different source set).
///
/// <para>Each refusal test carries its own NEGATIVE CONTROL: the same record judged with the bound
/// disabled lands on the pre-#668 answer (StaleAdopted, coordinates kept, execution permitted), so
/// the test cannot pass on a judgement that never consults the bound.</para>
/// </summary>
public class StaleAdoptionBoundTest
{
    private static readonly ImmutableDictionary<string, long> LiveSources =
        ImmutableDictionary.CreateRange(new Dictionary<string, long>
        {
            ["Hosting/InstanceAction/Source/ActionsExecutor"] = 638_000_000_000_000_000,
        });

    private const string ServedAssembly = "Hosting_InstanceAction/v18926-c003e001-0123456789ab.dll";

    /// <summary>The incident's record: an adopted build on the coordinates, the source moved past
    /// it, both versions recorded.</summary>
    private static NodeTypeDefinition Adopted(string adoptedVersion, string currentVersion) =>
        new()
        {
            CompilationStatus = CompilationStatus.Ok,
            LatestAssemblyCollection = "local",
            LatestAssemblyPath = ServedAssembly,
            LatestAssemblyMvid = "976b32b0088249ebbd95aed0dbf13e47",
            CompiledFrameworkVersion = NodeTypeCompilationHelpers.FrameworkVersion,
            AdoptedSourceFingerprint = "09de01b58389cb19",
            CurrentSourceFingerprint = "32492a2ffe2d261f",
            AdoptedModuleVersion = adoptedVersion,
            CurrentModuleVersion = currentVersion,
            CurrentSourceVersions = LiveSources,
            CompiledSources = LiveSources,
            BuildProvenance = BuildProvenance.AdoptedVerified,
            RequestedSourceStampAt = System.DateTimeOffset.UtcNow,
        };

    // ── The distance ────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("1.29.7", "1.56", 27)]
    [InlineData("1.57.0", "1.59", 2)]
    [InlineData("v1.9.0", "1.10.0-ci.12", 1)]
    [InlineData("1.3", "1.3.9", 0)]
    [InlineData("1.5.0", "1.4.0", -1)]
    [InlineData("2", "2.4", 4)]
    public void MinorVersionsBehind_IsTheMinorDistanceWithinOneMajor(string adopted, string current, int expected)
        => ModuleVersionCompatibility.MinorVersionsBehind(adopted, current).Should().Be(expected);

    [Theory]
    [InlineData("1.29.7", "2.0")]   // a MAJOR bump is the Incompatible rule's question, not this one
    [InlineData(null, "1.56")]
    [InlineData("1.29.7", null)]
    [InlineData("221c6c286785ddf2", "1.10")] // a content hash is not a version
    [InlineData("1.x", "1.56")]
    public void MinorVersionsBehind_IsNotMeasurable_WhenEitherSideIsUnknown(string? adopted, string? current)
        => ModuleVersionCompatibility.MinorVersionsBehind(adopted, current).Should().BeNull(
            "an unmeasured distance must never refuse — the INCONCLUSIVE rule the MAJOR check follows");

    [Theory]
    [InlineData("1.29.7", "1.56", 5, true)]
    [InlineData("1.54.0", "1.59", 5, false)]
    [InlineData("1.53.0", "1.59", 5, true)]
    [InlineData("1.29.7", "1.56", -1, false)] // disabled
    [InlineData(null, "1.56", 0, false)]      // unknown never refuses
    public void Exceeds_RefusesOnlyAMeasuredDistancePastAnEnabledBound(
        string? adopted, string current, int bound, bool expected)
        => StaleAdoptionBound.Exceeds(adopted, current, bound).Should().Be(expected);

    [Fact]
    public void TheBound_IsReadFromConfiguration_AndDefaultsWhenUnset()
    {
        StaleAdoptionBound.MaxMinorVersionsBehind(null)
            .Should().Be(StaleAdoptionBound.DefaultMaxMinorVersionsBehind);
        var configured = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [StaleAdoptionBound.ConfigKey] = "12" })
            .Build();
        StaleAdoptionBound.MaxMinorVersionsBehind(configured).Should().Be(12);
        var garbage = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [StaleAdoptionBound.ConfigKey] = "lots" })
            .Build();
        StaleAdoptionBound.MaxMinorVersionsBehind(garbage)
            .Should().Be(StaleAdoptionBound.DefaultMaxMinorVersionsBehind, "an unreadable value is not a disable");
    }

    // ── The owner's judgement — the incident ────────────────────────────────────────────────

    [Fact]
    public void TheIncident_OnAMeshThatCompiles_RefusesTheOldBuild_AndClearsItsCoordinates()
    {
        var record = Adopted("1.29.7", "1.56");

        var result = NodeTypeCompilationHelpers.ApplyAdoptedSourceStamp(
            record, LiveSources, canCompileLocally: true, maxMinorVersionsBehind: 5);

        result.BuildProvenance.Should().Be(BuildProvenance.AdoptionRefused,
            "27 minor versions behind is past the bound — the build is refused, not kept serving");
        result.LatestAssemblyPath.Should().BeNull(
            "a refused build stops serving: if the compile of the live source then fails, the type has "
            + "NO build — which is the point, an arbitrarily old program must not sign operations");
        result.CompilationStatus.Should().Be(CompilationStatus.Pending, "the live source is compiled");
        result.CompilationError.Should().Contain("Memex#668").And.Contain("27 minor version(s) behind")
            .And.Contain("1.29.7").And.Contain("1.56");

        // ── Negative control: the SAME record with the bound disabled is the pre-#668 answer. ──
        var unbounded = NodeTypeCompilationHelpers.ApplyAdoptedSourceStamp(
            record, LiveSources, canCompileLocally: true, maxMinorVersionsBehind: -1);
        unbounded.BuildProvenance.Should().Be(BuildProvenance.StaleAdopted);
        unbounded.LatestAssemblyPath.Should().Be(ServedAssembly,
            "without the bound the 27-minor-old build keeps serving — the defect");
    }

    [Fact]
    public void TheIncident_OnAMeshThatCannotCompile_IsUnavailable_NamedByTheBound_AndNotRun()
    {
        var record = Adopted("1.29.7", "1.56");

        var result = NodeTypeCompilationHelpers.ApplyAdoptedSourceStamp(
            record, LiveSources, canCompileLocally: false, maxMinorVersionsBehind: 5);

        result.BuildProvenance.Should().Be(BuildProvenance.AdoptionRefused);
        result.CompilationStatus.Should().Be(CompilationStatus.Unavailable,
            "nothing is known to be wrong with the source; it is the build that is too old — not Error");
        result.CompilationError.Should().StartWith("Build too far behind its source")
            .And.Contain(StaleAdoptionBound.ConfigKey);
        NodeTypeExecutionGate.Evaluate(result).Should().NotBe(BuildExecutionVerdict.Permitted,
            "the coordinates are kept on such a mesh, so the execute-time gate is what stops the bytes");

        var unbounded = NodeTypeCompilationHelpers.ApplyAdoptedSourceStamp(
            record, LiveSources, canCompileLocally: false, maxMinorVersionsBehind: -1);
        NodeTypeExecutionGate.Evaluate(unbounded).Should().Be(BuildExecutionVerdict.Permitted,
            "negative control: unbounded, the old build runs");
    }

    [Fact]
    public void WithinTheBound_TheBuildKeepsServing_AsBefore()
    {
        // The control-instance reading: Hosting/Deployment adopted 1.57.0 over 1.59.
        var result = NodeTypeCompilationHelpers.ApplyAdoptedSourceStamp(
            Adopted("1.57.0", "1.59"), LiveSources, canCompileLocally: false, maxMinorVersionsBehind: 5);

        result.BuildProvenance.Should().Be(BuildProvenance.StaleAdopted);
        result.CompilationStatus.Should().Be(CompilationStatus.Ok);
        result.LatestAssemblyPath.Should().Be(ServedAssembly);
    }

    [Fact]
    public void TheDefaultBound_IsTheProductionRule_ForAPureCaller()
    {
        var result = NodeTypeCompilationHelpers.ApplyAdoptedSourceStamp(
            Adopted("1.29.7", "1.56"), LiveSources, canCompileLocally: false);
        result.BuildProvenance.Should().Be(BuildProvenance.AdoptionRefused,
            "omitting the bound must not mean 'unbounded'");
    }

    // ── The delivery-gate settle ────────────────────────────────────────────────────────────

    [Fact]
    public void ADeliveryGate_DoesNotKeepAnOldBuildServing_PastTheBound()
    {
        var pending = Adopted("1.29.7", "1.56") with { CompilationStatus = CompilationStatus.Pending };

        var settled = BuildDeliveryHold.Settle(pending, hasUsableBuild: true, "no bundle for this identity", 5);

        settled.Should().NotBeNull();
        settled!.CompilationStatus.Should().Be(CompilationStatus.Unavailable);
        settled.BuildProvenance.Should().Be(BuildProvenance.AdoptionRefused);
        settled.CompilationError.Should().Contain("Memex#668").And.Contain("no bundle for this identity");

        // Re-settling the refused record keeps naming the BOUND, not a MAJOR bump.
        var again = BuildDeliveryHold.Settle(settled with { CompilationStatus = CompilationStatus.Pending },
            hasUsableBuild: true, "still no bundle", 5);
        again!.CompilationError.Should().StartWith("Build too far behind its source");

        var unbounded = BuildDeliveryHold.Settle(pending, hasUsableBuild: true, "no bundle for this identity", -1);
        unbounded!.BuildProvenance.Should().Be(BuildProvenance.StaleAdopted,
            "negative control: unbounded, the gate keeps the old build serving");
    }

    [Fact]
    public void ARefusalOnTheBound_IsItsOwnDeliveryEvent_NeverTheMajorBumpOne()
    {
        var before = Adopted("1.29.7", "1.56") with { CompilationStatus = CompilationStatus.Pending };
        var settled = BuildDeliveryHold.Settle(before, hasUsableBuild: true, "no bundle", 5)!;

        BuildDeliveryHold.EventOf(before, settled, 5).Should().Be(
            BuildDeliveryHold.DeliveryEvent.HeldTooFarBehind,
            "a same-MAJOR build past the bound was not refused for a MAJOR bump — the notification "
            + "must not say it was");

        // Control: a real MAJOR bump on the same path stays HeldIncompatible.
        var major = Adopted("1.29.7", "2.0") with
        {
            CompilationStatus = CompilationStatus.Unavailable,
            BuildProvenance = BuildProvenance.AdoptionRefused,
        };
        BuildDeliveryHold.EventOf(before, major, 5).Should().Be(BuildDeliveryHold.DeliveryEvent.HeldIncompatible);
    }
    [Fact]
    public void AServingBuildThatTheSourceOutrunsPastTheBound_IsAnnounced_EvenThoughBothStatesAreHolds()
    {
        // 5 minors behind: within the bound, serving (held: StaleAdopted).
        var serving = Adopted("1.50.0", "1.55") with
        {
            CompilationStatus = CompilationStatus.Pending,
            BuildProvenance = BuildProvenance.StaleAdopted,
        };
        BuildDeliveryHold.Settle(serving, hasUsableBuild: true, "no bundle", 5)!
            .BuildProvenance.Should().Be(BuildProvenance.StaleAdopted, "the precondition: within the bound");

        // The source advances to 6 behind: the next judgement refuses — the type STOPS running.
        var outrun = serving with { CurrentModuleVersion = "1.56" };
        var refused = BuildDeliveryHold.Settle(outrun, hasUsableBuild: true, "no bundle", 5)!;
        refused.BuildProvenance.Should().Be(BuildProvenance.AdoptionRefused);
        BuildDeliveryHold.EventOf(outrun, refused, 5).Should().Be(
            BuildDeliveryHold.DeliveryEvent.HeldTooFarBehind,
            "a serving type stopping is news, though StaleAdopted and AdoptionRefused are both holds");

        // Control: re-settling the same hold is no transition and says nothing.
        BuildDeliveryHold.EventOf(refused, refused, 5).Should().BeNull();
    }

    [Fact]
    public void ARefusalOnTheBound_Recovers_WhenTheBoundIsLifted_AndIsNeverRelabelledAMajorBump()
    {
        var refused = BuildDeliveryHold.Settle(
            Adopted("1.29.7", "1.56") with { CompilationStatus = CompilationStatus.Pending },
            hasUsableBuild: true, "no bundle", 5)!;
        refused.BuildProvenance.Should().Be(BuildProvenance.AdoptionRefused, "the precondition");

        var pending = refused with { CompilationStatus = CompilationStatus.Pending };
        foreach (var bound in new[] { -1, 30 })
        {
            var lifted = BuildDeliveryHold.Settle(pending, hasUsableBuild: true, "no bundle", bound)!;
            lifted.BuildProvenance.Should().Be(BuildProvenance.StaleAdopted,
                $"bound {bound} no longer refuses a same-MAJOR build — the usable build serves again");
            lifted.CompilationStatus.Should().Be(CompilationStatus.Ok);
            BuildDeliveryHold.EventOf(pending, lifted, bound).Should().Be(BuildDeliveryHold.DeliveryEvent.HeldStale);
        }

        // Controls: a real MAJOR bump stays refused under a lifted bound, and so does a refusal
        // with no usable build to fall back to.
        var major = pending with { CurrentModuleVersion = "2.0" };
        BuildDeliveryHold.Settle(major, hasUsableBuild: true, "no bundle", -1)!
            .CompilationError.Should().StartWith("Incompatible build, awaiting bundle");
        BuildDeliveryHold.Settle(pending, hasUsableBuild: false, "no bundle", -1)!
            .BuildProvenance.Should().Be(BuildProvenance.AdoptionRefused);
    }
}
