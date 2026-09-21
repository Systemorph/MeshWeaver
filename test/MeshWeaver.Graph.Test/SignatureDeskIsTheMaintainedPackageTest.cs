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
/// answered first", or the no-provider case could go back to rendering an empty stack, and every
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
            ImmutableArray.Create(maintainedPresent, legacyPresent), Doc, "title", "body");

        var area = Assert.IsType<LayoutAreaControl>(block);
        Assert.Equal(expectedDesk, area.Address);
        Assert.Equal(MarkdownOverviewLayoutArea.SignatureArea, area.Reference.Area);
        // The document rides as the layout-area REFERENCE — that is how one desk serves every page.
        Assert.Equal(Doc, area.Reference.Id);
    }

    /// <summary>
    /// With NO e-signature package installed the section must say so. It used to render an empty
    /// stack, which teaches the reader that this document cannot be signed when the truth is only
    /// that nobody installed a provider — a silent omission no test could tell from a real block.
    /// </summary>
    [Fact]
    public void WithNoPackageInstalled_TheBlockSaysSoInsteadOfRenderingNothing()
    {
        var block = MarkdownOverviewLayoutArea.SignatureBlockFor(
            ImmutableArray.Create(false, false), Doc, "title", "body");

        Assert.IsNotType<LayoutAreaControl>(block);
        var stack = Assert.IsType<StackControl>(block);
        Assert.NotEmpty(stack.Areas);   // an EMPTY stack is the defect, not the fallback
    }

    /// <summary>
    /// 🚨 The fallback is composed from the platform's layout controls, never from markup. The
    /// first version of it was a <c>Controls.Html</c> string carrying its own flexbox card and a
    /// hand-drawn <c>&lt;svg&gt;</c> certificate — an own UI framework inside a node, which renders
    /// on one client only and takes no theme token it is not handed. The mark it drew is in the
    /// platform icon set already.
    /// </summary>
    [Fact]
    public void TheUnconfiguredBlockIsComposedFromPlatformControls()
    {
        var block = MarkdownOverviewLayoutArea.UnconfiguredSignatureBlock("title", "body");

        Assert.IsType<StackControl>(block);
        Assert.IsType<IconControl>(MarkdownOverviewLayoutArea.UnconfiguredSignatureMark());
        Assert.IsType<StackControl>(MarkdownOverviewLayoutArea.UnconfiguredSignatureText("t", "b"));
    }

    /// <summary>
    /// The block renders localized copy, and the copy must not describe a rendering the block does
    /// not produce: there is no signature to attest yet, so the mark is the hint colour. An earlier
    /// version of these strings told the reader it was green.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    public void TheUnconfiguredCopyIsLocalizedAndDoesNotPromiseAColour(string locale)
    {
        var title = LocalizationCatalog.Get("signature.unconfigured.title", locale);
        var body = LocalizationCatalog.Get("signature.unconfigured.body", locale);

        Assert.NotEqual("signature.unconfigured.title", title);   // a raw key means it is missing
        Assert.NotEqual("signature.unconfigured.body", body);

        // The mark is rendered in the hint token, so no wording may name a colour for it.
        // 🚨 Asserted on Style, not on Color: IconControl.Color is bound by no view (the Blazor
        // icon view binds Data and Width only), so pinning Color would pin a property nothing
        // reads — a green assertion over a colour that never renders.
        Assert.Contains("var(--neutral-foreground-hint)",
            MarkdownOverviewLayoutArea.UnconfiguredSignatureMark().Style?.ToString());
        foreach (var colour in new[] { "green", "grün", "gruen" })
            Assert.DoesNotContain(colour, body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The approvals section delegates the same way and has never been renamed — pinned here so a
    /// future rename of THAT package trips a test instead of silently emptying the section.
    /// </summary>
    [Fact]
    public void TheApprovalsDeskStillNamesTheApprovalsPackage()
        => Assert.Equal("Approvals/Workspace", MarkdownOverviewLayoutArea.ApprovalDeskPath);
}
