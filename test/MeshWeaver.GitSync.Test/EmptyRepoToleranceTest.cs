using System.Net;
using MeshWeaver.GitSync;
using Octokit;
using Xunit;

namespace MeshWeaver.GitSync.Test;

/// <summary>
/// Pins the empty-repo tolerance in <see cref="OctokitGitHubRepoClient.IsMissingOrEmptyRepo"/>.
///
/// <para>The first "sync back" to a freshly-created GitHub repo failed with
/// <c>Git Repository is empty.</c>. A brand-new repo with ZERO commits has an unborn HEAD, and
/// GitHub returns <b>409 Conflict</b> (not 404) for ref/commit lookups until the first commit lands.
/// <c>ReadHead</c> originally caught only <see cref="NotFoundException"/> (404), so the 409 escaped
/// and aborted the export. The export's downstream logic already makes a parent-less first commit and
/// creates the branch ref — it just needs <c>ReadHead</c> to treat the 409 like "no head".</para>
/// </summary>
public class EmptyRepoToleranceTest
{
    [Fact]
    public void EmptyRepo_409Conflict_IsTreatedAsNoHead()
        => Assert.True(OctokitGitHubRepoClient.IsMissingOrEmptyRepo(
            new ApiException("Git Repository is empty.", HttpStatusCode.Conflict)));

    [Fact]
    public void MissingBranch_404NotFound_IsTreatedAsNoHead()
        => Assert.True(OctokitGitHubRepoClient.IsMissingOrEmptyRepo(
            new NotFoundException("Not Found", HttpStatusCode.NotFound)));

    [Fact]
    public void OtherApiErrors_AreNotSwallowed()
    {
        Assert.False(OctokitGitHubRepoClient.IsMissingOrEmptyRepo(
            new ApiException("boom", HttpStatusCode.InternalServerError)));
        Assert.False(OctokitGitHubRepoClient.IsMissingOrEmptyRepo(
            new ApiException("forbidden", HttpStatusCode.Forbidden)));
    }
}
