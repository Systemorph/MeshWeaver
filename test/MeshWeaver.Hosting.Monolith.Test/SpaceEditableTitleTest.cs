#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// The Space Overview is the default page for every top-level partition root (a Space is the
/// partition-owning node type). Its name renders as a <b>click-to-edit</b> title: for a user who can
/// <see cref="MeshWeaver.Mesh.Security.Permission.Update"/> the node the heading is wrapped in a
/// clickable container that toggles an inline name editor; everyone else sees a plain heading. This
/// pins the editor affordance that replaced the old static <c>&lt;h1&gt;</c>.
/// </summary>
public class SpaceEditableTitleTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddSpaceType();

    [Fact(Timeout = 60000)]
    public async Task SpaceOverview_RendersClickToEditTitle_ForEditor()
    {
        // The test base logs rbuergi in as Admin, so canEdit == true on the freshly-created space.
        var spaceId = $"TitleSpace{Guid.NewGuid():N}"[..18];
        const string spaceName = "Title Space";
        var spaceNode = MeshNode.FromPath(spaceId) with
        {
            Name = spaceName,
            NodeType = SpaceNodeType.NodeType,
            Content = new Space { Name = spaceName }
        };
        await NodeFactory.CreateNode(spaceNode).Should().Emit();

        var workspace = GetClient(c => c.AddData()).GetWorkspace();
        var reference = new LayoutAreaReference("Overview");
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new Address(spaceId), reference);

        var root = await stream.GetControlStream(reference.Area!)
            .Should().Within(30.Seconds()).Match(c => c is StackControl);
        var shell = (StackControl)root!;
        shell.Areas.Should().NotBeEmpty("Overview composes at least a header");

        // Walk ONLY the header subtree (the first Overview child) so we never touch the body's
        // @@("area/Search") catalog embed. Collect every resolvable descendant control.
        var headerArea = shell.Areas[0].Area?.ToString();
        headerArea.Should().NotBeNull();

        var controls = new List<UiControl>();
        async Task Collect(string area, int depth)
        {
            if (depth > 6)
                return;
            var control = await stream.GetControlStream(area)
                .Should().Within(15.Seconds()).Match(c => c != null);
            controls.Add(control!);
            if (control is StackControl sc)
                foreach (var name in sc.Areas.Select(a => a.Area?.ToString()).Where(n => !string.IsNullOrEmpty(n)))
                    await Collect(name!, depth + 1);
        }
        await Collect(headerArea!, 0);

        controls.Should().Contain(
            c => c is HtmlControl h && (h.Data?.ToString() ?? "").Contains(spaceName)
                 && (h.Data?.ToString() ?? "").Contains("<h1"),
            "the space name renders as an <h1> title");

        controls.Should().Contain(
            c => c is StackControl s && s.IsClickable,
            "an editor's title is wrapped in a clickable click-to-edit container "
            + "(the affordance that replaced the old static <h1>)");

        await NodeFactory.DeleteNode(spaceId).Should().Emit();
    }
}
