using Memex.Portal.Shared.Email;
using Xunit;

namespace MeshWeaver.Auth.Test;

/// <summary>
/// Pins the conversation-matching key: an inbound email is matched to its conversation by the
/// <b>normalized subject</b>, so any number of <c>Re:</c>/<c>Fwd:</c>/<c>AW:</c>… layers the sender's
/// mail client piles on still resolve to the <b>same</b> thread. This is the deterministic core of
/// "multiple replies end up in the same thread"; the live email→agent→reply round-trip is exercised
/// separately (needs a chat client).
/// </summary>
public class EmailThreadKeyTests
{
    [Theory]
    [InlineData("Project status")]
    [InlineData("Re: Project status")]
    [InlineData("RE: Project status")]
    [InlineData("re: Project status")]
    [InlineData("Fwd: Project status")]
    [InlineData("FW: Project status")]
    [InlineData("Re: Fwd: Project status")]
    [InlineData("Re: RE: Fwd: Project status")]
    [InlineData("AW: WG: Project status")]            // German Re:/Fwd:
    [InlineData("Re[2]: Project status")]             // numbered reply
    [InlineData("  Re :  Fwd:  Project   status  ")]  // stray whitespace
    public void ThreadKey_CollapsesAnyReplyOrForwardPrefixes_ToOneConversation(string subject)
    {
        EmailInboundProcessor.ThreadKey(subject).Should().Be("project-status");
    }

    [Fact]
    public void ThreadKey_DistinctSubjects_AreDistinctConversations()
    {
        EmailInboundProcessor.ThreadKey("Project status")
            .Should().NotBe(EmailInboundProcessor.ThreadKey("Invoice 42"));
    }

    [Fact]
    public void ThreadKey_EmptyOrPrefixOnly_FallsBackToStableKey()
    {
        EmailInboundProcessor.ThreadKey("Re: Fwd:").Should().NotBeNullOrEmpty();
        EmailInboundProcessor.ThreadKey("").Should().Be("email");
    }
}
