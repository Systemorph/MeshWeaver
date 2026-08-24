using Memex.Portal.Shared.Authentication;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The <c>client_id</c> handed out by Dynamic Client Registration must be STABLE for a given client.
/// It used to be random, which meant an MCP client — which re-registers on every reconnect — presented
/// as a brand-new application each time, so the user was asked to consent again on every connection.
/// </summary>
public class OAuthClientIdDerivationTest
{
    private const string Origin = "https://memex.meshweaver.cloud";

    [Fact]
    public void SameClientRegisteringTwiceGetsTheSameId()
    {
        var a = OAuthConnectController.DeriveClientId(Origin, "claude-code", ["http://localhost:8765/callback"]);
        var b = OAuthConnectController.DeriveClientId(Origin, "claude-code", ["http://localhost:8765/callback"]);
        Assert.Equal(a, b);
    }

    [Fact]
    public void RedirectUriOrderDoesNotChangeTheId()
    {
        var a = OAuthConnectController.DeriveClientId(Origin, "c", ["https://x/cb", "https://y/cb"]);
        var b = OAuthConnectController.DeriveClientId(Origin, "c", ["https://y/cb", "https://x/cb"]);
        Assert.Equal(a, b);
    }

    [Fact]
    public void ADifferentClientNameGivesADifferentId()
    {
        var a = OAuthConnectController.DeriveClientId(Origin, "claude-code", ["http://localhost:8765/callback"]);
        var b = OAuthConnectController.DeriveClientId(Origin, "some-other-client", ["http://localhost:8765/callback"]);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ADifferentRedirectUriGivesADifferentId()
    {
        var a = OAuthConnectController.DeriveClientId(Origin, "claude-code", ["http://localhost:8765/callback"]);
        var b = OAuthConnectController.DeriveClientId(Origin, "claude-code", ["http://localhost:9999/callback"]);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void TheSameClientGetsDifferentIdsOnDifferentDeployments()
    {
        var a = OAuthConnectController.DeriveClientId("https://memex.meshweaver.cloud", "c", ["https://x/cb"]);
        var b = OAuthConnectController.DeriveClientId("https://memex.systemorph.com", "c", ["https://x/cb"]);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void TheIdIsUrlSafeAndNonEmpty()
    {
        var id = OAuthConnectController.DeriveClientId(Origin, "claude-code", ["http://localhost:8765/callback"]);
        Assert.False(string.IsNullOrWhiteSpace(id));
        Assert.DoesNotContain("+", id);
        Assert.DoesNotContain("/", id);
        Assert.DoesNotContain("=", id);
    }
}
