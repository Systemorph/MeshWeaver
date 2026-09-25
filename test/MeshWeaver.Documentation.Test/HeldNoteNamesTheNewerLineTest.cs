#pragma warning disable CS1591
using System;
using System.Collections.Generic;
using System.IO;
using MeshWeaver.GitSync;
using MeshWeaver.Hosting;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 A hold that does not say WHICH way it is held gets acted on the wrong way.
///
/// <para><c>SealedSyncGate</c>'s sentence — <i>"built at S, not sealed for this instance (identity
/// I: 'plugins' is sealed at C)"</i> — is true, and it is equally consistent with two situations
/// whose remedies are opposite:</para>
/// <list type="number">
///   <item><description><b>Nothing has sealed recently.</b> The publishing lane is broken. Fix the
///   lane; another publication is exactly what is needed.</description></item>
///   <item><description><b>Seals are advancing under a NEWER framework identity</b> that this
///   instance does not run. The instance's IMAGE is behind. A roll is the remedy, and no number of
///   further publications will ever release the hold.</description></item>
/// </list>
///
/// <para>Measured 2026-09-16 on memex.meshweaver.cloud: held at <c>627fb3cd</c> under identity
/// <c>sd608997…</c> while the live publication was sealed under <c>s799247a…</c> — case 2. Two
/// issues stood open reading that note as case 1 (MeshWeaver.Plugins#1823 "no publication has
/// sealed since 2026-09-12", #1798 "9 events queued, nothing dispatched"), against a lane that was
/// green — 77 jobs, 0 failures, <c>Register the publication with memex</c> success — and an inbox
/// that returned <c>[]</c>. The fact that separates them was on the instance the whole time: the
/// release markers under the published root name EVERY line, not only this instance's.</para>
///
/// <para>These tests pin both halves: the reading (<see cref="SealedPublicationIndex.NewerLineThan"/>)
/// and the sentence it produces — and, as much as the clause itself, the cases where it must say
/// NOTHING rather than guess a direction. Since policy <c>module-sync-per-manifest-hash</c> the
/// gate holds nothing, so the sentence rides the green build's plan instead of a hold note.</para>
/// </summary>
public class HeldNoteNamesTheNewerLineTest
{
    private static readonly RepoIdentity Plugins = new("Systemorph", "MeshWeaver.Plugins");
    private const string Built = "e2ef5679aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Sealed = "627fb3cd2f1bc7142226a2ae881c81f9d83cc430";
    private const string Mine = "sd608997abeaeae7c88c77183718b6197";
    private const string Newer = "s799247a5b0d7267265862c1fe2a19c2a";

    private static IReadOnlyList<SealedSource> HeldAtAnotherCommit() =>
        [new("plugins", "Systemorph/MeshWeaver.Plugins", Sealed, true, null)];

    // ── The sentence ──────────────────────────────────────────────────────────
    //
    // 🚨 Policy module-sync-per-manifest-hash: the gate no longer HOLDS, so there is no hold note to
    // carry a direction. The newer line is still READ and still STATED — on the build plan's reason,
    // which is what tells an operator whether a synced type will adopt bytes or compile — and it is
    // still never invented where no marker places this instance.

    [Fact]
    public void ANoLineReading_NeverInventsADirection()
    {
        var plan = SealedSyncGate.DecideBuild(Plugins, Built, Sealed, HeldAtAnotherCommit(), Mine, null);

        plan.Proceed.Should().BeTrue("the seal no longer holds a source");
        plan.Commit.Should().Be(Built);
        plan.Reason.Should().Contain("'plugins' sealed at 627fb3cd")
            .And.NotContain("has since sealed",
                "a caller that could not place this instance on a line must not have a direction "
                + "invented for it");
    }

    [Fact]
    public void ANewerLine_IsStated_WhileTheBuildStillLands()
    {
        var plan = SealedSyncGate.DecideBuild(
            Plugins, Built, Sealed, HeldAtAnotherCommit(), Mine, new PublicationLine(Newer, "3.0.0-ci.8600"));

        plan.Proceed.Should().BeTrue();
        plan.Redirected.Should().BeFalse("the seal no longer chooses the commit");
        plan.Commit.Should().Be(Built);
        plan.Reason.Should().Contain("imported at the built commit e2ef5679")
            .And.Contain("'plugins' sealed at 627fb3cd")
            .And.Contain("3.0.0-ci.8600", "the operator must be able to see WHICH line moved on")
            .And.Contain(Newer, "…and under which identity, so it can be compared with this one");
    }

