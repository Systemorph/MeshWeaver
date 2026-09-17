#pragma warning disable CS1591
using System;
using System.IO;
using MeshWeaver.GitSync;
using MeshWeaver.Hosting;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 An empty reading of the sealed index had TWO meanings, and the gate acted on the wrong one.
///
/// <para><see cref="SealedSyncGate.Decide(RepoIdentity, string, string?, System.Collections.Generic.IReadOnlyList{SealedSource}, string)"/>
/// answers <c>Go</c> when no sealed source is attributable to the repository — correct, because an
/// instance running no publication of it is not this gate's business. A reading that FAILED
/// produced the same empty list, so a total reader failure passed <b>every</b> repository: the
/// exact inversion of "advance only to the commit sealed for this instance", with nothing red
/// anywhere and a single Warning in a log nobody gates on.</para>
///
/// <para>#3461 phase 5 — dropping the flat compatibility copy under the published root — is the
/// documented trigger. <c>SealedPublicationIndex</c>'s own comment says so: the reader "would find
/// no sentinel at all, report every source unsealed, and SealedSyncGate would then see an EMPTY
/// <c>mine</c> and return Go for every repository — silently removing the whole rule at the moment
/// it matters most." This pins the distinction so that flip cannot do it.</para>
/// </summary>
public class AnUnreadableSealIndexHoldsTest
{
    private const string Identity = "sd608997abeaeae7c88c77183718b6197";

    [Fact]
    public void AnUnconfiguredRootIsAnANSWER_NotAFailure()
    {
        var (sources, outcome) = SealedPublicationIndex.ReadingFor(null, Identity);

        sources.Should().BeEmpty();
        outcome.Should().Be(SealedReadOutcome.NotConfigured,
            "an instance that seeds from nothing genuinely has nothing sealed — the gate does not apply");
        SealedSyncGate.RefusedForUnreadableIndex(outcome, Identity)
            .Should().BeNull("nothing failed, so nothing is held");
    }

    [Fact]
    public void ARootWithNoDirectoryForThisIdentity_ReadsCleanlyAsEmpty()
    {
        using var root = new TempRoot();

        var (sources, outcome) = SealedPublicationIndex.ReadingFor(root.Path, Identity);

        sources.Should().BeEmpty();
        outcome.Should().Be(SealedReadOutcome.Read,
            "a freshly-built framework whose identity has no directory yet is an ORDINARY state, "
            + "and reporting it as a failure would hold every source on every new platform line");
        SealedSyncGate.RefusedForUnreadableIndex(outcome, Identity).Should().BeNull();
    }

    /// <summary>
    /// 🚨 THE case. The identity's directory exists and cannot be enumerated — here because it is
    /// a FILE where a directory is expected, which is what a half-finished layout migration looks
    /// like from this reader. The empty list must not read as "nothing sealed".
    /// </summary>
    [Fact]
    public void AnIdentityPathThatCannotBeEnumerated_IsUNREADABLE_AndHoldsEverySource()
    {
        using var root = new TempRoot();
        File.WriteAllText(Path.Combine(root.Path, Identity), "not a directory");

        var (sources, outcome) = SealedPublicationIndex.ReadingFor(root.Path, Identity);

        sources.Should().BeEmpty("nothing could be enumerated");
        outcome.Should().Be(SealedReadOutcome.Unreadable,
            "the root IS configured and the enumeration failed — that is an absence of "
            + "measurement, never a clean empty index");

        var refusal = SealedSyncGate.RefusedForUnreadableIndex(outcome, Identity);
        refusal.Should().NotBeNull("this is the verdict that stops the rule being switched off");
        refusal!.Proceed.Should().BeFalse();
        refusal.HoldReason.Should().Contain("could not be READ")
            .And.Contain(Identity, "the operator must see WHICH identity's index failed")
            .And.Contain("Cannot tell", "the rule is stated where it is applied");
    }

