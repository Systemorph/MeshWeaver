using Memex.Portal.Shared.Email;
using MeshWeaver.Mesh;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// Unit tests for <see cref="InvitationEmailSender.BuildInviteEmail"/> — the pure email-body
/// builder. A space-scoped invitation (<see cref="Invitation.SpacePath"/> set) addresses the
/// Space by name and links straight to it; a deployment-wide invitation keeps the generic
/// "invited to Memex" wording.
/// </summary>
public class InvitationEmailTest
{
    [Fact]
    public void SpaceInvite_AddressesSpaceByName_AndLinksToTheSpace()
    {
        var invitation = new Invitation { Email = "carol@acme.com", SpacePath = "TeamSpace" };

        var (subject, html) = InvitationEmailSender.BuildInviteEmail(
            invitation, "Team Space", "https://memex.example.com");

        Assert.Equal("You've been invited to Team Space", subject);
        Assert.Contains("Team Space", html);
        // Links to the Space itself, not just the portal root.
        Assert.Contains("https://memex.example.com/TeamSpace", html);
        Assert.DoesNotContain("invited to Memex", html);
    }

    [Fact]
    public void SpaceInvite_FallsBackToThePath_WhenTheNameIsUnresolved()
    {
        var invitation = new Invitation { Email = "carol@acme.com", SpacePath = "TeamSpace" };

        // Trailing slash on the base url must not double up in the link.
        var (subject, html) = InvitationEmailSender.BuildInviteEmail(
            invitation, spaceName: null, "https://memex.example.com/");

        Assert.Equal("You've been invited to TeamSpace", subject);
        Assert.Contains("https://memex.example.com/TeamSpace", html);
    }

    [Fact]
    public void GlobalInvite_KeepsTheGenericMemexWording()
    {
        var invitation = new Invitation { Email = "dave@acme.com" };

        var (subject, html) = InvitationEmailSender.BuildInviteEmail(
            invitation, spaceName: null, "https://memex.example.com");

        Assert.Equal("You've been invited to Memex", subject);
        Assert.Contains("invited to Memex", html);
    }
}
