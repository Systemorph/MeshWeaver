using System.Linq;
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

    /// <summary>
    /// The approvals section delegates the same way and has never been renamed — pinned here so a
    /// future rename of THAT package trips a test instead of silently emptying the section.
    /// </summary>
    [Fact]
    public void TheApprovalsDeskStillNamesTheApprovalsPackage()
        => Assert.Equal("Approvals/Workspace", MarkdownOverviewLayoutArea.ApprovalDeskPath);
}
