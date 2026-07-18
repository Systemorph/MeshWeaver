using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Markdown.Export.Configuration;
using MeshWeaver.Markdown.Export.Handlers;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using UglyToad.PdfPig;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// End-to-end test for <b>"send a deck/document to contacts"</b> (issue #423, piece 2). Verifies that
/// <see cref="SendDocumentDispatch.ExportAndSend"/> reuses the SAME node ⇒ file export pipeline as the
/// download (<c>ExportDocumentRequest → Templates/Export/Pdf → RenderedDocument bytes</c>) and then
/// emails those bytes as a PDF <see cref="EmailAttachment"/> via the new <see cref="IEmailSender"/>
/// attachment overload — to a recipient resolved from a User node PATH (email read under the caller's
/// identity) AND to a raw email address. A capturing <see cref="IEmailSender"/> (no mocking) records
/// every send so the test can assert the attachment bytes/mime/filename and the recipient.
/// <para>Mirrors <c>DeckExportScriptRelayTest</c> for the deck/slide seeding.</para>
/// </summary>
public class SendDocumentToContactsTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private readonly CapturingEmailSender _email = new();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddMarkdownExport()
            .ConfigureServices(s => s.AddSingleton<IEmailSender>(_email));

    [Fact(Timeout = 180000)]
    public async Task SendDeckToContacts_EmailsPdfAttachment_ToUserAndRawAddress()
    {
        // ── Seed a Space holding a Deck with three ordered Slide children (as DeckExportScriptRelayTest). ──
        var space = $"Space{Guid.NewGuid():N}"[..16];
        await NodeFactory.CreateNode(MeshNode.FromPath(space) with
        {
            Name = "Send Deck Space",
            NodeType = SpaceNodeType.NodeType,
            Content = new Space()
        }).Should().Emit();

        var deck = $"{space}/pitch";
        await NodeFactory.CreateNode(MeshNode.FromPath(deck) with
        {
            Name = "Pitch Deck",
            NodeType = DeckNodeType.NodeType,
            Content = new DeckContent { Title = "Pitch Deck", Slides = [$"{deck}/intro", $"{deck}/summary"] }
        }).Should().Emit();
        await CreateSlide($"{deck}/intro", "Intro", 1, "# Introduction\n\nAbout ALPHAWIDGET.");
        await CreateSlide($"{deck}/summary", "Summary", 2, "# Summary\n\nThoughts on GAMMAWIDGET.");

        // ── Seed a recipient User node (top-level partition root → System-seeded) with an email. ──
        var recipientEmail = "bob.recipient@example.com";
        var userPath = $"user{Guid.NewGuid():N}"[..16];
        await SeedTopLevel(MeshNode.FromPath(userPath) with
        {
            Name = "Bob Recipient",
            NodeType = UserNodeType.NodeType,
            State = MeshNodeState.Active,
            Content = new User { Email = recipientEmail, FullName = "Bob Recipient" }
        });

        var workspace = GetClient(c => c.AddData()).GetWorkspace();
        var options = new DocumentExportOptions { Format = ExportFormat.Pdf };

        // ── Act: send the deck to the picked User (by PATH) AND a raw email address. ──
        const string rawEmail = "carol.external@example.com";
        const string subject = "Please review our pitch";
        var result = await SendDocumentDispatch.ExportAndSend(
                Mesh, workspace, deck, options,
                recipientUserPaths: [userPath],
                rawEmails: [rawEmail],
                subject: subject,
                htmlBody: "<p>Here is the deck.</p>")
            .Should().Within(2.Minutes()).Emit();

        // ── Assert: send succeeded to BOTH recipients. ──
        result.Should().NotBeNull();
        result.Error.Should().BeNullOrEmpty("the send should succeed. Error: " + result.Error);
        result.Success.Should().BeTrue();
        result.ActivityPath.Should().NotBeNullOrEmpty("the export activity path should be recorded");
        result.SentTo.Should().Contain(recipientEmail).And.Contain(rawEmail);

        _email.Sent.Should().HaveCount(2, "one email per resolved recipient");

        // ── Assert the resolved-USER email carries the PDF attachment (non-empty, correct mime/name). ──
        var toUser = _email.Sent.Single(m => m.To == recipientEmail);
        toUser.Subject.Should().Be(subject);
        toUser.Attachments.Should().ContainSingle();
        var attachment = toUser.Attachments.Single();
        attachment.MimeType.Should().Be("application/pdf");
        attachment.FileName.Should().EndWith(".pdf");
        attachment.Content.Should().NotBeNull().And.NotBeEmpty();

        // The attachment is a real, content-faithful PDF: re-read it and prove a slide's text is present.
        using (var pdf = PdfDocument.Open(attachment.Content))
        {
            var text = string.Join("\n", pdf.GetPages().Select(p => p.Text));
            text.Should().Contain("ALPHAWIDGET", "the emailed PDF must contain the deck's rendered slide content");
            text.Should().Contain("GAMMAWIDGET");
        }

        // ── The raw-address recipient gets the SAME attachment. ──
        var toRaw = _email.Sent.Single(m => m.To == rawEmail);
        toRaw.Attachments.Should().ContainSingle();
        toRaw.Attachments.Single().Content.Should().NotBeNull().And.NotBeEmpty();

        await NodeFactory.DeleteNode(space).Should().Emit();
    }

    [Fact(Timeout = 60000)]
    public async Task NoAttachmentSendEmail_StillWorks()
    {
        // The additive attachment overload must not disturb existing no-attachment callers
        // (invitation / notification email). The plain 3-arg SendEmail still sends with 0 attachments.
        var ok = await Mesh.SendEmail("alice@example.com", "Plain notification", "<p>Hello.</p>")
            .Should().Within(10.Seconds()).Emit();

        ok.Should().BeTrue();
        _email.Sent.Should().ContainSingle();
        var sent = _email.Sent.Single();
        sent.To.Should().Be("alice@example.com");
        sent.Subject.Should().Be("Plain notification");
        sent.Attachments.Should().BeEmpty("the no-attachment path carries no files");
    }

    private async Task CreateSlide(string path, string name, int order, string body)
        => await NodeFactory.CreateNode(MeshNode.FromPath(path) with
        {
            Name = name,
            NodeType = SlideNodeType.NodeType,
            Order = order,
            Content = new SlideContent { Content = body, Notes = $"Notes for {name}" }
        }).Should().Emit();

    /// <summary>Capturing <see cref="IEmailSender"/> — records every send (no mocking) for assertions.</summary>
    private sealed class CapturingEmailSender : IEmailSender
    {
        public ConcurrentQueue<SentEmail> SentQueue { get; } = new();
        public IReadOnlyList<SentEmail> Sent => SentQueue.ToArray();

        public IObservable<bool> SendEmail(string toAddress, string subject, string htmlBody)
            => SendEmail(toAddress, subject, htmlBody, []);

        public IObservable<bool> SendEmail(
            string toAddress, string subject, string htmlBody, IReadOnlyCollection<EmailAttachment> attachments)
        {
            SentQueue.Enqueue(new SentEmail(toAddress, subject, htmlBody, attachments.ToList()));
            return Observable.Return(true);
        }
    }

    private sealed record SentEmail(
        string To, string Subject, string Body, IReadOnlyList<EmailAttachment> Attachments);
}
