using Xunit;

namespace MeshWeaver.GitSync.Test;

/// <summary>
/// Pins the missing-branch base policy in <see cref="OctokitGitHubRepoClient.AsNewBranchBase"/>:
/// a GitSync commit to a branch that does not exist on a NON-empty repo must base the new branch
/// on the DEFAULT branch head — parenting the commit on it and preserving its tree — instead of
/// creating a parent-less ORPHAN commit. An orphan branch shares no history with the default
/// branch, so it can never be PR'd or merged (observed live: a Space sync branch GitHub refused
/// to compare). RefExists is forced false so the push still CREATES the target ref rather than
/// trying to update a ref that is not there.
/// </summary>
public class NewBranchBaseTest
{
    [Fact]
    public void KeepsDefaultHeadAsParent_AndItsTreeAsPreserved()
    {
        var defaultHead = new OctokitGitHubRepoClient.HeadInfo(
            "abc1234def",
            RefExists: true,
            [("Docs/readme.md", "sha-1"), ("Other/file.json", "sha-2")]);

        var basis = OctokitGitHubRepoClient.AsNewBranchBase(defaultHead);

        // The commit parents on the default head → the new branch shares history with it.
        Assert.Equal("abc1234def", basis.CommitSha);
        // The default head's blobs are the preserved tree → the new branch is
        // "default branch + the mirrored subtree", not just the export.
        Assert.Equal(defaultHead.ExistingBlobs, basis.ExistingBlobs);
    }

    [Fact]
    public void ForcesRefExistsFalse_SoTheTargetRefIsCreated()
    {
        var defaultHead = new OctokitGitHubRepoClient.HeadInfo("abc1234def", RefExists: true, []);

        var basis = OctokitGitHubRepoClient.AsNewBranchBase(defaultHead);

        // The DEFAULT branch's ref exists, but the TARGET branch's does not — the push must
        // create refs/heads/{target}, not attempt an update of a missing ref.
        Assert.False(basis.RefExists);
    }
}
