#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
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
/// Pins the Space's <b>Edit</b> area to a markdown editor on the Space's main markdown body
/// (<see cref="Space.Body"/>) — NOT the generic property form over every field. The editor binds to
/// the Space CONTENT object via a node-bound <c>DataContext</c> and writes per-field (one source of
/// truth), so it edits the content object and never the whole-content-replacing
/// <c>MarkdownEditorControl.WithAutoSave</c> path (which would destroy the structured Space). See
/// <see cref="SpaceLayoutAreas.Edit"/> and Doc/GUI/DataBinding.
/// </summary>
public class SpaceEditMarkdownTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddSpaceType();

    [Fact(Timeout = 60000)]
    public async Task SpaceEdit_RendersMarkdownEditorOnBody_NotPropertyForm()
    {
        var spaceId = $"EditSpace{Guid.NewGuid():N}"[..18];
        var spaceNode = MeshNode.FromPath(spaceId) with
        {
            Name = "Edit Space",
            NodeType = SpaceNodeType.NodeType,
            Content = new Space { Name = "Edit Space", Body = "# Overview\n\nhello" }
        };
        await NodeFactory.CreateNode(spaceNode).Should().Emit();

        var workspace = GetClient(c => c.AddData()).GetWorkspace();
        var reference = new LayoutAreaReference(MeshNodeLayoutAreas.EditArea);
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new Address(spaceId), reference);

        // Collect every control in the rendered Edit tree.
        var controls = new List<UiControl>();
        await CollectControls(stream, reference.Area!, controls);

        // 1) Edit = a markdown editor (not "some other stuff").
        var editor = controls.OfType<MarkdownEditorControl>().FirstOrDefault();
        Assert.True(editor is not null,
            "the Space Edit area must render a MarkdownEditorControl on the body");

        // 2) It edits the CONTENT OBJECT: bound to the node stream (node-bound DataContext),
        //    NOT the whole-content-replacing WithAutoSave path.
        editor!.DataContext?.ToString().Should().Contain(LayoutAreaReference.MeshNodePrefix,
            "the body editor must be node-bound so edits write to the Space content per-field");
        editor.AutoSaveAddress.Should().BeNull(
            "WithAutoSave replaces the whole node content with a MarkdownContent — it would destroy the Space");

        // 3) NOT the generic property form (which renders a LayoutGrid of every Space field).
        controls.OfType<LayoutGridControl>().Should().BeEmpty(
            "the Space Edit area must be the markdown body editor, not the generic property form");

        await NodeFactory.DeleteNode(spaceId).Should().Emit();
    }

    /// <summary>
    /// The Doc + Skill partition roots are <c>nodeType: Space</c> but ship their landing page as a
    /// <see cref="MeshWeaver.Markdown.MarkdownContent"/> (their seeding layer can't reference the
    /// Space type). Such a Space keeps its markdown in <c>content</c>, NOT <c>Space.body</c> — so the
    /// body-field editor read blank (the reported "editor is empty" bug). The Edit area must instead
    /// route to the standard markdown editor, which LOADS the content and re-prerenders on save.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task SpaceEdit_MarkdownBackedSpace_LoadsContentIntoStandardEditor()
    {
        const string body = "# Welcome\n\nthis markdown must load into the editor";
        var spaceId = $"MdSpace{Guid.NewGuid():N}"[..16];
        var spaceNode = MeshNode.FromPath(spaceId) with
        {
            Name = "Markdown Space",
            NodeType = SpaceNodeType.NodeType,
            // MarkdownContent — the Doc/Skill shape, NOT a structured Space object.
            Content = new MeshWeaver.Markdown.MarkdownContent { Content = body }
        };
        await NodeFactory.CreateNode(spaceNode).Should().Emit();

        var workspace = GetClient(c => c.AddData()).GetWorkspace();
        var reference = new LayoutAreaReference(MeshNodeLayoutAreas.EditArea);
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new Address(spaceId), reference);

        var controls = new List<UiControl>();
        await CollectControls(stream, reference.Area!, controls);

        var editor = controls.OfType<MarkdownEditorControl>().FirstOrDefault();
        Assert.True(editor is not null,
            "a markdown-backed Space Edit area must render a MarkdownEditorControl");

        // The bug: the editor opened EMPTY. It must load the MarkdownContent.content verbatim.
        editor!.Value.Should().Be(body,
            "the markdown-backed Space editor must load its content (the empty-editor bug)");

        // Standard markdown editor path: WithValue + WithAutoSave (re-prerenders on save). This is the
        // opposite of the structured-Space case, whose editor is node-bound with AutoSaveAddress null.
        editor.AutoSaveAddress.Should().NotBeNull(
            "a markdown-backed Space edits via the standard WithAutoSave markdown editor");

        await NodeFactory.DeleteNode(spaceId).Should().Emit();
    }

    /// <summary>
    /// Resolves the control at <paramref name="areaName"/> and, when it is a container, recurses into
    /// its child areas — accumulating every resolved control so the test can assert on the whole tree.
    /// </summary>
    private static async Task CollectControls(
        ISynchronizationStream<JsonElement> stream,
        string areaName,
        List<UiControl> sink,
        int depth = 0)
    {
        if (depth > 6) return;

        var control = await stream.GetControlStream(areaName)
            .Should().Within(15.Seconds()).Match(c => c != null);
        if (control is null) return;

        sink.Add(control);
        if (control is StackControl stack)
        {
            foreach (var area in stack.Areas)
            {
                var name = area.Area?.ToString();
                if (!string.IsNullOrEmpty(name))
                    await CollectControls(stream, name, sink, depth + 1);
            }
        }
    }
}
