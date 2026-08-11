using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Markdown;
using MeshWeaver.Markdown.Export.Configuration;
using MeshWeaver.Markdown.Export.Email;
using MeshWeaver.Markdown.Export.Handlers;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// End-to-end tests for <b>emailing a document as the message body</b>.
///
/// <para>The defect these pin: a markdown document can embed a LIVE layout area with
/// <c>@@(…)</c>, and every export before this one lost it — the PDF/DOCX pipeline feeds raw
/// markdown into a Markdig pipeline that never heard of the embed syntax, so it lands as literal
/// source text; the pixel path emits the empty placeholder div the browser would have filled and
/// prints a blank. The email path must RESOLVE the area to real markup server-side.</para>
///
/// <para>Everything is asserted on the email a capturing <see cref="IEmailSender"/> actually
/// received — no mocking of the mesh, and no inspecting of intermediate state that a real
/// recipient would never see.</para>
/// </summary>
public class EmailDocumentExportTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string BaseUrl = "https://portal.example.com";
    private const string SenderObjectId = "11111111-2222-3333-4444-555555555555";

    private readonly CapturingEmailSender _email = new();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddMarkdownExport()
            .ConfigureServices(s => s.AddSingleton<IEmailSender>(_email));

    [Fact(Timeout = 180000)]
    public async Task EmailDocument_AsBody_ResolvesEmbeddedLayoutArea_AndIsMailSafe()
    {
        var space = $"Space{Guid.NewGuid():N}"[..16];
        await NodeFactory.CreateNode(MeshNode.FromPath(space) with
        {
            Name = "Email Export Space",
            NodeType = SpaceNodeType.NodeType,
            Content = new Space()
        }).Should().Emit();

        // The card TARGET — a real mesh node, so the OgCard area resolves off its node stream
        // with no outbound HTTP. Its name and description are what must appear in the mail.
        var target = $"{space}/QuantumLedger";
        await NodeFactory.CreateNode(MeshNode.FromPath(target) with
        {
            Name = "Quantum Ledger",
            Description = "Reconciliation across the settlement mesh.",
            NodeType = "Markdown",
            Content = new MarkdownContent { Content = "# Quantum Ledger" }
        }).Should().Emit();

        // The document: prose, a TABLE (column widths must land on every cell) and a live OgCard
        // embed pointing at the node above.
        var doc = $"{space}/Proposal";
        var markdown =
            "# Proposal\n\n"
            + "Please review the [details](/Some/Relative/Page) before Friday.\n\n"
            + "| No. | Component | Notes |\n"
            + "| --- | --- | --- |\n"
            + "| 1 | Ledger | Handles reconciliation across every settlement partition nightly. |\n"
            + "| 2 | Pricing | Curve fitting. |\n\n"
            + $"@@(\"{doc}/area/OgCard/{target}\")\n";

        await NodeFactory.CreateNode(MeshNode.FromPath(doc) with
        {
            Name = "Proposal",
            NodeType = "Markdown",
            Content = new MarkdownContent { Content = markdown }
        }).Should().Emit();

        var workspace = GetClient(c => c.AddData()).GetWorkspace();
        var options = new DocumentExportOptions { Format = ExportFormat.Html, BaseUrl = BaseUrl };

        const string recipient = "dana.reader@example.com";
        var result = await SendDocumentDispatch.ExportAndSend(
                Mesh, workspace, doc, options,
                recipientUserPaths: [],
                rawEmails: [recipient],
                subject: "Proposal for review",
                htmlBody: "<p>Hi, please see below.</p>",
                delivery: DocumentDelivery.EmailBody,
                identity: EmailDelivery.AsUser(SenderObjectId))
            .Should().Within(2.Minutes()).Emit();

        result.Error.Should().BeNullOrEmpty("the send should succeed. Error: " + result.Error);
        result.Success.Should().BeTrue();

        var sent = _email.Sent.Should().ContainSingle().Which;
        var body = sent.Body;

        // ── The layout area is RESOLVED, not an empty anchor ────────────────────────────────
        body.Should().NotContain("layout-area",
            "the placeholder anchor must be replaced by rendered markup, never shipped as an empty div");
        body.Should().Contain("Quantum Ledger",
            "the embedded OgCard area must be rendered into the email as a real card");
        body.Should().Contain("Reconciliation across the settlement mesh",
            "the card's description comes from the target node's live stream");

        // ── Mail-safe: no script, no external stylesheet, no head styles, no inline SVG ──────
        body.Should().NotContain("<script", "no mail client executes JS and many quarantine it");
        body.Should().NotContain("<style", "Gmail/Outlook.com strip head styles — CSS must be inline");
        body.Should().NotContain("<link", "an external stylesheet cannot load in a mail client");
        body.Should().NotContain("<svg", "Outlook renders through Word, which shows inline SVG as a broken box");
        body.Should().NotContain("display:grid", "Word supports neither grid nor flex");
        body.Should().NotContain("display:flex");
        body.Should().Contain("style=\"", "styling has to ride inline on the elements");

        // ── Links are ABSOLUTE — a relative href is dead when clicked from an inbox ──────────
        body.Should().Contain($"{BaseUrl}/Some/Relative/Page");
        Regex.Matches(body, "href=\"(?!https?:|mailto:|cid:|#)([^\"]*)\"")
            .Should().BeEmpty("every href must be absolute in an email");

        // ── The covering note survived, above the document ──────────────────────────────────
        body.Should().Contain("please see below");

        // ── Table columns carry per-cell widths (Word ignores colgroup) ─────────────────────
        body.Should().NotContain("<colgroup", "Word ignores it; the widths live on the cells");
        var cellWidths = Regex.Matches(body, "<t[dh][^>]*width=\"([0-9.]+)%\"")
            .Select(m => double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))
            .ToList();
        cellWidths.Should().NotBeEmpty("every table cell needs an explicit width for Word");
        cellWidths.Should().HaveCountGreaterThanOrEqualTo(9, "3 columns × 3 rows of the markdown table");

        // Content-proportional sizing: the prose column must be wider than the "No." column.
        var headerWidths = cellWidths.Take(3).ToList();
        headerWidths[2].Should().BeGreaterThan(headerWidths[0],
            "the Notes column carries far more text than No. and must be sized wider");

        // Body delivery attaches NOTHING to open — only cid parts, and this document has no images.
        sent.Attachments.Should().OnlyContain(a => a.IsInline,
            "sending the document AS the mail must not also attach a file to open");

        // ── The sender identity reaches the mail sender ─────────────────────────────────────
        // Sharing is a personal act: it must go out as the PERSON, not the portal's mailbox.
        sent.Delivery.SendAsUserObjectId.Should().Be(SenderObjectId);
        sent.Delivery.IsUserIdentity.Should().BeTrue();

        await NodeFactory.DeleteNode(space).Should().Emit();
    }

    [Fact(Timeout = 60000)]
    public void EmbeddedPicture_BecomesInlineContentIdPart()
    {
        // A 1×1 transparent GIF authored as a data URI — the shape a browser is happy with and
        // classic Outlook for Windows silently strips. It must become a cid: part instead.
        const string gif = "data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7";
        var html = $"<div><p>Before</p><img src=\"{gif}\" alt=\"dot\" />"
                   + "<img src=\"https://cdn.example.com/remote.png\" /></div>";

        var result = EmailImageInliner.ToContentIdParts(html);

        result.Attachments.Should().ContainSingle("the data URI is the only embeddable picture");
        var part = result.Attachments.Single();
        part.IsInline.Should().BeTrue();
        part.ContentId.Should().NotBeNullOrEmpty();
        part.MimeType.Should().Be("image/gif");
        part.Content.Should().NotBeEmpty();
        part.FileName.Should().EndWith(".gif");

        result.Html.Should().Contain($"cid:{part.ContentId}",
            "the body must reference the part by its content ID");
        result.Html.Should().NotContain("data:image/gif",
            "the data URI must be gone — classic Outlook strips it");

        result.Remote.Should().ContainSingle()
            .Which.Should().Be("https://cdn.example.com/remote.png",
                "a remote image is reported, since clients hide it until pictures are allowed");
    }

    [Fact(Timeout = 60000)]
    public void SharedMailboxFallback_CarriesReplyToTheSender()
    {
        // When the message cannot go out in the person's name, the From is generic — so a reply
        // must still reach the human rather than a system inbox nobody answers from.
        var delivery = EmailDelivery.AsSharedMailboxReplyingTo("erin.sender@example.com");

        delivery.IsUserIdentity.Should().BeFalse("this is the shared-mailbox path");
        delivery.SendAsUserObjectId.Should().BeNull();
        delivery.ReplyToAddress.Should().Be("erin.sender@example.com");

        EmailDelivery.AsSharedMailbox.ReplyToAddress.Should().BeNull(
            "a pure system send has no human to reply to");
        EmailDelivery.AsUser(SenderObjectId).IsUserIdentity.Should().BeTrue();
    }

    [Fact(Timeout = 60000)]
    public void Absolutize_LeavesAbsoluteAndAnchorLinksAlone()
    {
        EmailHtmlSanitizer.Absolutize("/Node/Path", BaseUrl).Should().Be($"{BaseUrl}/Node/Path");
        EmailHtmlSanitizer.Absolutize("Node/Path", BaseUrl).Should().Be($"{BaseUrl}/Node/Path");
        EmailHtmlSanitizer.Absolutize("https://other.example.com/x", BaseUrl)
            .Should().Be("https://other.example.com/x");
        EmailHtmlSanitizer.Absolutize("mailto:a@b.com", BaseUrl).Should().Be("mailto:a@b.com");
        EmailHtmlSanitizer.Absolutize("#section", BaseUrl).Should().Be("#section",
            "a fragment points inside the mail body itself");
        EmailHtmlSanitizer.Absolutize("//cdn.example.com/x.png", BaseUrl)
            .Should().Be("https://cdn.example.com/x.png");
    }

    /// <summary>
    /// Capturing <see cref="IEmailSender"/> — records every send, INCLUDING the sender identity
    /// it was asked to use (no mocking). Implementing the identity overload is the point: the
    /// default interface implementation drops <see cref="EmailDelivery"/>, so a sender that
    /// silently ignored the identity would look identical to one that honoured it.
    /// </summary>
    private sealed class CapturingEmailSender : IEmailSender
    {
        public ConcurrentQueue<SentEmail> SentQueue { get; } = new();
        public IReadOnlyList<SentEmail> Sent => SentQueue.ToArray();

        public IObservable<bool> SendEmail(string toAddress, string subject, string htmlBody)
            => SendEmail(toAddress, subject, htmlBody, []);

        public IObservable<bool> SendEmail(
            string toAddress, string subject, string htmlBody, IReadOnlyCollection<EmailAttachment> attachments)
            => SendEmail(toAddress, subject, htmlBody, attachments, EmailDelivery.AsSharedMailbox);

        public IObservable<bool> SendEmail(
            string toAddress, string subject, string htmlBody,
            IReadOnlyCollection<EmailAttachment> attachments, EmailDelivery delivery)
        {
            SentQueue.Enqueue(new SentEmail(toAddress, subject, htmlBody, attachments.ToList(), delivery));
            return Observable.Return(true);
        }
    }

    private sealed record SentEmail(
        string To, string Subject, string Body,
        IReadOnlyList<EmailAttachment> Attachments, EmailDelivery Delivery);
}
