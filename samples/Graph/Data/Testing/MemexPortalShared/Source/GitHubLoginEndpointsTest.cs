// <meshweaver>
// Id: Testing/MemexPortalShared/GitHubLoginEndpointsTest
// DisplayName: Testing/MemexPortalShared/GitHubLoginEndpointsTest — migrated from xunit (convert-xunit-to-inmesh.py)
// </meshweaver>
#nullable enable
using MeshWeaver.Reactive.Assertions;
using MeshWeaver.Testing.InMesh;
using System;
using Memex.Portal.Shared.Social;

/// <summary>
/// Security-critical logic of the "Sign in with GitHub" provider: the open-redirect guard on the
/// post-login return path (an attacker must not be able to bounce a freshly-authenticated user to an
/// external site), and the sign-in allow-predicate (which today admits everyone per the deployment
/// decision, and is the single place to tighten to an org allowlist later). The full authorize →
/// callback → cookie flow + the GitHub email fetch are integration-level (HTTP + cookies) and are
/// exercised by the e2e portal stack.
/// </summary>
public class GitHubLoginEndpointsTest
{
    [MeshTheory]
    [MeshInlineData(null, "/")]                                   // no return → home
    [MeshInlineData("", "/")]                                     // empty → home
    [MeshInlineData("   ", "/")]                                  // whitespace → home
    [MeshInlineData("/AgenticEngineering/Training", "/AgenticEngineering/Training")] // local path preserved
    [MeshInlineData("/space/x?tab=1", "/space/x?tab=1")]          // local path + query preserved
    [MeshInlineData("https://evil.example.com", "/")]            // absolute URL rejected
    [MeshInlineData("http://evil.example.com/x", "/")]           // absolute URL rejected
    [MeshInlineData("//evil.example.com", "/")]                  // protocol-relative rejected
    [MeshInlineData("evil.example.com", "/")]                    // bare host (no leading slash) rejected
    public void SafeLocal_RejectsExternalRedirects_KeepsLocalPaths(string? input, string expected) =>
        Assert.Equal(expected, GitHubLoginEndpoints.SafeLocal(input));

    [MeshFact]
    public void IsGitHubUserAllowed_AdmitsEveryone_Today()
    {
        // The deployment decision: any GitHub account may sign in (same access as an Entra user).
        // If this ever tightens to an allowlist, this test documents the change point.
        Assert.True(GitHubLoginEndpoints.IsGitHubUserAllowed("octocat", "octocat@example.com"));
        Assert.True(GitHubLoginEndpoints.IsGitHubUserAllowed(null, "someone@example.com"));
    }
}
