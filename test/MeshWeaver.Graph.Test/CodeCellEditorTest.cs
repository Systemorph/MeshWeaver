using MeshWeaver.Data;
using MeshWeaver.Layout;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Pins the pure rules of the EDIT-MODE code cell (<c>CodeLayoutAreas.BuildCellEditor</c>):
/// a viewer holding Update renders the cell's code segment as an inline Monaco editor —
/// edit mode IS the mode, there is no Edit button and no second page. The editor control must
/// be DETERMINISTIC for a given node + seed (every auto-save echoes a node emission and
/// re-renders the cell; a control that varied would re-mount Monaco under the viewer's cursor).
/// </summary>
public class CodeCellEditorTest
{
    [Fact]
    public void Editor_AutoSavesIntoTheNode_AndBindsTheCellBuffer()
    {
        var editor = (CodeEditorControl)CodeLayoutAreas.BuildCellEditor(
            "Course/Lesson/Source/Cell", "csharp", "300px");

        editor.AutoSaveAddress.Should().Be("Course/Lesson/Source/Cell",
            "the debounced text persists back into THIS node");
        editor.DataContext.Should().Be(LayoutAreaReference.GetDataPointer(CodeLayoutAreas.CellBufferDataId),
            "the editor binds the once-seeded cell buffer, never a per-render id");
        editor.Height.Should().Be("300px");
    }

    [Fact]
    public void CSharp_GetsLanguageServices_ScopedToTheOwner()
    {
        var editor = (CodeEditorControl)CodeLayoutAreas.BuildCellEditor(
            "Course/Lesson/Source/Cell", "csharp", "300px");

        editor.LanguageServer.Should().NotBeNull("C# cells get live Roslyn diagnostics + completions");
        editor.LanguageServer!.NodeTypePath.Should().Be("Course/Lesson");
        editor.LanguageServer.SourcePath.Should().Be("Course/Lesson/Source/Cell");
    }

    [Fact]
    public void OtherLanguages_GetNoLanguageServer()
    {
        var editor = (CodeEditorControl)CodeLayoutAreas.BuildCellEditor(
            "Course/Lesson/Source/Cell", "python", "300px");

        editor.LanguageServer.Should().BeNull("only C# has the Roslyn-backed language service");
    }

    [Fact]
    public void OwnerPath_IsTheNodeAboveSource_ElseTheParent()
    {
        CodeLayoutAreas.OwnerPathOf("Type/Source/File.cs").Should().Be("Type");
        CodeLayoutAreas.OwnerPathOf("Course/Lesson/Source/Cell").Should().Be("Course/Lesson");
        CodeLayoutAreas.OwnerPathOf("A/B").Should().Be("A");
        CodeLayoutAreas.OwnerPathOf("A").Should().Be("A");
    }

    [Fact]
    public void EditorHeight_FollowsTheSeed_WithinTheClamp()
    {
        CodeLayoutAreas.CellEditorHeight(null).Should().Be("96px", "an empty cell keeps the floor");
        CodeLayoutAreas.CellEditorHeight("one line").Should().Be("96px");
        CodeLayoutAreas.CellEditorHeight(string.Join("\n", new string[10])).Should().Be("210px",
            "ten lines at Monaco's 19px plus the chrome");
        CodeLayoutAreas.CellEditorHeight(string.Join("\n", new string[100])).Should().Be("480px",
            "a long example clamps rather than swallowing the page");
    }
}