    /// <summary>
    /// 🚨 THE PHASE-5 case, and the one the comment above predicted. `_current` EXISTS and cannot
    /// be followed (here: it names a generation that is not on disk — the same reading a pointer
    /// caught mid-replacement gives, since <c>az storage file upload</c> is create-then-put-range),
    /// and the flat compatibility copy is GONE because a generation publication disposed of it.
    ///
    /// <para>The fall-back therefore lands on a source directory that holds no publication: the
    /// source reads as unsealed AND unattributable, which <see cref="SealedSyncGate"/> answers with
    /// <c>Go</c> — for every repository, since a source nobody could attribute may be any of
    /// them.</para>
    /// </summary>
    [Fact]
    public void APointerThatCannotBeFollowed_OverADisposedFlatCopy_IsUNREADABLE()
    {
        using var root = new TempRoot();
        var source = Path.Combine(root.Path, Identity, "plugins");
        Directory.CreateDirectory(Path.Combine(source, "Systemorph-MeshWeaver-1-1"));
        File.WriteAllText(
            Path.Combine(source, "Systemorph-MeshWeaver-1-1",
                ShippedPrebuiltBundles.CompletionSentinelFileName), "Store.zip\n");
        File.WriteAllText(
            Path.Combine(source, ShippedPrebuiltBundles.PublicationPointerFileName),
            "Systemorph-MeshWeaver-9999-9\n");

        var (sources, outcome) = SealedPublicationIndex.ReadingFor(root.Path, Identity);

        sources.Should().HaveCount(1);
        sources[0].IsSealed.Should().BeFalse();
        sources[0].Refusal.Should().Contain("pointer could not be followed",
            "the reason must name the pointer, not the sentinel — they call for different action");
        sources[0].Repository.Should().BeNull("there is no marker to attribute it with");
        outcome.Should().Be(SealedReadOutcome.Unreadable,
            "a pointer that exists and cannot be followed, with no sealed publication behind it, "
            + "is an absence of measurement — and phase 5 removed the copy that used to be behind it");

        SealedSyncGate.RefusedForUnreadableIndex(outcome, Identity)!.Proceed.Should().BeFalse();
        var firstImport = SealedSyncGate.RefusedFirstImportForUnreadableIndex(outcome, Identity);
        firstImport.Should().NotBeNull(
            "a first import reads worse from an unreadable index than a green build does: only the "
            + "repository marker can attribute a seal there, so it would populate from the tip");
        firstImport!.Proceed.Should().BeFalse();
        firstImport.HoldReason.Should().Contain("could not be READ");
    }

