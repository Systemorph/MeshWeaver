using System.Net;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// #7482: <see cref="InvitationEmail.Build"/> renders the invitation in the language stored on it
/// (<see cref="Invitation.Locale"/>): every line from the catalog in that language, the standing footer
/// included. English is the control: the same invitation with <c>en</c>, or with no language at all
/// (written before the field existed), renders English.
/// </summary>
public class InvitationEmailTest
{
    private static Invitation SpaceInvite(string? locale) => new()
    {
        Email = "invitee@acme.com",
        SpacePath = "TeamSpace",
        Locale = locale,
        Note = "Invited to space TeamSpace",
        Message = "Welcome aboard!",
    };

    [Fact]
    public void AGermanInvitation_IsWrittenInGerman()
    {
        var (subject, html) = InvitationEmail.Build(SpaceInvite("de"), "Team Space", "https://portal.example.com/");
        var text = WebUtility.HtmlDecode(html);

        Assert.Equal("Sie wurden zu Team Space eingeladen", subject);
        Assert.Contains("Sie wurden zu „Team Space“ auf Memex eingeladen.", text);
        Assert.Contains("Team Space öffnen", text);
        Assert.Contains("Melden Sie sich mit dieser E-Mail-Adresse an, um loszulegen.", text);
        Assert.Contains(LocalizationCatalog.Get("notification.email.standingFooter", "de"), text);
        Assert.DoesNotContain(LocalizationCatalog.Get("notification.email.standingFooter", "en"), text);
        Assert.Contains("https://portal.example.com/TeamSpace", text);
    }

    [Theory]
    [InlineData("en")]
    [InlineData(null)]
    public void AnEnglishOrUnlabelledInvitation_IsWrittenInEnglish(string? locale)
    {
        var (subject, html) = InvitationEmail.Build(SpaceInvite(locale), "Team Space", "https://portal.example.com");
        var text = WebUtility.HtmlDecode(html);

        Assert.Equal("You've been invited to Team Space", subject);
        Assert.Contains("You've been invited to \"Team Space\" on Memex.", text);
        Assert.Contains("Open Team Space", text);
        Assert.Contains(LocalizationCatalog.Get("notification.email.standingFooter", "en"), text);
    }

    [Fact]
    public void TheDeploymentWideInvitation_IsWrittenInTheStoredLanguageToo()
    {
        var (subject, html) = InvitationEmail.Build(
            new Invitation { Email = "invitee@acme.com", Locale = "de" }, null, "https://portal.example.com");

        Assert.Equal("Sie wurden zu Memex eingeladen", subject);
        Assert.Contains("Memex öffnen", WebUtility.HtmlDecode(html));
    }

    [Fact]
    public void TheHostsMessage_IsInTheEmail_AndTheAdminNoteIsNot()
    {
        var (_, html) = InvitationEmail.Build(SpaceInvite("de"), "Team Space", null);
        var text = WebUtility.HtmlDecode(html);

        Assert.Contains("Welcome aboard!", text);
        Assert.DoesNotContain("Invited to space", text);
        Assert.DoesNotContain("href=", html);
    }
}
