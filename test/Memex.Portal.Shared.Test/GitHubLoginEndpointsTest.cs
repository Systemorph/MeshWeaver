using Memex.Portal.Shared.Social;
using Xunit;

namespace Memex.Portal.Shared.Test;

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
    [Theory]
    [InlineData(null, "/")]                                   // no return → home
    [InlineData("", "/")]                                     // empty → home
    [InlineData("   ", "/")]                                  // whitespace → home
    [InlineData("/AgenticEngineering/Training", "/AgenticEngineering/Training")] // local path preserved
    [InlineData("/space/x?tab=1", "/space/x?tab=1")]          // local path + query preserved
    [InlineData("https://evil.example.com", "/")]            // absolute URL rejected
    [InlineData("http://evil.example.com/x", "/")]           // absolute URL rejected
    [InlineData("//evil.example.com", "/")]                  // protocol-relative rejected
    [InlineData("evil.example.com", "/")]                    // bare host (no leading slash) rejected
    public void SafeLocal_RejectsExternalRedirects_KeepsLocalPaths(string? input, string expected) =>
        Assert.Equal(expected, GitHubLoginEndpoints.SafeLocal(input));

    [Fact]
    public void IsGitHubUserAllowed_AdmitsEveryone_Today()
    {
        // The deployment decision: any GitHub account may sign in (same access as an Entra user).
        // If this ever tightens to an allowlist, this test documents the change point.
        Assert.True(GitHubLoginEndpoints.IsGitHubUserAllowed("octocat", "octocat@example.com"));
        Assert.True(GitHubLoginEndpoints.IsGitHubUserAllowed(null, "someone@example.com"));
    }
}
