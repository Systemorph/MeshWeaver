using System;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The two Monaco controls that used to be bindable only to a hub-baked string —
/// <see cref="CodeEditorControl"/> and <see cref="DiffEditorControl"/> — bind to a NODE FIELD
/// (Doc/GUI/DataBinding → "Binding a rich control to a node field").
///
/// <para>Each control is built with its <c>BindToNode</c> builder exactly as a layout area would, and
/// its pointers are then resolved through the SAME seam every renderer reads through
/// (<see cref="LayoutAreaReference.TryParseMeshNodeDataContext"/> → <see cref="MeshNodeBindingExtensions.Bind"/>;
/// the Blazor views and the React client both branch on that context). So what is asserted is the
/// contract a renderer relies on: the pointer + DataContext the control carries resolve to the field,
/// FOLLOW a change made by somebody else, and — for the editor — a write lands on that one field only.
/// The Blazor/React halves pin their own rendering in MeshWeaver.Plugins.</para>
///
/// <para>Negative controls: the hub-baked literal shape (<c>WithValue</c> / <c>OriginalContent</c>)
/// carries no pointer and no node context, so there is nothing for a renderer to follow — the very
/// gap this closes; and a pointer resolved against the WRONG root (<c>bindContent: false</c>) draws
/// empty and stays empty through the change, so a pass below cannot be the binding answering anything
/// for any field.</para>
/// </summary>
public class NodeBoundEditorControlsTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static string NewId(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8];

    private async Task<string> SeedNode(string json)
    {
        var id = NewId("bound-");
        await NodeFactory.CreateNode(new MeshNode(id, TestPartition)
        {
            Name = id,
            NodeType = "Markdown",
            State = MeshNodeState.Active,
            Content = JsonSerializer.Deserialize<JsonElement>(json),
        }).Should().Within(TestTimeouts.Convergence).Emit();
        return $"{TestPartition}/{id}";
    }

    /// <summary>Resolves <paramref name="value"/> the way a renderer does: a relative pointer under a
    /// node-bound DataContext reads live off the node stream.</summary>
    private IObservable<string?> Resolve(object? value, string? dataContext)
    {
        var pointer = value.Should().BeOfType<JsonPointerReference>(
            "a bound control carries a POINTER, never the text itself").Subject;
        var ctx = LayoutAreaReference.TryParseMeshNodeDataContext(dataContext);
        ctx.Should().NotBeNull("the control's DataContext is the node-bound context the renderer branches on");
        var (nodePath, bindContent, subPath) = ctx.GetValueOrDefault();
        MeshNodeBindingExtensions.IsNodeBound(dataContext, pointer).Should().BeTrue();
        return MeshNodeBindingExtensions
            .Bind(Mesh, nodePath, bindContent, subPath, pointer)
            .Select(Text);
    }

    private static string? Text(object? emission) => emission switch
    {
        JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
        string s => s,
        _ => null,
    };

    private IObservable<MeshNode> Updated(string path, Func<JsonElement, string> content) =>
        Mesh.GetMeshNodeStream(path).Update(n => n with
        {
            Content = JsonSerializer.Deserialize<JsonElement>(
                content(JsonSerializer.SerializeToElement(n.Content, Mesh.JsonSerializerOptions))),
        });

    private string? FieldOf(MeshNode? node, string field) =>
        node?.Content is null
            ? null
            : JsonSerializer.SerializeToElement(node.Content, Mesh.JsonSerializerOptions)
                .TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    [Fact]
    public async Task ACodeEditorBoundToANodeField_RendersTheField_AndFollowsAChange()
    {
        var path = await SeedNode("""{"instructions":"first draft","other":"untouched"}""");
        var editor = new CodeEditorControl().WithLanguage("markdown").BindToNode(path, "instructions");

        var bound = Resolve(editor.Value, editor.DataContext).Replay();
        using var connection = bound.Connect();

        (await bound.Should().Within(TestTimeouts.Convergence).Match(
            v => v == "first draft", "the editor reads the field off the node — the area never loaded it")).Should().Be("first draft");

        // Somebody ELSE writes the node (an agent, another tab). The bound editor follows.
        await Updated(path, _ => """{"instructions":"second draft","other":"untouched"}""")
            .Should().Within(TestTimeouts.Convergence).Emit();

        (await bound.Should().Within(TestTimeouts.Convergence).Match(
            v => v == "second draft", "a bound editor FOLLOWS the node; a baked string would still say 'first draft'"))
            .Should().Be("second draft");
    }

    [Fact]
    public async Task AnEditInACodeEditorBoundToANodeField_WritesThatFieldOnly()
    {
        var path = await SeedNode("""{"instructions":"before","other":"untouched"}""");
        var editor = new CodeEditorControl().BindToNode(path, "instructions");
        var parsed = LayoutAreaReference.TryParseMeshNodeDataContext(editor.DataContext);
        parsed.Should().NotBeNull("BindToNode sets the node-bound context the write seam branches on");
        var ctx = parsed.GetValueOrDefault();
        var pointer = editor.Value.Should().BeOfType<JsonPointerReference>().Subject;

        // The renderer's write seam (BlazorView.UpdatePointer → MeshNodeBindingExtensions.Write).
        MeshNodeBindingExtensions.Write(Mesh, NullLogger.Instance, ctx.NodePath, ctx.BindContent, ctx.SubPath,
            pointer, "typed by the user");

        var node = await Mesh.GetMeshNodeStream(path)
            .Should().Within(TestTimeouts.Convergence).Match(
                n => FieldOf(n, "instructions") == "typed by the user",
                "the edit goes straight to the node — no /data copy, no Save button");
        FieldOf(node, "other").Should().Be("untouched", "a per-field write never clobbers a field it did not edit");
    }

    [Fact]
    public async Task ADiffEditorBoundToTwoNodeFields_RendersBothPanes_AndFollowsAChange()
    {
        var path = await SeedNode("""{"baselineText":"reviewed text","text":"reviewed text"}""");
        var diff = new DiffEditorControl { Language = "plaintext" }.BindToNode(path, "baselineText", "text");

        var original = Resolve(diff.Original, diff.DataContext).Replay();
        var modified = Resolve(diff.Modified, diff.DataContext).Replay();
        using var c1 = original.Connect();
        using var c2 = modified.Connect();

        await original.Should().Within(TestTimeouts.Convergence).Match(v => v == "reviewed text", "left pane = baseline");
        await modified.Should().Within(TestTimeouts.Convergence).Match(v => v == "reviewed text", "right pane = current");

        await Updated(path, _ => """{"baselineText":"reviewed text","text":"edited after review"}""")
            .Should().Within(TestTimeouts.Convergence).Emit();

        (await modified.Should().Within(TestTimeouts.Convergence).Match(
            v => v == "edited after review", "the right pane follows the node"))
            .Should().Be("edited after review");
        (await original.Should().Within(TestTimeouts.Convergence).Emit("the left pane still has its value"))
            .Should().Be("reviewed text", "only the edited field moved");
    }

    /// <summary>
    /// NEGATIVE CONTROLS. The literal shape a hub-baked area produced carries nothing a renderer
    /// could follow; and a pointer resolved against the wrong root draws empty and STAYS empty through
    /// the change — so the positive tests above measure the binding, not a stream that answers
    /// anything for any field.
    /// </summary>
    [Fact]
    public async Task NegativeControl_ABakedLiteralHasNothingToFollow_AndTheWrongRootStaysEmpty()
    {
        var path = await SeedNode("""{"instructions":"first draft"}""");

        var baked = new CodeEditorControl().WithValue("first draft");
        baked.Value.Should().BeOfType<string>("a baked editor holds the TEXT — a frozen copy");
        LayoutAreaReference.TryParseMeshNodeDataContext(baked.DataContext).Should().BeNull(
            "and no node context, so no renderer can follow the node");
        var bakedDiff = new DiffEditorControl { OriginalContent = "a", ModifiedContent = "b" };
        bakedDiff.Original.Should().BeNull("the literal strings stay literal — the bindable panes are opt-in");

        // Same field name, resolved against the whole NODE instead of its content: there is no
        // top-level "instructions", so it must draw empty — and stay empty after the content changes.
        var wrongRoot = new CodeEditorControl().BindToNode(path, "instructions", bindContent: false);
        var bound = Resolve(wrongRoot.Value, wrongRoot.DataContext).Replay();
        using var connection = bound.Connect();
        (await bound.Should().Within(TestTimeouts.Convergence).Emit("the binding answers"))
            .Should().BeNull("a pointer resolved against the wrong root finds no field");

        await Updated(path, _ => """{"instructions":"second draft"}""").Should().Within(TestTimeouts.Convergence).Emit();
        await bound.Where(v => v is not null).Should().NotEmit(TimeSpan.FromSeconds(2),
            "the change is to Content.instructions, which this binding does not point at");
    }

    /// <summary>The pointers and the node context survive the wire the renderer receives them over.</summary>
    [Fact]
    public void BoundControlsSurviveSerialization()
    {
        var options = Mesh.JsonSerializerOptions;
        var editor = new CodeEditorControl().BindToNode("p/agent", "instructions");
        var editorBack = JsonSerializer.Deserialize<UiControl>(
                JsonSerializer.Serialize<UiControl>(editor, options), options)
            .Should().BeOfType<CodeEditorControl>().Subject;
        editorBack.Value.Should().Be(new JsonPointerReference("instructions"));
        editorBack.DataContext.Should().Be(editor.DataContext);

        var diff = new DiffEditorControl().BindToNode("p/post", "baselineText", "text");
        var diffBack = JsonSerializer.Deserialize<UiControl>(
                JsonSerializer.Serialize<UiControl>(diff, options), options)
            .Should().BeOfType<DiffEditorControl>().Subject;
        diffBack.Original.Should().Be(new JsonPointerReference("baselineText"));
        diffBack.Modified.Should().Be(new JsonPointerReference("text"));
        diffBack.DataContext.Should().Be(diff.DataContext);
    }

    /// <summary>
    /// The documented escape hatch for a pane whose text is NOT on the node: an ABSOLUTE pointer
    /// (<c>/data/…</c>) is never node-bound, even under the node-bound DataContext
    /// <c>BindToNode</c> set, so the renderer resolves it against the layout area's data. The
    /// relative sibling on the same control stays node-bound — the control arm, so the
    /// <c>false</c> is the pointer's shape and not a context that failed to parse.
    /// </summary>
    [Fact]
    public void AnAbsolutePointerUnderANodeBoundContext_ReadsTheLayoutAreasData_NotTheNode()
    {
        var diff = new DiffEditorControl()
            .BindToNode("p/post", "baselineText", "text")
            .WithOriginal(new JsonPointerReference("/data/previousVersion"));

        var original = diff.Original.Should().BeOfType<JsonPointerReference>().Subject;
        var modified = diff.Modified.Should().BeOfType<JsonPointerReference>().Subject;
        MeshNodeBindingExtensions.IsNodeBound(diff.DataContext, original).Should().BeFalse(
            "an absolute pointer is a layout-area path — the pane is fed from /data");
        MeshNodeBindingExtensions.IsNodeBound(diff.DataContext, modified).Should().BeTrue(
            "the relative pane on the same control still reads the node");
    }

    /// <summary>
    /// <c>field</c> is a RELATIVE JSON POINTER, not a bare property name: a <c>/</c> descends, and a
    /// property whose NAME contains <c>/</c> is written escaped (<c>~1</c>). The unescaped spelling of
    /// that same name is the control arm — it descends instead and finds nothing.
    /// </summary>
    [Fact]
    public void TheField_IsARelativeJsonPointer_NestedAndEscaped()
    {
        var node = new MeshNode("n", "p")
        {
            Content = JsonSerializer.Deserialize<JsonElement>(
                """{"review":{"notes":"nested"},"a/b":"slash in the name"}"""),
        };
        string? Read(string field)
        {
            var editor = new CodeEditorControl().BindToNode("p/n", field);
            var pointer = editor.Value.Should().BeOfType<JsonPointerReference>().Subject;
            return Text(MeshNodeBindingExtensions.ResolveField(
                node, bindContent: true, subPath: null, pointer, Mesh.JsonSerializerOptions));
        }

        Read("review/notes").Should().Be("nested", "a / in the pointer descends into the nested object");
        Read("a~1b").Should().Be("slash in the name", "~1 is the escaped / of a property NAME");
        Read("a/b").Should().BeNull("unescaped, the same text is a path a → b, which does not exist");
    }

    /// <summary>A leading <c>/</c> would make the pointer absolute — bound to the layout area's data
    /// instead of the node, silently. The builders refuse it, and an empty field with it.</summary>
    [Theory]
    [InlineData("/instructions")]
    [InlineData("")]
    public void BindToNode_RefusesAnAbsoluteOrEmptyField(string field)
    {
        Action codeEditor = () => new CodeEditorControl().BindToNode("p/n", field);
        Action diffOriginal = () => new DiffEditorControl().BindToNode("p/n", field, "text");
        Action diffModified = () => new DiffEditorControl().BindToNode("p/n", "text", field);
        codeEditor.Should().Throw<ArgumentException>();
        diffOriginal.Should().Throw<ArgumentException>();
        diffModified.Should().Throw<ArgumentException>();
    }

    /// <summary>
    /// An editor has ONE write target. <c>WithAutoSave</c> writes <c>CodeConfiguration.Code</c>,
    /// <c>BindToNode</c> writes the bound field; a control carrying both would write each keystroke
    /// twice. <c>BindToNode</c> replaces an earlier auto-save address, and <c>WithAutoSave</c> refuses
    /// a node-bound editor. Control arm: on an editor that is NOT node-bound, <c>WithAutoSave</c>
    /// still works.
    /// </summary>
    [Fact]
    public void AutoSaveAndANodeBinding_AreMutuallyExclusive()
    {
        var bound = new CodeEditorControl().WithAutoSave("p/code").BindToNode("p/agent", "instructions");
        bound.AutoSaveAddress.Should().BeNull("the binding is the write path; the auto-save address is cleared");
        bound.Value.Should().Be(new JsonPointerReference("instructions"));

        Action secondWriteTarget = () => new CodeEditorControl().BindToNode("p/agent", "instructions").WithAutoSave("p/code");
        secondWriteTarget.Should().Throw<InvalidOperationException>("a second write target is refused, not silently added");

        new CodeEditorControl().WithValue("var x = 1;").WithAutoSave("p/code")
            .AutoSaveAddress.Should().Be("p/code", "the Code-node shape is unchanged");
    }

    /// <summary><c>WithOriginal(null)</c> / <c>WithModified(null)</c> clear the bindable slot, so the
    /// pane falls back to its literal.</summary>
    [Fact]
    public void ClearingABindablePane_FallsBackToTheLiteral()
    {
        var diff = new DiffEditorControl { OriginalContent = "a", ModifiedContent = "b" }
            .BindToNode("p/post", "baselineText", "text")
            .WithOriginal(null)
            .WithModified(null);
        diff.Original.Should().BeNull();
        diff.Modified.Should().BeNull();
        diff.OriginalContent.Should().Be("a");
        diff.ModifiedContent.Should().Be("b");
    }
}
