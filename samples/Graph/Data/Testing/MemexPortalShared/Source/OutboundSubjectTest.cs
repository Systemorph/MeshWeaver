// <meshweaver>
// Id: Testing/MemexPortalShared/OutboundSubjectTest
// DisplayName: Testing/MemexPortalShared/OutboundSubjectTest — migrated from xunit (convert-xunit-to-inmesh.py)
// </meshweaver>
#nullable enable
using MeshWeaver.Reactive.Assertions;
using MeshWeaver.Testing.InMesh;
using System;
using Memex.Portal.Shared.Email;

/// <summary>
/// The subject an outbound mail leaves with.
///
/// <para>The rule is "add a reply marker unless one is already there", and the whole defect was in
/// what counts as already there. <c>StartsWith("Re:")</c> is right for the case this lane was built
/// for — the Email Router's reply, whose subject the agent writes as <c>Re: &lt;original&gt;</c> —
/// and wrong for every outbound mail that is not a reply. The forward action of
/// Systemorph/MeshWeaver#2656 is what made it visible: the inbox lane queues
/// <c>Fwd: Quote for 200 seats</c> and it went out as <c>Re: Fwd: Quote for 200 seats</c>, telling
/// the recipient the opposite of what happened.</para>
/// </summary>
public class OutboundSubjectTest
{
    [MeshTheory]
    [MeshInlineData("Re: Project status")]
    [MeshInlineData("RE: Project status")]
    [MeshInlineData("re: Project status")]
    [MeshInlineData("Re[2]: Project status")]
    [MeshInlineData("AW: Project status")]              // German reply
    public void ASubjectThatIsAlreadyAReply_IsSentUnchanged(string subject)
        => OutboundEmailSender.OutboundSubject(subject).Should().Be(subject);

    [MeshTheory]
    [MeshInlineData("Fwd: Quote for 200 seats")]
    [MeshInlineData("FW: Quote for 200 seats")]
    [MeshInlineData("WG: Quote for 200 seats")]         // German forward
    [MeshInlineData("  Fwd: Quote for 200 seats")]      // whatever the mail client left in front
    public void ASubjectThatIsAlreadyAForward_IsNotTurnedIntoAReply(string subject)
        => OutboundEmailSender.OutboundSubject(subject).Should().Be(subject,
            "a forward that arrives as \"Re: Fwd: …\" tells the recipient the opposite of what "
            + "happened to it");

    [MeshFact]
    public void APlainSubject_StillGetsTheReplyMarker()
        => OutboundEmailSender.OutboundSubject("Project status")
            .Should().Be("Re: Project status",
                "the agent-reply case this lane was built for is unchanged");

    [MeshFact]
    public void AMarkerInTheMIDDLE_IsNotAPrefix()
        => OutboundEmailSender.OutboundSubject("Question about the RE: header")
            .Should().Be("Re: Question about the RE: header",
                "only a LEADING marker says the subject is already a reply or a forward");

    [MeshFact]
    public void AnEmptyOrNullSubject_DoesNotThrow()
    {
        OutboundEmailSender.OutboundSubject("").Should().Be("Re: ");
        OutboundEmailSender.OutboundSubject(null).Should().Be("Re: ");
    }
}
