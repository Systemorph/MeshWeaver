using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using MeshWeaver.Compiler;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>An OLD module on a NEWER platform release</b> — the decision behind
/// <c>Modules:VersionStrictness</c> (maintainer, 2026-09-08: <i>"typically it should accept newer
/// platform versions, especially within the same family ⇒ a setting for version strictness; for
/// dev we should be very tolerant and only use min versions"</i>).
///
/// <para><b>What was measured before the fix.</b> Adoption keyed on the EXACT framework identity:
/// a bundle sealed for identity A was declined whole on identity B, whatever the platform line —
/// so every roll adopted nothing until every satellite had re-sealed, and on 2026-09-08 no sealed
/// set could be produced at all. Written RED first: <see cref="PrebuiltAdoptionPolicy"/> did not
/// exist, and the only decision in the tree (<c>PrebuiltAssemblySeeder.DeclineReason</c>)
/// answers "decline" for every identity but the live one.</para>
///
/// <para><b>The measured half.</b> A version comparison can never say whether bytes LOAD; the link
/// check over the assembly's own metadata can. The arms below use this test assembly's real bytes:
/// against the running process every platform reference resolves (adopt); against a surface that
/// carries no <c>MeshWeaver.*</c> assembly at all they do not (compile instead — or refuse, loudly
/// and naming the type, on a require-prebuilt mesh). Same bytes, same policy, one variable.</para>
/// </summary>
public class PrebuiltAdoptionPolicyTest : IDisposable
{
    private const string LiveIdentity = "s0000000000000000000000000000live";
    private const string OlderIdentity = "s000000000000000000000000000older";
    private static readonly PrebuiltAdoptionPolicy.Live Live = new(LiveIdentity, "3.1.0-ci.9000", RequirePrebuilt: false);
    private static readonly PrebuiltAdoptionPolicy.Live LiveRequirePrebuilt = Live with { RequirePrebuilt = true };

    private static PrebuiltAdoptionPolicy.Candidate Older(string? version = "3.0.0-ci.8059", string? floor = null)
        => new(OlderIdentity, version, floor);

    private readonly string root = Path.Combine(Path.GetTempPath(), "adoption-policy-" + Guid.NewGuid().ToString("N"));

    // ── the exact path is byte for byte what shipped ─────────────────────────────────────────

    [Fact]
    public void ThisIdentity_AdoptsUnderEveryStrictness_WithNoLinkCheck()
    {
        foreach (var strictness in Enum.GetValues<VersionStrictness>())
        {
            var d = PrebuiltAdoptionPolicy.Decide(strictness, new(LiveIdentity, null, null), Live);
            d.Adopts.Should().BeTrue($"the live identity's own bundle always adopts ({strictness})");
            d.LinkCheckRequired.Should().BeFalse("its bytes were compiled against exactly this platform");
        }
    }

    [Fact]
    public void Exact_DeclinesEveryOtherIdentity_LikeToday()
    {
        var d = PrebuiltAdoptionPolicy.Decide(VersionStrictness.Exact, Older(), Live);
        d.Verdict.Should().Be(AdoptionVerdict.Decline);
        d.Reason.Should().Be(PrebuiltAssemblySeeder.DeclineReason(OlderIdentity, LiveIdentity)!,
            "Exact is the old rule, stated by the old function — no second wording");
    }

    // ── Family: same major line, floor satisfied, links measured ─────────────────────────────

    [Fact]
    public void Family_AdoptsAnOlderReleaseOfTheSameLine_SubjectToTheLinkCheck()
    {
        var d = PrebuiltAdoptionPolicy.Decide(VersionStrictness.Family, Older("3.0.0-ci.8059"), Live);
        d.Adopts.Should().BeTrue("3.0.0 and 3.1.0 are one platform line");
        d.LinkCheckRequired.Should().BeTrue("the bytes were compiled against another build — their links are MEASURED, never assumed");
        d.Reason.Should().Contain("3.0.0-ci.8059").And.Contain("3.1.0-ci.9000");
    }

    [Fact]
    public void Family_DeclinesAnotherLine()
    {
        var d = PrebuiltAdoptionPolicy.Decide(VersionStrictness.Family, Older("2.9.0"), Live);
        d.Verdict.Should().Be(AdoptionVerdict.Decline);
        d.Reason.Should().Contain("line 2").And.Contain("line 3");
    }

    [Fact]
    public void Family_DeclinesAnIdentityNoReleaseMarkerNames()
    {
        // No _releases/<version> marker → the identity cannot be placed on a line. Family does not
        // guess; Minimum (below) does not need to.
        var d = PrebuiltAdoptionPolicy.Decide(VersionStrictness.Family, Older(version: null), Live);
        d.Verdict.Should().Be(AdoptionVerdict.Decline);
        d.Reason.Should().Contain("_releases");
    }

