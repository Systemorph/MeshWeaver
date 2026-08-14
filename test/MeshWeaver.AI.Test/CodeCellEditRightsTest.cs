#pragma warning disable CS1591

using System;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Pins the rights-gating of the code cell's Edit button:
/// <list type="number">
///   <item>A viewer WITH Update permission gets the DIRECT edit navigation
///   (ButtonControl with <c>NavigateToHref</c> to the Edit area).</item>
///   <item>A viewer WITHOUT Update permission (read-only Viewer role) still
///   sees an "Edit" button — but the dialog-triggering variant (no
///   <c>NavigateToHref</c>; its click opens the copy-to-home DialogControl).</item>
/// </list>
/// Uses <see cref="MonolithMeshTestBase.ConfigureMeshBase"/> (NO blanket
/// public-admin grant) so permissions are genuinely granular: the read-only
/// user "Bob" holds only a static Viewer assignment on the partition.
/// </summary>
public class CodeCellEditRightsTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Partition = "rbuergi";
    private const string ViewerUser = "Bob";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            // Static (synchronously collected) grant: Bob is a READ-ONLY Viewer
            // on the partition — enough to subscribe + render the cell, not
            // enough to edit it. No public-admin blanket, no grant for anyone else.
            .AddMeshNodes(new MeshNode($"{ViewerUser}_Access", $"{Partition}/_Access")
            {
                NodeType = "AccessAssignment",
                Name = $"{ViewerUser} viewer access",
                Content = new AccessAssignment
                {
                    AccessObject = ViewerUser,
                    Roles = [new RoleAssignment { Role = "Viewer" }]
                }
            });

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    private async Task<string> SeedExecutableCode()
    {
        var id = $"gated-{Guid.NewGuid():N}";
        var path = $"{Partition}/{id}";
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        await mesh.CreateNode(new MeshNode(id, Partition)
        {
            Name = "Rights-gated cell",
            NodeType = "Code",
            Content = new CodeConfiguration { Code = "\"hi\"", IsExecutable = true }
        }).Should().Within(30.Seconds()).Emit();
        return path;
    }

    private static string FindArea(IContainerControl container, string id)
    {
        var match = container.Areas
            .Select(a => a.Area?.ToString())
            .FirstOrDefault(a => a is not null && (a == id || a.EndsWith("/" + id, StringComparison.Ordinal)));
        match.Should().NotBeNull(
            $"container should contain an area '{id}' — found: " +
            $"[{string.Join(", ", container.Areas.Select(a => a.Area))}]");
        return match!;
    }

    private async Task<(ISynchronizationStream<JsonElement> Stream, StackControl Cell, StackControl Toolbar)>
        RenderCell(string codePath)
    {
        var workspace = GetClient().GetWorkspace();
        var reference = new LayoutAreaReference(CodeLayoutAreas.ContentArea);
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new Address(codePath), reference);

        var root = (StackControl)(await stream.GetControlStream(reference.Area!)
            .Should().Within(30.Seconds()).Match(c => c is StackControl s
                && s.Areas.Any(a => a.Area?.ToString() is { } p
                    && (p == CodeLayoutAreas.CellArea
                        || p.EndsWith("/" + CodeLayoutAreas.CellArea, StringComparison.Ordinal)))))!;
        var cell = (StackControl)(await stream
            .GetControlStream(FindArea(root, CodeLayoutAreas.CellArea))
            .Should().Within(10.Seconds()).Match(c => c is StackControl))!;
        var toolbar = (StackControl)(await stream
            .GetControlStream(FindArea(cell, CodeLayoutAreas.CellToolbarArea))
            .Should().Within(10.Seconds()).Match(c => c is StackControl))!;
        return (stream, cell, toolbar);
    }

    private async Task<ButtonControl> RenderEditButton(string codePath)
    {
        var (stream, _, toolbar) = await RenderCell(codePath);
        var edit = await stream
            .GetControlStream(FindArea(toolbar, CodeLayoutAreas.EditButtonArea))
            .Should().Within(10.Seconds()).Match(c => c is ButtonControl);
        return (ButtonControl)edit!;
    }

    private static bool HasArea(IContainerControl container, string id) =>
        container.Areas.Any(a => a.Area?.ToString() is { } s
            && (s == id || s.EndsWith("/" + id, StringComparison.Ordinal)));

    [Fact(Timeout = 60000)]
    public async Task Editor_Gets_Inline_Editor_And_No_Edit_Button()
    {
        // Default circuit: the DevLogin admin (claim role Admin ⇒ Update). Edit mode IS the
        // mode for an editor: the cell's code segment is the inline Monaco editor (auto-saving
        // into this node), and there is NO Edit button — no second mode to navigate to.
        var codePath = await SeedExecutableCode();
        var (stream, cell, toolbar) = await RenderCell(codePath);

        HasArea(toolbar, CodeLayoutAreas.EditButtonArea).Should().BeFalse(
            "an editor's cell already IS the editor — a dedicated Edit button is retired");

        var code = await stream
            .GetControlStream(FindArea(cell, CodeLayoutAreas.CellCodeArea))
            .Should().Within(10.Seconds()).Match(c => c is CodeEditorControl);
        code.Should().BeOfType<CodeEditorControl>()
            .Which.AutoSaveAddress.Should().Be(codePath,
                "the inline editor persists its debounced text back into THIS node");
    }

    [Fact(Timeout = 60000)]
    public async Task ReadOnly_Viewer_Gets_DialogTriggering_Edit()
    {
        var codePath = await SeedExecutableCode();

        // Switch the circuit to the read-only Viewer (no Admin claim, no
        // Update grant — only the static Viewer role on the partition).
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        accessService.SetHostIdentity(new AccessContext { ObjectId = ViewerUser, Name = ViewerUser });
        try
        {
            var edit = await RenderEditButton(codePath);
            edit.NavigateToHref.Should().BeNull(
                "a read-only viewer's Edit must NOT navigate — it opens the " +
                "copy-to-home dialog explaining the content is read-only");
            edit.Data.Should().Be("Edit",
                "the button is still labeled Edit so the affordance stays discoverable");
        }
        finally
        {
            accessService.SetHostIdentity(null);
        }
    }
}
