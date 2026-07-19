using Memex.Portal.Shared.Authentication;
using MeshWeaver.Graph.Configuration;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// Regression guard for the EA-credential <b>path mismatch</b>. <see cref="EaGraphAuth"/> stores a
/// user's Graph refresh token as a node and later loads it; the two sides MUST agree on the path.
/// A previous form built the node with <c>new MeshNode(EaCredentialNodeType.NodeType, PathFor(user))</c>,
/// which the <c>MeshNode(Id, Namespace)</c> ctor turned into <c>Auth/_EaCredential/{user}/EaCredential</c>
/// (Id = "EaCredential", one level too deep, NodeType unset) — while <see cref="EaGraphAuth"/> loads
/// <c>Auth/_EaCredential/{user}</c>. The token was stored but never found, so the Executive Assistant
/// reported "mailbox not connected" on every send, forever — undetectable by recycle or restart.
/// </summary>
public class EaCredentialNodeTest
{
    [Theory]
    [InlineData("rbuergi")]
    [InlineData("a1b2c3d4-0000-1111-2222-abcdef012345")]
    public void CredentialNode_WritePath_EqualsLoadPath(string userObjectId)
    {
        var node = EaGraphAuth.NewCredentialNode(userObjectId);

        // The whole bug in one assertion: the node we WRITE must live exactly where we READ.
        Assert.Equal(EaGraphAuth.PathFor(userObjectId), node.Path);
        Assert.Equal($"Auth/_EaCredential/{userObjectId}", node.Path);
    }

    [Fact]
    public void CredentialNode_CarriesTheEaCredentialNodeType()
    {
        var node = EaGraphAuth.NewCredentialNode("rbuergi");

        // NodeType must be set so the node's hub activates with the EaCredential data source
        // (WithContentType<EaCredential>) — otherwise the content never binds on load.
        Assert.Equal(EaCredentialNodeType.NodeType, node.NodeType);
        Assert.Equal("EaCredential", node.NodeType);
    }
}
