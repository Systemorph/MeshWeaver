using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Markdown;
using MeshWeaver.Markdown.Export;
using MeshWeaver.Markdown.Export.Configuration;
using MeshWeaver.Markdown.Export.Messaging;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Security.Test;

/// <summary>
/// Regression guard for issue <b>#423</b> (the reopen): an ORDINARY, NON-ADMIN user must be able to
/// run the built-in script templates — <c>Templates/Export/{Pdf,Docx}</c> and
/// <c>Templates/Import/{NodeCopy,Mirror}</c>.
///
/// <para>The defect: those templates are seeded by library code via <c>AddMeshNodes</c> with <b>no
/// accompanying access grant</b>, while <see cref="ExecuteScriptRequest"/> is gated by
/// <c>[RequiresPermission(Permission.Execute)]</c> on the template's own path. So every non-admin's
/// export died with <c>"Access denied: user 'sglauser' lacks Execute permission on
/// 'Templates/Export/Pdf'"</c>. The gate is correct; the missing grant was the bug, and the fix is
/// <see cref="ScriptTemplates.PublicExecuteGrant"/>.</para>
///
/// <para>🚨 <b>Why the existing coverage could not catch this, and what this fixture does
/// differently.</b> <c>DeckExportScriptRelayTest</c> / <c>ExportDocumentScriptRelayTest</c> derive
/// from <see cref="MonolithMeshTestBase"/> and run as the auto-logged-in DevLogin ADMIN, whose root
/// grant carries Execute everywhere — structurally unable to fail. This fixture therefore does two
/// things they don't:</para>
/// <list type="number">
///   <item>calls <see cref="MonolithMeshTestBase.ConfigureMeshBase"/> instead of
///     <c>base.ConfigureMesh</c>, to OPT OUT of <c>TestUsers.PublicAdminAccess()</c> — that default
///     grants <see cref="WellKnownUsers.Public"/> the <b>Admin</b> role at ROOT, which would hand
///     every user Execute on everything and mask the defect completely;</item>
///   <item>drives the export as <see cref="OrdinaryUser"/>, who is granted
///     <see cref="Role.Editor"/> on the space being exported FROM and <b>deliberately nothing at
///     all on <c>Templates</c></b> — exactly a real signed-in user's shape.</item>
/// </list>
/// </summary>
public class ScriptTemplateExecuteAccessTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>A plain signed-in user — no admin anywhere. Named after the issue's reporter.</summary>
    private const string OrdinaryUser = "sglauser";

    private const string SpaceNs = "ExportSpace";
    private const string SourceId = "Report";
    private const string SourcePath = $"{SpaceNs}/{SourceId}";

    private static readonly string ExportPdfTemplate =
        $"{MarkdownExportTemplates.TemplatesNamespace}/{MarkdownExportTemplates.ExportPdfId}";
    private static readonly string ExportDocxTemplate =
        $"{MarkdownExportTemplates.TemplatesNamespace}/{MarkdownExportTemplates.ExportDocxId}";
    private static readonly string ImportNodeCopyTemplate =
        $"{GraphImportTemplates.TemplatesNamespace}/{GraphImportTemplates.NodeCopyId}";
    private static readonly string ImportMirrorTemplate =
        $"{GraphImportTemplates.TemplatesNamespace}/{GraphImportTemplates.MirrorId}";

    // 🚨 ConfigureMeshBase, NOT base.ConfigureMesh — see the class remarks: the default adds
    // TestUsers.PublicAdminAccess() (Public → Admin at ROOT), under which this test can never fail.
    // AddGraph() (from the base) seeds Templates/Import/*; AddMarkdownExport() seeds
    // Templates/Export/*. Both also seed the SAME partition-level grant, so this fixture doubles as
    // the idempotency check on AddMeshNodesIfAbsent — a duplicate would show up as two grant nodes.
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMarkdownExport()
            .AddMeshNodes(
                // The space the user exports FROM, plus the document itself.
                new MeshNode(SpaceNs) { Name = "Export Space", NodeType = MarkdownNodeType.NodeType },
                new MeshNode(SourceId, SpaceNs)
                {
                    Name = "Sample Document",
                    NodeType = MarkdownNodeType.NodeType,
                    Content = MarkdownContent.Parse(
                        "# Sample\n\nA NONADMINTOKEN paragraph that must reach the rendered PDF.",
                        "", SourcePath)
                },
                // The user's own home partition — the shape onboarding writes. A script run lands
                // its Activity here (ActivityParentPath = "{viewer}"); every user is implicitly
                // Admin of the scope equal to their own id, so no explicit home grant is needed.
                new MeshNode(OrdinaryUser) { Name = "S Glauser", NodeType = "User" },
                // The ONLY grant this user gets: Editor on the space being exported.
                // Nothing whatsoever on Templates — that is the point of the test.
                AssignmentNodeFactory.UserRole(OrdinaryUser, Role.Editor.Id, SpaceNs));

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    private void SignInAsOrdinaryUser()
    {
        var ctx = new AccessContext { ObjectId = OrdinaryUser, Name = "S Glauser" };
        Access.SetContext(ctx);
        Access.SetCircuitContext(ctx);
    }

    private void SignOut()
    {
        Access.SetContext(null);
        Access.SetCircuitContext(null);
    }

    private Task<Permission> EffectivePermissions(string path)
        => Mesh.GetEffectivePermissions(path, OrdinaryUser)
            .FirstAsync().Timeout(30.Seconds()).ToTask();

    /// <summary>
    /// The precise pin: a non-admin holds <see cref="Permission.Execute"/> on every built-in script
    /// template — and still holds NO write there. Covers Import as well as Export, because
    /// <c>Templates/Import/NodeCopy</c> shipped with the identical exposure and is dispatched
    /// through the same gate by <c>NodeCopyDispatchHandler</c>.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task OrdinaryUser_CanExecuteBuiltInScriptTemplates_ButCannotWriteThem()
    {
        foreach (var template in new[]
                 {
                     ExportPdfTemplate, ExportDocxTemplate,
                     ImportNodeCopyTemplate, ImportMirrorTemplate
                 })
        {
            var effective = await EffectivePermissions(template);
            Output.WriteLine($"{OrdinaryUser} on {template}: {effective}");

            effective.Should().HaveFlag(Permission.Execute,
                $"an ordinary signed-in user must be able to RUN {template} — " +
                "ExecuteScriptRequest is gated on Execute on this exact path (issue #423)");
            effective.Should().HaveFlag(Permission.Read,
                $"{template} must be resolvable by the caller for the dispatch to find it");

            // Minimum grant: Viewer, never Editor/Admin. A user may run a template, never change it.
            effective.Should().NotHaveFlag(Permission.Create,
                $"the Templates grant must not confer Create on {template}");
            effective.Should().NotHaveFlag(Permission.Update,
                $"the Templates grant must not confer Update on {template}");
            effective.Should().NotHaveFlag(Permission.Delete,
                $"the Templates grant must not confer Delete on {template}");
        }
    }

    /// <summary>
    /// The grant is scoped to <c>Templates</c> and must not leak anywhere else — it is emphatically
    /// NOT a root grant filed under <c>Templates/</c> (AccessControl.md → "The scope invariant").
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task TemplatesGrant_DoesNotLeakOutsideTheTemplatesPartition()
    {
        var atRoot = await EffectivePermissions("");
        atRoot.Should().NotHaveFlag(Permission.Execute,
            "the Templates grant is scoped to Templates — it must confer nothing at ROOT");

        // A partition the user has no business in stays closed.
        var elsewhere = await EffectivePermissions(MonolithMeshTestBase.TestPartition);
        elsewhere.Should().NotHaveFlag(Permission.Read,
            "an ungranted partition must stay unreadable — the Templates grant must not widen it");
    }

    /// <summary>
    /// The user-visible acceptance criterion from #423: a NON-ADMIN clicks "Export to PDF" and gets
    /// a PDF. Before the fix this failed at dispatch with
    /// <c>"Templates/Export/Pdf: Access denied: … lacks Execute permission"</c> on
    /// <see cref="ExportDocumentResponse.Error"/>.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task OrdinaryUser_ExportsDocumentToPdf_EndToEnd()
    {
        SignInAsOrdinaryUser();
        try
        {
            var request = new ExportDocumentRequest(SourcePath, new DocumentExportOptions
            {
                Format = ExportFormat.Pdf,
                Title = "Sample Document",
                CoverPage = false,
                TableOfContents = false
            });

            var dispatch = await Mesh
                .Observe<ExportDocumentResponse>(request, o => o.WithTarget(new Address(SourcePath)))
                .Should().Within(60.Seconds()).Emit();

            // THE regression assertion — this is the exact string the issue reports.
            dispatch.Message.Error.Should().BeNullOrEmpty(
                "a non-admin's export must dispatch, not be refused for lack of Execute on " +
                $"{ExportPdfTemplate} (issue #423)");
            dispatch.Message.ActivityPath.Should().NotBeNullOrEmpty(
                "the response should carry the running activity's path");

            var workspace = GetClient(c => c.AddData()).GetWorkspace();
            var terminal = await workspace
                .GetMeshNodeStream(dispatch.Message.ActivityPath)
                .Select(node => node?.Content as ActivityLog)
                .Should().Within(2.Minutes())
                .Match(log => log is not null && log.Status != ActivityStatus.Running);

            terminal!.Status.Should().Be(ActivityStatus.Succeeded,
                because: "the non-admin's export should render without errors. Messages:\n  "
                         + string.Join("\n  ",
                             terminal.Messages.Select(m => $"[{m.LogLevel}] {m.Message}")));

            terminal.ReturnValue.Should().NotBeNull("the script should record its rendered output");
            var rendered = terminal.ReturnValue!.Value
                .Deserialize<RenderedDocument>(Mesh.JsonSerializerOptions);
            rendered.Should().NotBeNull();
            rendered!.Format.Should().Be(ExportFormat.Pdf);
            rendered.MimeType.Should().Be("application/pdf");
            rendered.Content.Should().NotBeNull().And.NotBeEmpty();
            rendered.Content.Length.Should().BeGreaterThan(100,
                "a real PDF is at least a few hundred bytes");
        }
        finally
        {
            SignOut();
        }
    }
}