    [Fact]
    public void AnUnsealedPublication_IsStated_WhileTheBuildStillLands()
    {
        var plan = SealedSyncGate.DecideBuild(
            Plugins, Built, null,
            [new("plugins", "Systemorph/MeshWeaver.Plugins", Sealed, false, "sentinel absent")],
            Mine, new PublicationLine(Newer, "3.0.0-ci.8600"));

        plan.Proceed.Should().BeTrue();
        plan.Reason.Should().Contain("sentinel absent").And.Contain("3.0.0-ci.8600");
    }

    [Fact]
    public void AProceedingDecision_CarriesNoClause()
        => SealedSyncGate.Decide(
                Plugins, Built, Sealed,
                [new("plugins", "Systemorph/MeshWeaver.Plugins", Built, true, null)],
                Mine, new PublicationLine(Newer, "3.0.0-ci.8600"))
            .HoldReason.Should().BeNull("nothing is held, so there is no direction to state");

    // ── The reading ───────────────────────────────────────────────────────────

    [Fact]
    public void ANewerLineUnderAnotherIdentity_IsFound()
    {
        using var root = new Markers((Mine, "3.0.0-ci.8392"), (Newer, "3.0.0-ci.8600"));

        SealedPublicationIndex.NewerLineThan(root.Path, Mine)
            .Should().Be(new PublicationLine(Newer, "3.0.0-ci.8600"));
    }

    [Fact]
    public void TheNEWESTLineIsReported_WhenSeveralAreAhead()
    {
        using var root = new Markers(
            (Mine, "3.0.0-ci.8392"), ("sAAA", "3.0.0-ci.8500"), (Newer, "3.0.0-ci.8600"));

        SealedPublicationIndex.NewerLineThan(root.Path, Mine)!.Identity.Should().Be(Newer);
    }

    [Fact]
    public void TheNEWESTInstanceHasNoNewerLine()
    {
        using var root = new Markers((Mine, "3.0.0-ci.8600"), (Newer, "3.0.0-ci.8392"));

        SealedPublicationIndex.NewerLineThan(root.Path, Mine)
            .Should().BeNull("this instance IS the line the registry is sealing on");
    }

    /// <summary>
    /// 🚨 The silence that matters. An identity no marker places cannot be COMPARED, so there is no
    /// direction to state — and inventing one here would be the same defect in the other direction:
    /// telling an operator to roll an instance whose line nobody can establish.
    /// </summary>
    [Fact]
    public void AnIdentityNoMarkerPlaces_YieldsNothing()
    {
        using var root = new Markers((Newer, "3.0.0-ci.8600"));

        SealedPublicationIndex.NewerLineThan(root.Path, Mine).Should().BeNull();
    }

    [Fact]
    public void AnAbsentRootYieldsNothing_AndNeverThrows()
        => SealedPublicationIndex.NewerLineThan(
                Path.Combine(Path.GetTempPath(), "no-such-published-root-" + Guid.NewGuid().ToString("N")), Mine)
            .Should().BeNull();

    /// <summary>
    /// 🚨 Ordering is sealed-publication LINEAGE, never SemVer — the same trap
    /// <see cref="SealedPublicationIndex.ReleasesOf"/> documents (#3542). Under SemVer §11.4 the
    /// retired <c>rc9</c> line sorts ABOVE a later <c>ci</c> build, which would report an instance
    /// on today's line as behind a three-day-old one and send an operator to roll BACKWARDS.
    /// </summary>
    [Fact]
    public void ARetiredRcLabel_DoesNotOutrankALaterCiBuild()
    {
        using var root = new Markers((Mine, "3.0.0-ci.8600"), (Newer, "3.0.0-rc9.ci.7824"));

        SealedPublicationIndex.NewerLineThan(root.Path, Mine)
            .Should().BeNull("the rc line is retired and older by run number, whatever SemVer says");
    }

    /// <summary>A published root carrying only the release markers these cases need.</summary>
    private sealed class Markers : IDisposable
    {
        public string Path { get; }

        public Markers(params (string Identity, string Version)[] lines)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "mw-published-" + Guid.NewGuid().ToString("N"));
            var markers = System.IO.Path.Combine(Path, SealedPublicationIndex.ReleaseMarkerDirectoryName);
            Directory.CreateDirectory(markers);
            foreach (var (identity, version) in lines)
                File.WriteAllText(System.IO.Path.Combine(markers, version), identity);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
        }
    }
}
