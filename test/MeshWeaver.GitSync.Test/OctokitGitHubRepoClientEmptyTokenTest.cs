#pragma warning disable CS1591

using System;
using MeshWeaver.GitSync;
using MeshWeaver.Mesh.Threading;
using Xunit;

namespace MeshWeaver.GitSync.Test;

/// <summary>
/// Regression: an EMPTY access token must mean ANONYMOUS access, not a crash.
/// <see cref="OctokitGitHubRepoClient.Fetch(string, string, string?, string)"/> builds its Octokit
/// client EAGERLY (at call time, before the observable is subscribed), so an empty token used to
/// throw <c>ArgumentException("String cannot be empty (Parameter 'token')")</c> from Octokit's
/// <c>new Credentials("")</c> right there — which is exactly what 500'd the plugin registry's
/// <c>/api/plugins</c> when a URL source resolved no App token. The client must now build a
/// credential-less (anonymous) client instead of throwing.
/// </summary>
public class OctokitGitHubRepoClientEmptyTokenTest
{
    [Fact]
    public void Fetch_WithEmptyToken_DoesNotThrowOnConstruction()
    {
        var client = new OctokitGitHubRepoClient(new IoPoolRegistry());

        // Building the Fetch pipeline (which eagerly constructs the Octokit client with the token)
        // must not throw for an empty token — the old `new Credentials("")` ArgumentException is the
        // registry's 500. We never subscribe, so no network call is made.
        var ex = Record.Exception(() =>
            _ = client.Fetch("https://github.com/acme/public-repo", "main", null, ""));

        Assert.Null(ex);
    }
}
