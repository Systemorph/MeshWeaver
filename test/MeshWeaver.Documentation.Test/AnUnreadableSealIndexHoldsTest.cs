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
