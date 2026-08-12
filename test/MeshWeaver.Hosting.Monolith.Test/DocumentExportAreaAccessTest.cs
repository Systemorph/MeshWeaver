using System;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Markdown;
using MeshWeaver.Markdown.Export.Configuration;
using MeshWeaver.Markdown.Export.Html;
using MeshWeaver.Markdown.Export.Messaging;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// 🚨 <b>Information-disclosure regression for document export.</b>
///
/// <para>Resolving an embedded layout area server-side means the EXPORT now reads content the
/// reader never explicitly asked for. That makes the identity the area is read under a security
/// boundary, not an implementation detail — and it is exactly the boundary that was broken: the
/// resolver captured the caller's <see cref="AccessContext"/> and applied it with
/// <c>Observable.Defer</c> + <c>using var</c>, so the scope closed when the factory RETURNED. The
/// area stream was therefore <i>created</i> under the caller's identity but <i>subscribed and
/// rendered</i> without it — and the owner's read gate evaluates on the subscription. A permission
/// check that appears to run but is not the one that decides.</para>
///
/// <para>The test is an A/B on the SAME document, because "the secret is absent" is a claim that
/// passes trivially when area resolution is broken outright: <c>carol</c> may read the embedded
/// node and MUST see it; <c>bob</c> may not and MUST NOT. Only the pair pins the behaviour.</para>
///
/// <para>🔍 <b>What this test does NOT prove, stated so nobody assumes otherwise.</b> It asserts the
/// user-visible OUTCOME — an export contains only what its requester may read — and that outcome
/// holds because each export runs in its own script/activity hub, where the ambient identity IS the
/// requester. It does <i>not</i> discriminate the <c>Observable.Defer</c> → <c>Observable.Using</c>
/// change: with caller and ambient being the same identity, both shapes pass. An attempt to
/// discriminate them, by resolving with an explicit caller that differs from the ambient identity,
/// produced ORDER-DEPENDENT results on a shared workspace — the same (ambient, caller) pair
/// returned the full card or nothing depending only on which identity had read that area FIRST.
/// That points at the layout-area result being cached per (address, reference) without identity,
/// which is its own question and deserves its own investigation rather than an assertion bolted on
/// here. So the scope change is defence-in-depth (it can only ever hold the caller's identity for
/// LONGER than before), not a mechanism this test proves operative.</para>
/// </summary>
public class DocumentExportAreaAccessTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    // Single-token sentinel: PDF text extraction rebuilds words from glyph positions and does not
    // reliably preserve spacing, so a multi-word phrase can come back joined.
    private const string SecretToken = "CONFIDENTIALMERGERTARGET";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)          // row-level security on; no PublicAdminAccess
            .AddMarkdownExport()
            .AddMeshNodes(
                // bob may read the DOCUMENT but has no grant at all on the Secret space.
                AssignmentNodeFactory.UserRole("bob_viewer_docs", "Viewer", "Docs", accessObject: "bob"),
                // carol may read both — the positive control.
                AssignmentNodeFactory.UserRole("carol_viewer_docs", "Viewer", "Docs", accessObject: "carol"),
                AssignmentNodeFactory.UserRole("carol_viewer_secret", "Viewer", "Secret", accessObject: "carol"));

    [Fact(Timeout = 180000)]
    public async Task EmbeddedArea_IsRenderedForAReaderWhoMayReadIt_AndWithheldFromOneWhoMayNot()
    {
        await SeedDocumentAndSecret();

        // ── carol MAY read the embedded node: it must appear ────────────────────────────────
        Login("carol");
        var allowed = ReadPdfText(await Export("Docs/Report"));
        allowed.Should().Contain(SecretToken,
            "carol has Viewer on the Secret space, so the embedded area must resolve for her — "
            + "without this half the withholding assertion below would also pass if area "
            + "resolution were broken outright");

        // ── bob MAY NOT: it must be withheld, and he must SEE that something was withheld ───
        Login("bob");
        var denied = ReadPdfText(await Export("Docs/Report"));

        denied.Should().NotContain(SecretToken,
            "bob has no grant on the Secret space; resolving the embed under the wrong identity "
            + "would put content he cannot read into a document he can download and forward");

        denied.Should().Contain("Board summary",
            "the rest of the document — which bob may read — still renders");

        // No teardown deletes here on purpose. The mesh is per-test-class and disposed with it, and
        // the last identity in scope is bob — a Viewer, who cannot delete a space. Deleting as
        // system to work around that would only add a privileged step this test exists to distrust.
    }

    /// <summary>
    /// A document both users may read, embedding a node only carol may read. Seeded as system —
    /// the legitimate provisioner — so the grants under test are the only thing that varies.
    /// Idempotent-by-construction: each test class gets its own mesh.
    /// </summary>
    private async Task SeedDocumentAndSecret()
    {
        await CreateAsSystem(MeshNode.FromPath("Docs") with
        {
            Name = "Docs", NodeType = SpaceNodeType.NodeType, Content = new Space()
        });
        await CreateAsSystem(MeshNode.FromPath("Secret") with
        {
            Name = "Secret", NodeType = SpaceNodeType.NodeType, Content = new Space()
        });

        // The embedded target lives in the space bob cannot read.
        await CreateAsSystem(MeshNode.FromPath("Secret/Target") with
        {
            Name = SecretToken,
            Description = "Deal terms.",
            NodeType = MarkdownNodeType.NodeType,
            Content = new MarkdownContent { Content = "# Secret" }
        });

        // The document lives where BOTH may read it, and embeds the restricted node.
        await CreateAsSystem(MeshNode.FromPath("Docs/Report") with
        {
            Name = "Report",
            NodeType = MarkdownNodeType.NodeType,
            Content = new MarkdownContent
            {
                Content = "# Report\n\nBoard summary.\n\n@@(\"Docs/Report/area/OgCard/Secret/Target\")\n"
            }
        });
    }

    private void Login(string userId)
        => Mesh.ServiceProvider.GetRequiredService<AccessService>()
            .SetCircuitContext(new AccessContext { ObjectId = userId, Name = userId });

    private Task CreateAsSystem(MeshNode node)
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        return Observable.Using(access.ImpersonateAsSystem, _ => NodeFactory.CreateNode(node))
            .SubscribeOn(TaskPoolScheduler.Default)
            .Timeout(TimeSpan.FromSeconds(30))
            .FirstAsync()
            .ToTask(TestContext.Current.CancellationToken);
    }

    /// <summary>Runs the real export pipeline (request → .csx template → ActivityLog) as the
    /// currently logged-in user and returns the rendered PDF bytes.</summary>
    private async Task<byte[]> Export(string sourcePath)
    {
        var request = new ExportDocumentRequest(sourcePath, new DocumentExportOptions
        {
            Format = ExportFormat.Pdf,
            CoverPage = false,
            TableOfContents = false,
            BaseUrl = "https://portal.example.com"
        });

        var dispatch = await Mesh
            .Observe<ExportDocumentResponse>(request, o => o.WithTarget(new Address(sourcePath)))
            .Should().Within(30.Seconds()).Emit();
        dispatch.Message.Error.Should().BeNullOrEmpty();

        var workspace = GetClient(c => c.AddData()).GetWorkspace();
        var terminal = await workspace
            .GetMeshNodeStream(dispatch.Message.ActivityPath)
            .Select(node => node?.Content as ActivityLog)
            .Should().Within(2.Minutes())
            .Match(log => log is not null && log.Status != ActivityStatus.Running);

        terminal!.Status.Should().Be(ActivityStatus.Succeeded,
            because: "the export must succeed for both users. Messages:\n  "
                     + string.Join("\n  ", terminal.Messages.Select(m => $"[{m.LogLevel}] {m.Message}")));

        var rendered = terminal.ReturnValue!.Value
            .Deserialize<RenderedDocument>(Mesh.JsonSerializerOptions);
        rendered.Should().NotBeNull();
        return rendered!.Content;
    }

    private static string ReadPdfText(byte[] bytes)
    {
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(bytes);
        return string.Join("\n", pdf.GetPages().Select(p => p.Text));
    }
}
