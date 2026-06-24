using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Kernel;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Markdown.Test;

/// <summary>
/// Regression guard for the interactive-markdown <b>kernel-anchor</b> bug: a user viewing a doc they
/// can only READ must still be able to run its interactive code blocks. The per-view kernel Activity
/// is the VIEWING USER'S ephemeral execution context — the code runs AS that user — so it must be
/// created where the user can legitimately write: their OWN home partition, NOT under the read-only
/// doc node.
///
/// <para><b>The bug.</b> The Blazor views anchored the kernel Activity at
/// <c>{docPath}/_Activity/markdown-{id}</c>. A non-owner viewer (Read but not Create on the doc
/// partition) was DENIED that create by RLS, so the activity never existed, the
/// <c>CreateActivityAndSubmit</c> <c>onReady</c> gate never fired, and the view hung forever on
/// "Starting interactive kernel…". (The same denial produced the earlier <c>_Activity/markdown-*</c>
/// NotFound storm.)</para>
///
/// <para><b>The fix.</b> The views re-anchor the Activity at the viewing user's home partition
/// (<c>{userHome}/_Activity/markdown-{id}</c>, <c>userHome == AccessContext.ObjectId</c>). The viewer
/// always owns their own partition: <see cref="MeshWeaver.Graph.Security.RlsNodeValidator"/> grants any
/// write under <c>{userId}/…</c> and the permission evaluator auto-grants Admin at scope == userId — so
/// the create succeeds with NO System impersonation. Below we drive the production
/// <see cref="MarkdownViewLogic.CreateActivityAndSubmit"/> as a non-owner viewer and assert (a) the
/// home-anchored create SUCCEEDS + onReady fires, while (b) the OLD doc-anchored create is DENIED — the
/// exact contrast that makes the re-anchor necessary.</para>
/// </summary>
[Collection("KernelTests")]
public class KernelOwnerAnchorIntegrationTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    // The non-owner viewer: a plain authenticated user with NO admin claim and only Read (Viewer) on
    // the doc partition. Distinct from the auto-logged-in admin (Roland), whose global Admin claim
    // would mask the bug by granting Create everywhere.
    private static readonly AccessContext Viewer = new() { ObjectId = "Vivian", Name = "Vivian" };

    private const string DocPartition = "ReadOnlyDocs";
    private const string DocPath = DocPartition + "/InteractivePage";

    /// <summary>
    /// Real RLS (no PublicAdminAccess — that would let every user create everywhere via Public). The
    /// "doc" lives in a partition the viewer can only READ: a Group partition + a Markdown page, with a
    /// static Viewer grant for the viewer. Viewer = Read|Execute|Api — crucially NOT Create.
    /// </summary>
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(
                new MeshNode(DocPartition) { Name = "Read-only Docs", NodeType = "Group", State = MeshNodeState.Active },
                MeshNode.FromPath(DocPath) with
                {
                    Name = "Interactive Page",
                    NodeType = "Markdown",
                    State = MeshNodeState.Active,
                    MainNode = DocPartition,
                },
                // The viewer can READ the docs partition (Viewer role) but Viewer excludes Create.
                AssignmentNodeFactory.UserRole(Viewer.ObjectId!, Role.Viewer.Id, DocPartition));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    private static SubmitCodeRequest[] DemoSubmissions() =>
    [
        new SubmitCodeRequest("MeshWeaver.Layout.Controls.Markdown(\"anchored hello\")") { Id = "anchor-demo" }
    ];

    /// <summary>
    /// THE FIX. A non-owner viewer drives the production helper with the kernel Activity anchored at
    /// THEIR home partition (what the re-anchored views now pass). The create must SUCCEED and the
    /// <c>onReady</c> gate must fire — proving the viewer's deferred kernel subscribe will hit an
    /// address that EXISTS, run under their own identity, with no System impersonation.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task NonOwnerViewer_KernelActivityAtViewerHome_CreatesAndFiresOnReady()
    {
        var client = GetClient();
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();

        // Simulate the circuit running as the non-owner viewer (what ResolveCircuitUser yields in the
        // Blazor view). The create eager-captures this identity as CreatedBy, so RLS sees the viewer.
        accessService.SetCircuitContext(Viewer);
        try
        {
            var kernelId = Guid.NewGuid().ToString("N");
            var ownerPath = Viewer.ObjectId!;                       // {userHome} — the re-anchored owner
            var activityPath = $"{ownerPath}/_Activity/markdown-{kernelId}";
            var kernelAddress = new Address(activityPath);

            var ready = new AsyncSubject<Unit>();
            MarkdownViewLogic.CreateActivityAndSubmit(
                client, meshService, kernelAddress, ownerPath, kernelId, DemoSubmissions(),
                onReady: () => { ready.OnNext(Unit.Default); ready.OnCompleted(); });

            // onReady fires ONLY after the activity node is created AND routable. Pre-fix (doc anchor)
            // the create was denied and this never fired; post-fix (home anchor) it fires.
            await ready.Should().Within(20.Seconds()).Emit();

            // And the activity genuinely exists at the user-anchored path.
            var node = await client.GetWorkspace().GetMeshNodeStream(activityPath)
                .Where(n => n is not null)
                .Take(1)
                .Should().Within(10.Seconds())
                .Match(n => n is not null);

            node!.Path.Should().Be(activityPath);
            node.NodeType.Should().Be("Activity");
        }
        finally
        {
            accessService.SetCircuitContext(TestUsers.Admin);
        }
    }

    /// <summary>
    /// THE BUG (repro of the OLD anchor). The very same non-owner viewer attempting to create the
    /// kernel Activity under the READ-ONLY doc node — the path the views used to anchor at — is DENIED
    /// by RLS with <see cref="UnauthorizedAccessException"/>. This is exactly why the kernel hung; the
    /// fix above avoids it by never anchoring under the doc.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task NonOwnerViewer_KernelActivityUnderReadOnlyDoc_IsDenied()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();

        accessService.SetCircuitContext(Viewer);
        try
        {
            var kernelId = Guid.NewGuid().ToString("N");
            // The OLD shape: anchor the Activity under the doc node the viewer can only read.
            var activityNamespace = $"{DocPath}/_Activity";
            var docAnchoredActivity = new MeshNode($"markdown-{kernelId}", activityNamespace)
            {
                Name = "Markdown view (doc-anchored)",
                NodeType = "Activity",
                MainNode = DocPath,
                State = MeshNodeState.Active,
                Content = new ActivityLog("MarkdownExecution")
                {
                    Id = $"markdown-{kernelId}",
                    HubPath = DocPath,
                    Status = ActivityStatus.Running
                }
            };

            // The viewer has Read (Viewer role) on the doc partition but NOT Create — RLS denies.
            Func<Task> create = async () =>
                await meshService.CreateNode(docAnchoredActivity).FirstAsync().ToTask();

            await create.Should().ThrowAsync<UnauthorizedAccessException>(
                "a non-owner viewer (Read but not Create on the doc partition) cannot anchor the "
                + "interactive-kernel Activity under the read-only doc — the bug that left the kernel "
                + "stuck on 'Starting interactive kernel…'");
        }
        finally
        {
            accessService.SetCircuitContext(TestUsers.Admin);
        }
    }
}
