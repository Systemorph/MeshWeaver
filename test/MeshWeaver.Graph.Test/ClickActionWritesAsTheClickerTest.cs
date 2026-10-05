using System;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>A write a user triggers from a layout area runs as THAT user</b>
/// (Systemorph/MeshWeaver.Reinsurance#249). The claims review page wrapped its click-triggered
/// writes in <c>ImpersonateAsSystem</c> on the belief that the clicker's identity is gone by the
/// time the write runs. It is not: the click runs under the clicker's delivery, and a write the
/// action subscribes carries that identity through <c>.Subscribe()</c>. So the write needs no
/// impersonation at all — and with one it bypasses the user's grants and stamps the audit trail
/// with <c>system-security</c>.
///
/// <para>The shape is the page's own: a button whose click action subscribes
/// <c>hub.GetMeshNodeStream(target).Update(…)</c> and reports the outcome. The mesh grants NO
/// public admin; the target is WRITTEN through the mesh (a config-seeded node is served without
/// row-level security). An editor and a viewer each open the page and click: the viewer's write
/// is refused and the target is untouched, the editor's lands stamped with the editor.</para>
///
/// <para>Negative control, run before this file was committed: the same click action wrapped in
/// <c>ImpersonateAsSystem</c> (the page's old shape) lets the viewer's write through, stamped
/// <c>system-security</c>.</para>
/// </summary>
public class ClickActionWritesAsTheClickerTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string PageType = "ClickWriteProbeType";
    private const string Page = "ClickWriteProbe";
    private const string Area = "Probe";
    private const string WriteArea = Area + "/write";
    private const string WrittenName = "written by a click";

    private static readonly AccessContext Editor = new() { ObjectId = "click-editor", Name = "Click Editor" };
    private static readonly AccessContext Viewer = new() { ObjectId = "click-viewer", Name = "Click Viewer" };

    private readonly string target = $"{TestPartition}/clicktarget{Guid.NewGuid():N}";
    private readonly ReplaySubject<string> outcomes = new();

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    // ConfigureMeshBase, deliberately NOT ConfigureMesh: the default adds a Public Admin grant,
    // under which every clicker may write everything.
    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder).AddMeshNodes(
            new MeshNode(PageType)
            {
                HubConfiguration = config => config.AddLayout(layout => layout.WithView(Area, (host, _) =>
                    Controls.Stack.WithView(
                        Controls.Button("Write").WithClickAction(ctx =>
                            // The claims review page's write: subscribed in the click action, no
                            // impersonation — the clicker's identity rides the subscription.
                            ctx.Hub.GetMeshNodeStream(target)
                                .Update(node => node with { Name = WrittenName })
                                .Take(1)
                                .Subscribe(
                                    _ => outcomes.OnNext("written"),
                                    ex => outcomes.OnNext($"refused: {ex.GetType().Name}"))),
                        "write")))
            },
            new MeshNode(Page) { NodeType = PageType },
            PageGrant(Editor),
            PageGrant(Viewer));

    // Both clickers may OPEN the page; what they may do to the target is decided per target.
    private static MeshNode PageGrant(AccessContext who) =>
        Grant(who, Page, "Viewer");

    private static MeshNode Grant(AccessContext who, string scope, string role) =>
        new($"{who.ObjectId}_Access", $"{scope}/_Access")
        {
            NodeType = "AccessAssignment",
            Name = $"{who.Name} — {role}",
            MainNode = scope,
            Content = new AccessAssignment
            {
                AccessObject = who.ObjectId!,
                DisplayName = who.Name,
                Roles = [new RoleAssignment { Role = role }],
            },
        };

    [Fact(Timeout = 120000)]
    public async Task AViewerCannotWriteThroughTheClick_AndAnEditorWritesAsThemselves()
    {
        var ct = TestContext.Current.CancellationToken;
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        // Written as the administrator (the harness's DevLogin identity), through the mesh.
        await mesh.CreateNode(MeshNode.FromPath(target) with { NodeType = "Markdown", Name = "untouched" })
            .Timeout(TestTimeouts.Convergence).Await(ct);
        await mesh.CreateNode(Grant(Editor, target, "Editor")).Timeout(TestTimeouts.Convergence).Await(ct);
        await mesh.CreateNode(Grant(Viewer, target, "Viewer")).Timeout(TestTimeouts.Convergence).Await(ct);

        // The viewer opens the page and clicks: the write is refused, the target untouched.
        ActAs(Viewer);
        var viewerStream = OpenPage(ct);
        await Click(await viewerStream, Viewer, ct);
        (await NextOutcome(0, ct)).Should().StartWith("refused",
            "the viewer holds no Update on the target, and the click's write runs as the viewer");
        (await ReadTargetAsAdmin(ct)).Name.Should().Be("untouched");

        // The control: the editor's same click writes, stamped with the editor.
        ActAs(Editor);
        var editorStream = OpenPage(ct);
        await Click(await editorStream, Editor, ct);
        (await NextOutcome(1, ct)).Should().Be("written", "the editor holds Update on the target");
        var written = await ReadTargetAsAdmin(ct, n => n.Name == WrittenName);
        written.LastModifiedBy.Should().Be(Editor.ObjectId,
            "the write is the clicking user's, and the audit stamp says so — never system-security");
    }

    // ── Harness ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Makes <paramref name="identity"/> the one the NEXT subscription is opened as.</summary>
    private void ActAs(AccessContext identity)
    {
        TestUsers.DevLogin(Mesh, identity);
        Mesh.ServiceProvider.GetRequiredService<AccessService>().SetContext(identity);
    }

    private async Task<ISynchronizationStream<JsonElement>> OpenPage(CancellationToken ct)
    {
        var stream = GetClient().GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            new Address(Page), new LayoutAreaReference(Area));
        await stream.GetControlStream(WriteArea).Should().Within(TestTimeouts.Convergence)
            .Match(c => c is ButtonControl, "the page renders its Write button", cancellationToken: ct);
        return stream;
    }

    private static async Task Click(ISynchronizationStream<JsonElement> stream, AccessContext actingUser, CancellationToken ct)
    {
        var answer = new ReplaySubject<string?>(1);
        stream.SubmitUserAction(new ClickedEvent(WriteArea, stream.StreamId), actingUser,
            onRefused: answer.OnNext, onAccepted: () => answer.OnNext(null));
        (await answer.Should().Within(TestTimeouts.Convergence).Emit("the owner answers every click", ct))
            .Should().BeNull("the page's own subscriber clicked it");
    }

    private Task<string> NextOutcome(int index, CancellationToken ct) =>
        outcomes.Skip(index).FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

    private Task<MeshNode> ReadTargetAsAdmin(CancellationToken ct, Func<MeshNode, bool>? settled = null)
    {
        ActAs(TestUsers.Admin);
        return Mesh.GetMeshNodeStream(target).Where(n => n is not null && (settled?.Invoke(n) ?? true))
            .Select(n => n!).FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
    }
}
