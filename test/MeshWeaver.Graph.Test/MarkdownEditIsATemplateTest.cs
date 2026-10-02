using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The reference conversion for "templates first, data later" (Doc/GUI/DataBinding): the Markdown
/// node's Edit / Suggest page.
///
/// <para><b>What it was.</b> <c>MarkdownEditLayoutArea.BuildArea</c> emitted a Stack whose only
/// child was a DEFERRED view — <c>GetMeshNodeStream().Take(1).Select(node =&gt; …)</c> — so the
/// page showed nothing but the loading placeholder until the node's owning hub answered, and then
/// baked the markdown into the editor (<c>WithValue(initialContent)</c>): a snapshot that an edit
/// made anywhere else while the page was open never reached.</para>
///
/// <para><b>What the two arms pin.</b> (1) The area is a TEMPLATE: every view in the tree is
/// static — no container carries a deferred renderer — so the first emission IS the page, with
/// nothing to wait for. (2) The editor's pointer, resolved through the very seam the GUI uses
/// (<see cref="MeshNodeBindingExtensions.Bind"/> over <c>IMeshNodeStreamCache</c>), reads the
/// node's markdown and then FOLLOWS a later edit — the live binding the snapshot could not give.
/// </para>
///
/// <para>Negative control, run before this file was committed: against the pre-conversion area the
/// first arm fails on <see cref="EveryViewIsStatic"/> (the root Stack holds one deferred view —
/// a delegate, not a control) and the editor is nowhere in the template — it only existed
/// after the node arrived.</para>
/// </summary>
public class MarkdownEditIsATemplateTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheEditPageIsATemplate_ItsFirstEmissionWaitsOnNoData(bool trackChanges)
    {
        const string path = "acme/Notes/Readme";

        var page = MarkdownEditLayoutArea.BuildTemplate(path, trackChanges, locale: "en");

        EveryViewIsStatic(page);

        var editor = Descendants(page).OfType<MarkdownEditorControl>().Should().ContainSingle(
            "the editor is part of the template — it does not appear only once the node has loaded").Subject;
        editor.Value.Should().BeOfType<JsonPointerReference>(
            "the body is a POINTER into the node, never the markdown text baked in on the hub")
            .Which.Pointer.Should().Be(MarkdownEditLayoutArea.MarkdownBodyPointer);
        var parsed = LayoutAreaReference.TryParseMeshNodeDataContext(editor.DataContext);
        parsed.Should().NotBeNull("the pointer resolves against the node, on the GUI side");
        var ctx = parsed.GetValueOrDefault();
        ctx.NodePath.Should().Be(path);
        ctx.BindContent.Should().BeTrue("the body lives in the node's MarkdownContent");
        editor.TrackChangesEnabled.Should().Be(trackChanges);
        editor.AutoSaveAddress.Should().Be(path);

        var title = Descendants(page).OfType<TextFieldControl>().Should().ContainSingle().Subject;
        title.Data.Should().BeOfType<JsonPointerReference>().Which.Pointer.Should().Be(nameof(MeshNode.Name));
        title.DataContext.Should().Be(LayoutAreaReference.GetMeshNodeDataContext(path, bindContent: false));
    }

    [Fact]
    public async Task TheEditorsPointer_ReadsTheNodesMarkdown_AndFollowsALaterEdit()
    {
        var id = "md-template-" + Guid.NewGuid().ToString("N")[..8];
        var path = $"{TestPartition}/{id}";
        await NodeFactory.CreateNode(new MeshNode(id, TestPartition)
        {
            Name = id,
            NodeType = "Markdown",
            State = MeshNodeState.Active,
            Content = new MarkdownContent { Content = "# First draft" },
        }).Should().Within(TestTimeouts.Convergence).Emit();

        // The pointer and context exactly as the template hands them to the GUI.
        var editor = Descendants(MarkdownEditLayoutArea.BuildTemplate(path, trackChanges: false, locale: "en"))
            .OfType<MarkdownEditorControl>().Single();
        var parsed = LayoutAreaReference.TryParseMeshNodeDataContext(editor.DataContext);
        parsed.Should().NotBeNull("the template's DataContext must parse as a node-bound context");
        var ctx = parsed.GetValueOrDefault();
        var pointer = editor.Value.Should().BeOfType<JsonPointerReference>(
            "the body is a pointer into the node").Subject;
        var bound = MeshNodeBindingExtensions.Bind(
                Mesh, ctx.NodePath, ctx.BindContent, ctx.SubPath, pointer)
            .Replay();
        using var connection = bound.Connect();

        var first = await bound.Should().Within(TestTimeouts.Convergence).Match(
            v => Text(v) == "# First draft",
            "the GUI resolves the template's pointer against the node and finds its markdown");
        Text(first).Should().Be("# First draft");

        await Mesh.GetMeshNodeStream(path)
            .Update(n => n with { Content = new MarkdownContent { Content = "# Edited elsewhere" } })
            .Should().Within(TestTimeouts.Convergence).Emit();

        var later = await bound.Should().Within(TestTimeouts.Convergence).Match(
            v => Text(v) == "# Edited elsewhere",
            "the binding stays subscribed, so an edit made while the page is open reaches it — the "
            + "baked-in WithValue(initialContent) snapshot never could");
        Text(later).Should().Be("# Edited elsewhere");
    }

    private static string? Text(object? emission) =>
        emission is JsonElement { ValueKind: JsonValueKind.String } je ? je.GetString() : null;

    // ── template assertions ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A template's first emission waits on nothing: every view in every container is a CONTROL.
    /// A deferred view — <c>WithView((h, c) =&gt; observable)</c>, the shape that renders only once
    /// data has arrived — is recorded in the container as the delegate or observable that will
    /// produce it, so any non-control entry is a slot the first render leaves empty.
    /// </summary>
    internal static void EveryViewIsStatic(UiControl root)
    {
        foreach (var control in Descendants(root))
        {
            if (Member(control.GetType(), "Views")?.GetValue(control) is not IEnumerable views)
                continue;
            var deferred = views.OfType<object>().Where(v => v is not UiControl)
                .Select(v => v.GetType().Name).ToList();
            deferred.Should().BeEmpty(
                $"{control.GetType().Name} carries {deferred.Count} deferred view(s) — a view that renders "
                + "only once data has arrived. A template binds its data instead.");
        }
    }

    internal static IEnumerable<UiControl> Descendants(UiControl root)
    {
        yield return root;
        if (Member(root.GetType(), "Views")?.GetValue(root) is IEnumerable views)
            foreach (var v in views)
                if (v is UiControl child)
                    foreach (var d in Descendants(child))
                        yield return d;
    }

    private static PropertyInfo? Member(Type? t, string name)
    {
        for (; t is not null; t = t.BaseType)
        {
            var p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (p is not null)
                return p;
        }
        return null;
    }
}
