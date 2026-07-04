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

    private async Task<ButtonControl> RenderEditButton(string codePath)
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
        var edit = await stream
            .GetControlStream(FindArea(toolbar, CodeLayoutAreas.EditButtonArea))
            .Should().Within(10.Seconds()).Match(c => c is ButtonControl);
        return (ButtonControl)edit!;
    }

    [Fact(Timeout = 60000)]
    public async Task Editor_Gets_Direct_Edit_Navigation()
    {
        // Default circuit: the DevLogin admin (claim role Admin ⇒ Update).
        var codePath = await SeedExecutableCode();
        var edit = await RenderEditButton(codePath);
        edit.NavigateToHref.Should().NotBeNull(
            "a viewer with Update permission navigates straight to the Edit area");
        edit.NavigateToHref!.ToString().Should().Contain(CodeLayoutAreas.EditArea);
    }

    [Fact(Timeout = 60000)]
    public async Task ReadOnly_Viewer_Gets_DialogTriggering_Edit()
    {
        var codePath = await SeedExecutableCode();

        // Switch the circuit to the read-only Viewer (no Admin claim, no
        // Update grant — only the static Viewer role on the partition).
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        accessService.SetCircuitContext(new AccessContext { ObjectId = ViewerUser, Name = ViewerUser });
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
            accessService.SetCircuitContext(null);
        }
    }
}
