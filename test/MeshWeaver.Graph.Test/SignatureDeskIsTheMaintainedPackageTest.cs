using System.Collections.Immutable;
using System.Linq;
using MeshWeaver.Layout;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The document page renders its signature block from a PACKAGE desk, and the path it delegates to
/// used to be a single hard-coded constant: <c>"DeepSign/Workspace"</c>.
///
/// <para>That package was renamed to <c>Signature/</c> in MeshWeaver.Plugins and no <c>DeepSign</c>
/// path exists anywhere on its <c>main</c> — core's own <c>PublicationSealStarvation</c> records
/// GitSync answering <i>"No files found under subdirectory 'DeepSign'"</i>. The constant kept
/// pointing at the retired desk, while the node menu (<c>Signature/RequestSignatureMenu</c>) wrote
/// through the new one. Nothing errored: on a mesh where the stale desk node was still installed
/// the probe found it and the section rendered — from the wrong package — so a signature requested
/// from the menu was invisible in the block on the page it was requested from.</para>
///
/// <para>These tests pin the properties that failure needed: that the maintained package is
/// PREFERRED, that a legacy name may only ever be a fallback behind it, and that the choice is
/// decided by list order rather than by whichever probe happens to answer first.</para>
///
/// <para>🚨 <b>Pinning the array alone is not coverage of the behaviour that changed</b>, which is
/// what the first version of this class did. The selection could regress to "whichever probe
/// answered first", or the no-provider case could start rendering a card again, and every
/// assertion about the array would still pass. The selection and the fallback are therefore
/// exercised through <see cref="MarkdownOverviewLayoutArea.SignatureBlockFor"/> — the pure function
/// the reactive section is a wrapper over.</para>
/// </summary>
public class SignatureDeskIsTheMaintainedPackageTest
{
    private const string Maintained = "Signature/Workspace";
    private const string Legacy = "DeepSign/Workspace";

    [Fact]
    public void TheMaintainedPackageIsPreferredOverEveryLegacyName()
    {
        var paths = MarkdownOverviewLayoutArea.SignatureDeskPaths;

        // A document page with no desk to delegate to renders no signatures at all.
        Assert.NotEmpty(paths);

        // The FIRST entry is the one a mesh carrying BOTH packages renders, so it must be the
        // package that still exists in source.
        Assert.Equal(Maintained, paths[0]);
    }

    [Fact]
    public void TheRetiredPackageIsStillReachableButOnlyAsAFallback()
    {
        var paths = MarkdownOverviewLayoutArea.SignatureDeskPaths;

        // Kept deliberately: meshes installed before the rename still serve only this desk.
        // Drop the entry once none do.
        Assert.Contains(Legacy, paths);

        // A legacy desk must never win over the maintained one on a mesh that has both.
        Assert.True(paths.IndexOf(Legacy) > paths.IndexOf(Maintained));
    }

    [Fact]
    public void EveryCandidateIsDistinctSoPreferenceOrderIsUnambiguous()
    {
        var paths = MarkdownOverviewLayoutArea.SignatureDeskPaths;

        // A repeated path makes "the first one present" mean two different things.
        Assert.Equal(paths.Length, paths.Distinct().Count());
    }

    private const string Doc = "acme/Contracts/MasterAgreement";

    /// <summary>
    /// 🚨 THE regression this PR exists to stop, and the one the array assertions cannot see: the
    /// block must come from the first desk present in LIST order. On a mesh carrying both packages
    /// that is the maintained one — which is exactly the case that rendered from the retired desk.
    /// </summary>
    [Theory]
    [InlineData(true, true, Maintained)]    // both installed — the maintained package wins
    [InlineData(true, false, Maintained)]   // only the maintained one
    [InlineData(false, true, Legacy)]       // only a pre-rename mesh's desk — still served
    public void TheFirstDeskPresentInListOrderRendersTheBlock(
        bool maintainedPresent, bool legacyPresent, string expectedDesk)
    {
        var block = MarkdownOverviewLayoutArea.SignatureBlockFor(
            ImmutableArray.Create(maintainedPresent, legacyPresent), Doc);

        var area = Assert.IsType<LayoutAreaControl>(block);
        Assert.Equal(expectedDesk, area.Address);
        Assert.Equal(MarkdownOverviewLayoutArea.SignatureArea, area.Reference.Area);
        // The document rides as the layout-area REFERENCE — that is how one desk serves every page.
        Assert.Equal(Doc, area.Reference.Id);
    }

    /// <summary>
    /// With NO e-signature package installed the section renders NOTHING (maintainer, 2026-09-23:
    /// the signature feature must not show on every page, only where a signature was explicitly
    /// requested). An earlier version rendered a "No e-signature provider is configured" card on
    /// every Markdown page of such a mesh.
    /// </summary>
    [Fact]
    public void WithNoPackageInstalled_TheSectionRendersNothing()
    {
        var block = MarkdownOverviewLayoutArea.SignatureBlockFor(ImmutableArray.Create(false, false), Doc);

        var stack = Assert.IsType<StackControl>(block);
        Assert.Empty(stack.Areas);
    }

    /// <summary>A probe list SHORTER than the desk list (no answer yet for a desk) is "absent", never an index error.</summary>
    [Fact]
    public void AMissingProbeAnswerCountsAsAbsent()
    {
        var block = MarkdownOverviewLayoutArea.SignatureBlockFor(ImmutableArray<bool>.Empty, Doc);

        Assert.Empty(Assert.IsType<StackControl>(block).Areas);
    }

    /// <summary>The no-provider card's copy is retired with the card — a leftover key would be dead text.</summary>
    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    public void TheRetiredUnconfiguredCopyIsGoneFromTheCatalog(string locale)
        => Assert.Equal("signature.unconfigured.title",
            LocalizationCatalog.Get("signature.unconfigured.title", locale));

    /// <summary>
    /// The approvals section delegates the same way and has never been renamed — pinned here so a
    /// future rename of THAT package trips a test instead of silently emptying the section.
    /// </summary>
    [Fact]
    public void TheApprovalsDeskStillNamesTheApprovalsPackage()
        => Assert.Equal("Approvals/Workspace", MarkdownOverviewLayoutArea.ApprovalDeskPath);
}
