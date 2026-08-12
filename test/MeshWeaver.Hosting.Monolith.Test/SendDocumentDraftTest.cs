using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Markdown;
using MeshWeaver.Markdown.Export;
using MeshWeaver.Markdown.Export.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// The acceptance test for the lost compose form.
///
/// <para><b>The bug.</b> Someone filled in recipient, subject and message in "Share ⇒ as email",
/// was sent through Microsoft 365 consent, and came back to an empty form. The connect button must
/// navigate with <c>forceLoad: true</c> — consent is a server-side MVC endpoint that an in-circuit
/// Blazor navigation cannot reach — and a full navigation TEARS DOWN THE CIRCUIT. The compose state
/// lived in the layout area's <c>/data</c> store, i.e. circuit memory, keyed by a
/// <c>Guid.NewGuid()</c> minted per render. So the return trip re-rendered, minted a fresh key, and
/// re-seeded a blank form. Nothing was ever persisted, so there was nothing to come back to.</para>
///
/// <para><b>What these tests prove.</b> That the state now lives on a mesh node, so a circuit
/// teardown is irrelevant — and that the draft is private to its author, which is why it is filed
/// under the author rather than under the shared document.</para>
/// </summary>
public class SendDocumentDraftTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddMarkdownExport();

    /// <summary>
    /// 🚨 THE round-trip test: fill the draft, blow the circuit away, come back — the three fields
    /// the user typed are still there.
    ///
    /// <para>"Blowing the circuit away" is modelled the only way it is observable server-side: the
    /// draft path is a pure function of (author, document) rather than a per-render Guid, and the
    /// state is read back through a FRESH stream handle rather than the one that wrote it. That is
    /// exactly what the returning request does — no circuit, no layout-area data store, no memory of
    /// the previous render survives the consent navigation.</para>
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task Draft_SurvivesTheConsentRoundTrip()
    {
        var doc = await SeedDocument();
        var me = TestUsers.Admin.ObjectId!;

        var defaults = new EmailDraft { Subject = "Shared with you: report", Message = "Hi,", Delivery = "body" };
        var draftPath = await EmailDraftNodeType
            .EnsureExists(Mesh, me, doc, defaults)
            .Should().Within(20.Seconds()).Emit();

        draftPath.Should().Be($"{me}/_Draft/{doc.Replace("/", "_")}",
            "the draft is keyed by (author, document) — a per-render id is precisely what could not " +
            "survive the navigation");

        // ── The user fills the form in. Every field write goes through the node stream, which is
        //    what the node-bound form controls do per keystroke (debounced).
        await Mesh.GetWorkspace().GetMeshNodeStream(draftPath)
            .Update(node => node with
            {
                Content = new EmailDraft
                {
                    Email = "someone@example.com",
                    Subject = "Q3 numbers, as promised",
                    Message = "Hi Anna,\n\nHere is the report we discussed.",
                    Delivery = "attachment",
                    DocumentPath = doc
                }
            })
            .Should().Within(20.Seconds()).Emit();

        // ── forceLoad → the circuit dies. Nothing in memory carries over: the returning render
        //    resolves the draft path from scratch and opens a NEW stream handle on it.
        var restoredPath = await EmailDraftNodeType
            .EnsureExists(Mesh, me, doc, defaults)
            .Should().Within(20.Seconds()).Emit();
        restoredPath.Should().Be(draftPath, "the returning render must find the same draft");

        var restored = await Mesh.GetWorkspace().GetMeshNodeStream(restoredPath)
            .Where(n => n is not null)
            .Select(n => n!.ContentAs<EmailDraft>(Mesh.JsonSerializerOptions))
            .Should().Within(20.Seconds()).Match(d => d is not null && d.Email == "someone@example.com");

        restored!.Email.Should().Be("someone@example.com", "the recipient must survive consent");
        restored.Subject.Should().Be("Q3 numbers, as promised", "the subject must survive consent");
        restored.Message.Should().Be("Hi Anna,\n\nHere is the report we discussed.",
            "the message must survive consent — this is the field that cost the user the most work");
        restored.Delivery.Should().Be("attachment", "the delivery choice must survive consent too");
    }

    /// <summary>
    /// The draft is dropped once the mail is out, so sharing the same document again starts clean
    /// instead of resurrecting an old recipient. Discarding is idempotent — a second discard (or one
    /// racing the send's own) is not an error.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task Draft_IsDiscardedAfterSending_AndDiscardIsIdempotent()
    {
        var doc = await SeedDocument();
        var me = TestUsers.Admin.ObjectId!;

        var draftPath = await EmailDraftNodeType
            .EnsureExists(Mesh, me, doc, new EmailDraft { Subject = "s" })
            .Should().Within(20.Seconds()).Emit();

        await EmailDraftNodeType.Discard(Mesh, draftPath).Should().Within(20.Seconds()).Emit();
        await EmailDraftNodeType.Discard(Mesh, draftPath).Should().Within(20.Seconds()).Emit();

        var remaining = await Mesh.GetWorkspace()
            .GetQuery($"drafts|{draftPath}", $"path:{draftPath} nodeType:{EmailDraftNodeType.NodeType}")
            .Should().Within(20.Seconds()).Emit();
        remaining.Should().BeEmpty("a sent draft must not resurface the next time the document is shared");
    }

    private async Task<string> SeedDocument()
    {
        var space = $"Space{Guid.NewGuid():N}"[..16];
        await NodeFactory.CreateNode(MeshNode.FromPath(space) with
        {
            Name = "Draft Test Space",
            NodeType = SpaceNodeType.NodeType,
            Content = new Space()
        }).Should().Emit();

        var doc = $"{space}/report";
        await NodeFactory.CreateNode(MeshNode.FromPath(doc) with
        {
            Name = "Report",
            NodeType = "Markdown",
            Content = new MarkdownContent { Content = "# Report\n\nBody." }
        }).Should().Emit();
        return doc;
    }
}