    /// <summary>
    /// The control that keeps the case above from holding the whole fleet on any pointer hiccup:
    /// the SAME unusable pointer, with a sealed FLAT copy behind it, is today's behaviour exactly —
    /// the fall-back is a publication, so the reading is a statement.
    /// </summary>
    [Fact]
    public void TheSamePointer_WithASealedFlatCopyBehindIt_ReadsCleanly()
    {
        using var root = new TempRoot();
        var source = Path.Combine(root.Path, Identity, "plugins");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "Store.zip"), "bytes");
        File.WriteAllText(
            Path.Combine(source, ShippedPrebuiltBundles.CompletionSentinelFileName), "Store.zip\n");
        File.WriteAllText(
            Path.Combine(source, SealedPublicationIndex.RepositoryMarkerFileName),
            "Systemorph/MeshWeaver.Plugins\n");
        File.WriteAllText(
            Path.Combine(source, SealedPublicationIndex.SourceCommitMarkerFileName),
            "e2ef5679aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n");
        File.WriteAllText(
            Path.Combine(source, ShippedPrebuiltBundles.PublicationPointerFileName),
            "Systemorph-MeshWeaver-9999-9\n");

        var (sources, outcome) = SealedPublicationIndex.ReadingFor(root.Path, Identity);

        sources.Should().HaveCount(1);
        sources[0].IsSealed.Should().BeTrue("the flat copy behind the pointer IS a sealed publication");
        outcome.Should().Be(SealedReadOutcome.Read);
        SealedSyncGate.RefusedForUnreadableIndex(outcome, Identity).Should().BeNull();
        SealedSyncGate.RefusedFirstImportForUnreadableIndex(outcome, Identity).Should().BeNull();
    }

    /// <summary>
    /// And the ordinary phase-5 reading: no flat copy, and the pointer resolves. Without this the
    /// case above could pass because the reader had stopped following pointers at all.
    /// </summary>
    [Fact]
    public void APointerThatResolves_WithNoFlatCopy_ReadsTheGenerationAndIsSEALED()
    {
        using var root = new TempRoot();
        var source = Path.Combine(root.Path, Identity, "plugins");
        var generation = Path.Combine(source, "Systemorph-MeshWeaver-1-1");
        Directory.CreateDirectory(generation);
        File.WriteAllText(Path.Combine(generation, "Store.zip"), "bytes");
        File.WriteAllText(
            Path.Combine(generation, ShippedPrebuiltBundles.CompletionSentinelFileName), "Store.zip\n");
        File.WriteAllText(
            Path.Combine(generation, SealedPublicationIndex.RepositoryMarkerFileName),
            "Systemorph/MeshWeaver.Plugins\n");
        File.WriteAllText(
            Path.Combine(generation, SealedPublicationIndex.SourceCommitMarkerFileName),
            "e2ef5679aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n");
        File.WriteAllText(
            Path.Combine(source, ShippedPrebuiltBundles.PublicationPointerFileName),
            "Systemorph-MeshWeaver-1-1\n");

        var (sources, outcome) = SealedPublicationIndex.ReadingFor(root.Path, Identity);

        sources.Should().HaveCount(1);
        sources[0].IsSealed.Should().BeTrue();
        sources[0].Repository.Should().Be("Systemorph/MeshWeaver.Plugins");
        outcome.Should().Be(SealedReadOutcome.Read);
    }

    /// <summary>
    /// 🚨 A MARKER THAT COULD NOT BE READ is not an absent one, and null attribution is what the
    /// gate answers with <c>Go</c> (Copilot's review of the phase-5 PR). The fixture makes
    /// <c>repository.txt</c> a DIRECTORY, which is a read failure no test privilege can turn into a
    /// success — <c>chmod 000</c> would not hold as root, which CI may well be.
    /// </summary>
    [Fact]
    public void AMarkerThatCannotBeRead_IsUNREADABLE_NotAnUnattributableSource()
    {
        using var root = new TempRoot();
        var source = Path.Combine(root.Path, Identity, "plugins");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "Store.zip"), "bytes");
        File.WriteAllText(
            Path.Combine(source, ShippedPrebuiltBundles.CompletionSentinelFileName), "Store.zip\n");
        File.WriteAllText(
            Path.Combine(source, SealedPublicationIndex.SourceCommitMarkerFileName),
            "e2ef5679aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n");
        Directory.CreateDirectory(Path.Combine(source, SealedPublicationIndex.RepositoryMarkerFileName));

        var (sources, outcome) = SealedPublicationIndex.ReadingFor(root.Path, Identity);

        sources.Should().HaveCount(1);
        sources[0].Repository.Should().BeNull("it could not be read — which is the point");
        sources[0].Refusal.Should().Contain("marker could not be read");
        outcome.Should().Be(SealedReadOutcome.Unreadable,
            "an unattributable source is what the gate answers with Go, so 'I could not read the "
            + "marker' must not arrive as 'this source belongs to nobody'");
        SealedSyncGate.RefusedForUnreadableIndex(outcome, Identity)!.Proceed.Should().BeFalse();
    }

    /// <summary>
    /// The same class one file over: a SEAL that is present and unreadable used to read as "no
    /// completion sentinel" — an unsealed source, which holds only when something can attribute it.
    /// </summary>
    [Fact]
    public void ASealThatCannotBeRead_IsUNREADABLE_NotAnUnsealedSource()
    {
        using var root = new TempRoot();
        var source = Path.Combine(root.Path, Identity, "plugins");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(
            Path.Combine(source, ShippedPrebuiltBundles.CompletionSentinelFileName));

        var (sources, outcome) = SealedPublicationIndex.ReadingFor(root.Path, Identity);

        sources.Should().HaveCount(1);
        sources[0].IsSealed.Should().BeFalse();
        sources[0].Refusal.Should().Contain("could not be read");
        outcome.Should().Be(SealedReadOutcome.Unreadable);
    }

    /// <summary>
    /// The control for both: a sealed, attributable source reads cleanly. Without it the two cases
    /// above could pass because every reading had become UNREADABLE.
    /// </summary>
    [Fact]
    public void AReadableSealedSource_StillReadsCleanly()
    {
        using var root = new TempRoot();
        var source = Path.Combine(root.Path, Identity, "plugins");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "Store.zip"), "bytes");
        File.WriteAllText(
            Path.Combine(source, ShippedPrebuiltBundles.CompletionSentinelFileName), "Store.zip\n");
        File.WriteAllText(
            Path.Combine(source, SealedPublicationIndex.RepositoryMarkerFileName),
            "Systemorph/MeshWeaver.Plugins\n");
        File.WriteAllText(
            Path.Combine(source, SealedPublicationIndex.SourceCommitMarkerFileName),
            "e2ef5679aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n");

        var (sources, outcome) = SealedPublicationIndex.ReadingFor(root.Path, Identity);

        sources.Should().HaveCount(1);
        sources[0].IsSealed.Should().BeTrue();
        sources[0].Repository.Should().Be("Systemorph/MeshWeaver.Plugins");
        outcome.Should().Be(SealedReadOutcome.Read);
        SealedSyncGate.RefusedForUnreadableIndex(outcome, Identity).Should().BeNull();
    }

    /// <summary>
    /// The negative control for the whole change: with the SAME empty list, the per-repository
    /// verdict still says Go. That is what makes the precondition necessary — the gate alone
    /// cannot tell these apart, and never could.
    /// </summary>
    [Fact]
    public void TheGateAloneStillSaysGoOnAnEmptyList_WhichIsWhyThePreconditionExists()
        => SealedSyncGate.Decide(
                new RepoIdentity("Systemorph", "MeshWeaver.Plugins"),
                "e2ef5679aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", null, [], Identity)
            .Proceed.Should().BeTrue(
                "an empty list reads as 'no seal attributable to this repository' — correct for a "
                + "real absence, and indistinguishable from a failed read, which is exactly why "
                + "the caller must ask RefusedForUnreadableIndex FIRST");

    private sealed class TempRoot : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "mw-sealroot-" + Guid.NewGuid().ToString("N"));

        public TempRoot() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
        }
    }
}
