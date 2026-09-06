using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Memex.Portal.Shared.Email;
using MeshWeaver.Fixture;                  // TestTimeouts.Convergence
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.Reactive.Assertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// #3473: <c>IEmailSender</c> took ONE recipient and had no Cc/Bcc, so a mail with copied
/// recipients could not be delivered as the message the sender approved.
///
/// <para><b>What went wrong.</b> Every <c>SendEmail</c> overload took a single
/// <c>toAddress</c>, so <c>SendDocumentDispatch.ExportAndSend</c> did the only thing available to
/// it: <c>emails.ToObservable().SelectMany(to =&gt; hub.SendEmail(to, …))</c> — one mail per
/// recipient. A To with seven Cc, the ordinary shape of a business mail, went out as eight separate
/// messages. None of them named the others, so nobody could see who else had it, Reply-All reached
/// only the sender, and the thread split into eight unrelated conversations. Bcc — whose entire
/// definition is "receives the message but is not on its visible header" — had no expressible form
/// at all: sending separately to a blind recipient produces a message whose header does not match
/// what anyone else got.</para>
///
/// <para><b>The two arms.</b> <see cref="AMessageWithCcAndBcc_IsDeliveredAsOneMessage"/> shows the
/// fixed shape with numbers, and <see cref="TheOldPerRecipientShape_SplitsOneMailIntoFour"/> shows
/// the same four recipients through the old surface — four sends, each blind to the others. That
/// second test is the defect, measured, and it stays green forever: it is what a single-address
/// sender still does, which is exactly why
/// <see cref="ASingleAddressSender_RefusesCopiedRecipients_RatherThanDroppingThem"/> makes it
/// unreachable by accident.</para>
/// </summary>
public class EmailCopiedRecipientsTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Primary = "client@example.com";
    private const string CopiedOne = "colleague@example.com";
    private const string CopiedTwo = "manager@example.com";
    private const string Blind = "archive@example.com";

    private readonly EnvelopeRecordingSender envelopeSender = new();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services => services.AddSingleton<IEmailSender>(envelopeSender));

    private static EmailMessage BusinessMail() => new()
    {
        To = [Primary],
        Cc = [CopiedOne, CopiedTwo],
        Bcc = [Blind],
        Subject = "Q3 review deck",
        HtmlBody = "<p>Attached.</p>",
    };

    // ── The fix, with numbers ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 🚨 ONE message, four recipients — and the visibility is right in both directions.
    ///
    /// <para>The counts are the assertion: <b>1</b> send carrying <b>4</b> recipients, of whom
    /// <b>3</b> can see each other and <b>1</b> cannot be seen. On the old surface the same mail was
    /// 4 sends carrying 1 recipient each, with 0 able to see anyone — see
    /// <see cref="TheOldPerRecipientShape_SplitsOneMailIntoFour"/>.</para>
    /// </summary>
    [Fact]
    public async Task AMessageWithCcAndBcc_IsDeliveredAsOneMessage()
    {
        var sent = await Mesh.SendEmail(BusinessMail())
            .Should().Within(TestTimeouts.Convergence).Emit();

        sent.Should().BeTrue();

        var envelopes = envelopeSender.Sent;
        Output.WriteLine($"sends: {envelopes.Count}");
        foreach (var envelope in envelopes)
            Output.WriteLine(
                $"  to=[{string.Join(", ", envelope.To)}] cc=[{string.Join(", ", envelope.Cc)}] "
                + $"bcc=[{string.Join(", ", envelope.Bcc)}]");

        envelopes.Should().HaveCount(1,
            "a message with copied recipients IS one message; sending it once per recipient "
            + "delivers a different mail than the one the sender approved");

        var only = envelopes[0];
        only.AllRecipients.Should().HaveCount(4);

        // Where they SHOULD see each other.
        only.VisibleRecipients.Should().Equal([Primary, CopiedOne, CopiedTwo],
            "To and Cc are the visible header — that mutual visibility is the whole point of a Cc, "
            + "and it is precisely what N separate mails cannot express");

        // Where they should NOT.
        only.VisibleRecipients.Should().NotContain(Blind,
            "a blind-copied recipient appearing on the visible header is the disclosure Bcc exists "
            + "to prevent");
        only.Bcc.Should().Equal([Blind]);
    }

    /// <summary>
    /// The defect, measured on the surface that produced it. Four recipients through the
    /// single-address surface become FOUR mails, and each one's visible header names exactly one
    /// person — so no recipient learns that any other received it, and Reply-All reaches nobody.
    ///
    /// <para>This is not a regression test for the fix; it is the CONTROL that gives the numbers in
    /// <see cref="AMessageWithCcAndBcc_IsDeliveredAsOneMessage"/> their meaning. It stays green,
    /// because a single-address sender still behaves this way — which is why the contract now
    /// refuses to route a copied message into it.</para>
    /// </summary>
    [Fact]
    public async Task TheOldPerRecipientShape_SplitsOneMailIntoFour()
    {
        var recorder = new SingleAddressRecordingSender();
        IEmailSender legacy = recorder;
        var everyone = new[] { Primary, CopiedOne, CopiedTwo, Blind };

        // Verbatim the shape SendDocumentDispatch.SendToAll used.
        var results = await everyone.ToObservable()
            .SelectMany(to => legacy.SendEmail(to, "Q3 review deck", "<p>Attached.</p>"))
            .ToList()
            .Should().Within(TestTimeouts.Convergence).Emit();

        results.Should().HaveCount(4);
        recorder.Sent.Should().HaveCount(4,
            "one mail per recipient — four messages where the sender composed one");

        foreach (var (to, _, _) in recorder.Sent)
            Output.WriteLine($"  separate mail to {to}");

        foreach (var address in everyone)
            recorder.Sent.Should().Contain(s => s.To == address,
                "each recipient got their OWN mail — that is the four-way split #3473 names");

        recorder.Sent.Should().OnlyContain(s => everyone.Contains(s.To),
            "each mail's visible header names ONE address, so no recipient can see any other, "
            + "and Bcc has no expressible form here at all");
    }

    // ── Refusing, never dropping ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// 🚨 A sender that cannot carry copied recipients REFUSES the message; it does not quietly
    /// deliver a smaller one.
    ///
    /// <para>Two assertions. The fault is the first — the caller who asked for a Cc learns it did
    /// not happen. Zero recorded sends is the second, and it is the one that discriminates: relax
    /// the default to "forward the To and drop the rest" and this records one send whose Cc has
    /// silently vanished, which is the same class of defect as stamping an undelivered mail
    /// <c>Sent</c> (#2023).</para>
    /// </summary>
    [Fact]
    public async Task ASingleAddressSender_RefusesCopiedRecipients_RatherThanDroppingThem()
    {
        var recorder = new SingleAddressRecordingSender();
        IEmailSender legacy = recorder;

        var notification = await legacy.SendEmail(BusinessMail()).Materialize()
            .Should().Within(TestTimeouts.Convergence).Emit();

        Output.WriteLine($"refusal: {notification.Kind}: {notification.Exception?.Message}");

        notification.Kind.Should().Be(NotificationKind.OnError,
            "a Cc that cannot be honoured is a refusal, not a smaller success");
        notification.Exception.Should().BeOfType<NotSupportedException>();
        notification.Exception!.Message.Should().Contain("Cc: 2");
        notification.Exception!.Message.Should().Contain("Bcc: 1",
            "the refusal names what could not be carried, so an operator can act on it");

        recorder.Sent.Should().BeEmpty(
            "NOTHING may go out — a mail delivered to fewer people than the sender addressed is "
            + "the silent data loss this refusal exists to prevent");
    }

    /// <summary>
    /// The additive proof: a message that FITS a single-address sender still reaches one, through
    /// the legacy overload, with its delivery identity intact. Without this the refusal above could
    /// "pass" by refusing everything and breaking every existing caller.
    /// </summary>
    [Fact]
    public async Task ASingleRecipientMessage_StillReachesAnOldSender()
    {
        var recorder = new SingleAddressRecordingSender();
        IEmailSender legacy = recorder;
        var identity = EmailDelivery.AsSharedMailboxReplyingTo("author@example.com");

        var sent = await legacy.SendEmail(new EmailMessage
        {
            To = [Primary],
            Subject = "One recipient",
            HtmlBody = "<p>Hi.</p>",
            Delivery = identity,
        }).Should().Within(TestTimeouts.Convergence).Emit();

        sent.Should().BeTrue();
        recorder.Sent.Should().HaveCount(1);
        recorder.Sent[0].To.Should().Be(Primary);
        recorder.Sent[0].Delivery.ReplyToAddress.Should().Be("author@example.com",
            "the sender identity travels with the message, not beside it");
    }

    /// <summary>A message nobody would receive is refused rather than reported as sent.</summary>
    [Fact]
    public async Task AMessageWithNoRecipients_IsRefused()
    {
        var recorder = new SingleAddressRecordingSender();
        IEmailSender legacy = recorder;

        var notification = await legacy
            .SendEmail(new EmailMessage { Subject = "Nobody", HtmlBody = "<p/>" })
            .Materialize().Should().Within(TestTimeouts.Convergence).Emit();

        notification.Kind.Should().Be(NotificationKind.OnError);
        notification.Exception!.Message.Should().Contain("no recipients");
        recorder.Sent.Should().BeEmpty();
    }

    // ── The no-op sender carries the whole envelope ───────────────────────────────────────────────

    /// <summary>
    /// The host's <see cref="NoOpEmailSender"/> ACCEPTS copied recipients when mail is switched off.
    /// It delivers nothing to anybody and says so, so there is no smaller message for it to deliver
    /// silently — and refusing here would break every local-dev and test send that carries a Cc,
    /// while protecting a delivery that is not happening either way.
    /// </summary>
    [Fact]
    public async Task TheNoOpSender_AcceptsCopiedRecipients_WhenMailIsOff()
    {
        var noOp = new NoOpEmailSender(new EmailOptions { Enabled = false });

        var sent = await noOp.SendEmail(BusinessMail())
            .Should().Within(TestTimeouts.Convergence).Emit();

        sent.Should().BeTrue("mail is off; nothing is claimed to have been delivered to anyone");
    }

    /// <summary>
    /// …and still REFUSES the whole envelope on a misconfigured install (#2023), rather than
    /// stamping a four-recipient mail delivered while nothing left the process.
    /// </summary>
    [Fact]
    public async Task TheNoOpSender_RefusesTheWholeEnvelope_WhenMailClaimsToBeOn()
    {
        var noOp = new NoOpEmailSender(new EmailOptions { Enabled = true });

        var notification = await noOp.SendEmail(BusinessMail()).Materialize()
            .Should().Within(TestTimeouts.Convergence).Emit();

        notification.Kind.Should().Be(NotificationKind.OnError,
            "a mail-enabled install with nothing to send with must fail visibly, copies included");
    }

    // ── The envelope's own rules ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Visible recipients are To then Cc, de-duplicated case-insensitively — and never Bcc.
    /// Pinned separately because this is the property a UI renders as "who will see this".
    /// </summary>
    [Fact]
    public void VisibleRecipients_AreToThenCc_DeduplicatedAndNeverBlind()
    {
        var message = new EmailMessage
        {
            To = [Primary, "CLIENT@example.com"],
            Cc = [CopiedOne],
            Bcc = [Blind],
            Subject = "s",
            HtmlBody = "b",
        };

        message.VisibleRecipients.Should().Equal([Primary, CopiedOne]);
        message.AllRecipients.Should().Equal([Primary, CopiedOne, Blind]);
        message.HasCopiedRecipients.Should().BeTrue();
        message.FitsASingleAddressSender.Should().BeFalse();
    }

    /// <summary>The simple case is still simple, and still fits every sender ever written.</summary>
    [Fact]
    public void AddressedTo_OneRecipient_FitsASingleAddressSender()
    {
        var message = EmailMessage.Addressed(Primary, "s", "b");

        message.FitsASingleAddressSender.Should().BeTrue();
        message.HasCopiedRecipients.Should().BeFalse();
        message.VisibleRecipients.Should().Equal([Primary]);
    }

    // ── Senders ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>Records the whole envelope of every send — a real sender, not a mock.</summary>
    private sealed class EnvelopeRecordingSender : IEmailSender
    {
        private readonly ConcurrentQueue<EmailMessage> sent = new();

        public IReadOnlyList<EmailMessage> Sent => [.. sent];

        public IObservable<bool> SendEmail(EmailMessage message)
        {
            sent.Enqueue(message);
            return Observable.Return(true);
        }

        public IObservable<bool> SendEmail(string toAddress, string subject, string htmlBody)
            => SendEmail(EmailMessage.Addressed(toAddress, subject, htmlBody));

        public IObservable<bool> SendEmail(
            string toAddress, string subject, string htmlBody,
            IReadOnlyCollection<EmailAttachment> attachments)
            => SendEmail(EmailMessage.Addressed(toAddress, subject, htmlBody) with
            {
                Attachments = attachments,
            });
    }

    /// <summary>
    /// A sender written against the PRE-#3473 contract: one address per send, no envelope member.
    /// The absence of <c>SendEmail(EmailMessage)</c> is the assertion — it is what makes the
    /// interface's default refusal the thing under test.
    /// </summary>
    private sealed class SingleAddressRecordingSender : IEmailSender
    {
        private readonly ConcurrentQueue<(string To, string Subject, EmailDelivery Delivery)> sent = new();

        public IReadOnlyList<(string To, string Subject, EmailDelivery Delivery)> Sent => [.. sent];

        public IObservable<bool> SendEmail(string toAddress, string subject, string htmlBody)
            => SendEmail(toAddress, subject, htmlBody, [], EmailDelivery.AsSharedMailbox);

        public IObservable<bool> SendEmail(
            string toAddress, string subject, string htmlBody,
            IReadOnlyCollection<EmailAttachment> attachments)
            => SendEmail(toAddress, subject, htmlBody, attachments, EmailDelivery.AsSharedMailbox);

        public IObservable<bool> SendEmail(
            string toAddress, string subject, string htmlBody,
            IReadOnlyCollection<EmailAttachment> attachments, EmailDelivery delivery)
        {
            sent.Enqueue((toAddress, subject, delivery));
            return Observable.Return(true);
        }
    }
}