    [Fact]
    public void Family_HonoursTheDeclaredFloor()
    {
        var d = PrebuiltAdoptionPolicy.Decide(VersionStrictness.Family, Older(floor: "3.2.0"), Live);
        d.Verdict.Should().Be(AdoptionVerdict.Decline);
        d.Reason.Should().Contain("3.2.0").And.Contain("3.1.0-ci.9000");
        PrebuiltAdoptionPolicy.Decide(VersionStrictness.Family, Older(floor: "3.0.0"), Live)
            .Adopts.Should().BeTrue("a satisfied floor does not decline");
    }

    // ── Minimum: floor + links only ───────────────────────────────────────────────────────────

    [Fact]
    public void Minimum_IgnoresTheLine_ButHonoursTheFloorAndRequiresTheLinkCheck()
    {
        var other = PrebuiltAdoptionPolicy.Decide(VersionStrictness.Minimum, Older("2.9.0"), Live);
        other.Adopts.Should().BeTrue("Minimum does not consult the platform line");
        other.LinkCheckRequired.Should().BeTrue();

        var unnamed = PrebuiltAdoptionPolicy.Decide(VersionStrictness.Minimum, Older(version: null), Live);
        unnamed.Adopts.Should().BeTrue("an identity with no release marker is still a candidate under Minimum");

        var floored = PrebuiltAdoptionPolicy.Decide(VersionStrictness.Minimum, Older(floor: "4.0.0"), Live);
        floored.Verdict.Should().Be(AdoptionVerdict.Decline, "the floor is the ONE thing Minimum checks besides the links");
    }

    // ── the measured half: real bytes against a real surface ─────────────────────────────────

    [Fact]
    public void AfterLink_AdoptsWhenEveryPlatformReferenceResolves()
    {
        var decision = PrebuiltAdoptionPolicy.Decide(VersionStrictness.Family, Older(), Live);
        var bytes = File.ReadAllBytes(typeof(PrebuiltAdoptionPolicyTest).Assembly.Location);
        var link = ModulePlatformLink.Check(
            bytes, "MeshWeaver.Hosting.Test", ImmutableHashSet<string>.Empty,
            ModulePlatformSurface.OfRunningProcess(AppContext.BaseDirectory));
        link.State.Should().Be(ModuleLinkState.Linkable, "this assembly's references are all loaded in this process");

        var final = PrebuiltAdoptionPolicy.AfterLink(decision, link, Live);
        final.Verdict.Should().Be(AdoptionVerdict.Adopt);
        final.LinkCheckRequired.Should().BeFalse("the check has run");
    }

    [Fact]
    public void AfterLink_CompilesInstead_WhenAPlatformTypeIsMissing_AndRefusesLoudlyOnARequirePrebuiltMesh()
    {
        var decision = PrebuiltAdoptionPolicy.Decide(VersionStrictness.Family, Older(), Live);
        var bytes = File.ReadAllBytes(typeof(PrebuiltAdoptionPolicyTest).Assembly.Location);
        // A platform that carries NO MeshWeaver assembly: every MeshWeaver.* reference is a
        // certain load failure — the coarse shape of "the newer platform moved a type".
        var bare = ModulePlatformSurface.Of([]);
        var link = ModulePlatformLink.Check(bytes, "MeshWeaver.Hosting.Test", ImmutableHashSet<string>.Empty, bare);
        link.State.Should().Be(ModuleLinkState.Unlinkable);
        link.MissingTypes.Should().NotBeEmpty();

        var compile = PrebuiltAdoptionPolicy.AfterLink(decision, link, Live);
        compile.Verdict.Should().Be(AdoptionVerdict.CompileInstead, "a mesh that may compile replaces the bytes with a live build");
        compile.Reason.Should().Contain(link.MissingTypes[0]);

        var refuse = PrebuiltAdoptionPolicy.AfterLink(decision, link, LiveRequirePrebuilt);
        refuse.Verdict.Should().Be(AdoptionVerdict.Refuse, "a mesh that cannot compile must SAY it cannot serve the type");
        refuse.Reason.Should().Contain(PrebuiltAssemblySeeder.RequirePrebuiltConfigKey)
            .And.Contain("MeshWeaver.Hosting.Test").And.Contain(link.MissingTypes[0]);
    }

    [Fact]
    public void AfterLink_PassesThroughADecisionThatNeededNoCheck()
    {
        var exact = PrebuiltAdoptionPolicy.Decide(VersionStrictness.Family, new(LiveIdentity, null, null), Live);
        var link = ModulePlatformLink.Check(
            File.ReadAllBytes(typeof(PrebuiltAdoptionPolicyTest).Assembly.Location),
            "MeshWeaver.Hosting.Test", ImmutableHashSet<string>.Empty, ModulePlatformSurface.Of([]));
        PrebuiltAdoptionPolicy.AfterLink(exact, link, Live).Should().Be(exact,
            "the exact identity's bytes are not re-judged by a link check they never needed");
    }

    // ── configuration ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Defaults_FamilyInGeneral_MinimumInDevelopment()
    {
        PrebuiltAdoptionPolicy.Parse(null, isDevelopment: false).Should().Be(VersionStrictness.Family);
        PrebuiltAdoptionPolicy.Parse(null, isDevelopment: true).Should().Be(VersionStrictness.Minimum);
        PrebuiltAdoptionPolicy.Parse("exact", isDevelopment: true).Should().Be(VersionStrictness.Exact,
            "a configured value wins over the environment, case-insensitively");
        PrebuiltAdoptionPolicy.Parse("nonsense", isDevelopment: false).Should().Be(VersionStrictness.Family,
            "an unparseable value falls back to the general default, never to Exact by accident");
        PrebuiltAdoptionPolicy.ConfigKey.Should().Be("Modules:VersionStrictness");
    }

    [Fact]
    public void MajorOf_ReadsTheLine()
    {
        PrebuiltAdoptionPolicy.MajorOf("3.0.0-ci.8059").Should().Be(3);
        PrebuiltAdoptionPolicy.MajorOf("12.4.1").Should().Be(12);
        PrebuiltAdoptionPolicy.MajorOf("ci.8059").Should().BeNull();
        PrebuiltAdoptionPolicy.SameFamily("3.0.0", "3.1.0-ci.1").Should().BeTrue();
        PrebuiltAdoptionPolicy.SameFamily("3.0.0", "4.0.0").Should().BeFalse();
        PrebuiltAdoptionPolicy.SameFamily("3.0.0", null).Should().BeFalse();
    }

    // ── the stamp a tolerant adoption carries ─────────────────────────────────────────────────

    [Fact]
    public void LiveStampOf_RepointsDependenciesAtTheLivePlatform()
    {
        var recorded = new Dictionary<string, string>
        {
            ["MeshWeaver.Layout"] = "ref:OLD",
            ["MeshWeaver.Data"] = "ref:OLD2",
            [CompiledDependencies.ToolchainKey] = "mvid:old",
            [CompiledDependencies.ContentKey] = "i123",
        };
        var stamped = PrebuiltAdoptionPolicy.LiveStampOf(
            recorded, key => key == "MeshWeaver.Layout" ? "ref:LIVE" : null, "mvid:live")!;
        stamped["MeshWeaver.Layout"].Should().Be("ref:LIVE", "the assembly the bytes bind to HERE");
        stamped["MeshWeaver.Data"].Should().Be("ref:OLD2", "a key with no live id keeps the producer's value");
        stamped[CompiledDependencies.ToolchainKey].Should().Be("mvid:live");
        stamped[CompiledDependencies.ContentKey].Should().Be("i123", "the input token is the producer's fact");
        PrebuiltAdoptionPolicy.LiveStampOf(null, _ => null, null).Should().BeNull("a legacy bundle stamps nothing");
    }

    // ── the release markers place an identity on a line ───────────────────────────────────────

    [Fact]
    public void ReleasesOf_MapsEachIdentityToItsNewestVersion()
    {
        var markers = Path.Combine(root, SealedPublicationIndex.ReleaseMarkerDirectoryName);
        Directory.CreateDirectory(markers);
        File.WriteAllText(Path.Combine(markers, "3.0.0-ci.900"), OlderIdentity + "\n");
        File.WriteAllText(Path.Combine(markers, "3.0.0-ci.3758"), OlderIdentity + "\n");
        File.WriteAllText(Path.Combine(markers, "3.1.0-ci.9000"), LiveIdentity);
        File.WriteAllText(Path.Combine(markers, "empty"), "");

        var releases = SealedPublicationIndex.ReleasesOf(root);
        releases[OlderIdentity].Should().Be("3.0.0-ci.3758", "SemVer order, not string order — ci.900 must not win");
        releases[LiveIdentity].Should().Be("3.1.0-ci.9000");
        releases.Count.Should().Be(2, "an empty marker names nothing");
        SealedPublicationIndex.ReleasesOf(Path.Combine(root, "absent")).Count.Should().Be(0);
        SealedPublicationIndex.ReleasesOf(null).Count.Should().Be(0);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        catch { /* best effort */ }
    }
}
