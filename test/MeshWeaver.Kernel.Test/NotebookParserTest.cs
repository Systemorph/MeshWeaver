using System;
using System.Linq;
using FluentAssertions;
using MeshWeaver.Kernel;
using MeshWeaver.Layout;
using Xunit;

namespace MeshWeaver.Kernel.Test;

/// <summary>
/// Tests for NotebookParser to verify parsing markdown to cells and serializing back.
/// </summary>
public class NotebookParserTest(ITestOutputHelper output)
{
    private readonly ITestOutputHelper output = output;

    [Fact]
    public void ParseMarkdown_WithCodeBlock_ShouldCreateCodeCell()
    {
        // Arrange
        var markdown = @"```csharp --execute
Console.WriteLine(""Hello"");
```";

        // Act
        var cells = NotebookParser.ParseMarkdown(markdown);

        // Assert
        output.WriteLine($"Parsed {cells.Count} cells");
        cells.Should().HaveCount(1);

        var cell = cells[0];
        var skin = cell.Skins.OfType<NotebookCellSkin>().FirstOrDefault();
        skin.Should().NotBeNull();

        var cellType = skin!.CellType as NotebookCellType?;
        cellType.Should().Be(NotebookCellType.Code);

        var language = skin.Language as string;
        language.Should().Be("csharp");

        var content = cell.Content as string;
        content.Should().Contain("Console.WriteLine");

        output.WriteLine($"Cell type: {cellType}");
        output.WriteLine($"Language: {language}");
        output.WriteLine($"Content: {content}");
    }

    [Fact]
    public void ParseMarkdown_WithMarkdownContent_ShouldCreateMarkdownCell()
    {
        // Arrange
        var markdown = @"# Hello World

This is some markdown content.

- Item 1
- Item 2";

        // Act
        var cells = NotebookParser.ParseMarkdown(markdown);

        // Assert
        output.WriteLine($"Parsed {cells.Count} cells");
        cells.Should().HaveCount(1);

        var cell = cells[0];
        var skin = cell.Skins.OfType<NotebookCellSkin>().FirstOrDefault();
        skin.Should().NotBeNull();

        var cellType = skin!.CellType as NotebookCellType?;
        cellType.Should().Be(NotebookCellType.Markdown);

        var content = cell.Content as string;
        content.Should().Contain("# Hello World");
        content.Should().Contain("Item 1");

        output.WriteLine($"Cell type: {cellType}");
        output.WriteLine($"Content: {content}");
    }

    [Fact]
    public void ParseMarkdown_WithMixedContent_ShouldCreateMultipleCells()
    {
        // Arrange
        var markdown = @"# Introduction

This is the intro.

```csharp --execute
var x = 42;
Console.WriteLine(x);
```

## Results

The result is displayed above.

```python --execute
print('Hello from Python')
```";

        // Act
        var cells = NotebookParser.ParseMarkdown(markdown);

        // Assert
        output.WriteLine($"Parsed {cells.Count} cells");
        cells.Should().HaveCount(4); // markdown, code, markdown, code

        // First cell should be markdown
        var cell0Skin = cells[0].Skins.OfType<NotebookCellSkin>().First();
        (cell0Skin.CellType as NotebookCellType?).Should().Be(NotebookCellType.Markdown);
        (cells[0].Content as string).Should().Contain("Introduction");
        output.WriteLine($"Cell 0: Markdown - {(cells[0].Content as string)?[..Math.Min(30, ((string)cells[0].Content!).Length)]}...");

        // Second cell should be C# code
        var cell1Skin = cells[1].Skins.OfType<NotebookCellSkin>().First();
        (cell1Skin.CellType as NotebookCellType?).Should().Be(NotebookCellType.Code);
        (cell1Skin.Language as string).Should().Be("csharp");
        (cells[1].Content as string).Should().Contain("var x = 42");
        output.WriteLine($"Cell 1: Code (csharp) - {(cells[1].Content as string)?[..Math.Min(30, ((string)cells[1].Content!).Length)]}...");

        // Third cell should be markdown
        var cell2Skin = cells[2].Skins.OfType<NotebookCellSkin>().First();
        (cell2Skin.CellType as NotebookCellType?).Should().Be(NotebookCellType.Markdown);
        (cells[2].Content as string).Should().Contain("Results");
        output.WriteLine($"Cell 2: Markdown - {(cells[2].Content as string)?[..Math.Min(30, ((string)cells[2].Content!).Length)]}...");

        // Fourth cell should be Python code
        var cell3Skin = cells[3].Skins.OfType<NotebookCellSkin>().First();
        (cell3Skin.CellType as NotebookCellType?).Should().Be(NotebookCellType.Code);
        (cell3Skin.Language as string).Should().Be("python");
        (cells[3].Content as string).Should().Contain("print");
        output.WriteLine($"Cell 3: Code (python) - {(cells[3].Content as string)?[..Math.Min(30, ((string)cells[3].Content!).Length)]}...");
    }

    [Fact]
    public void ParseMarkdown_ThenSerialize_ShouldRoundtrip()
    {
        // Arrange
        var originalMarkdown = @"# My Notebook

Some intro text here.

```csharp --execute cell1
var message = ""Hello World"";
Console.WriteLine(message);
```

## Section Two

More explanatory text.

```python --execute cell2
result = 1 + 2
print(result)
```";

        // Act - Parse
        var cells = NotebookParser.ParseMarkdown(originalMarkdown);
        output.WriteLine($"Parsed {cells.Count} cells");

        // Act - Serialize back
        var serialized = NotebookParser.SerializeToMarkdown(cells);
        output.WriteLine($"Serialized markdown:\n{serialized}");

        // Act - Parse again
        var reparsedCells = NotebookParser.ParseMarkdown(serialized);
        output.WriteLine($"Re-parsed {reparsedCells.Count} cells");

        // Assert - Same number of cells
        reparsedCells.Should().HaveCount(cells.Count);

        // Assert - Same content in each cell
        for (int i = 0; i < cells.Count; i++)
        {
            var originalSkin = cells[i].Skins.OfType<NotebookCellSkin>().First();
            var reparsedSkin = reparsedCells[i].Skins.OfType<NotebookCellSkin>().First();

            (reparsedSkin.CellType as NotebookCellType?).Should().Be(originalSkin.CellType as NotebookCellType?);

            if ((originalSkin.CellType as NotebookCellType?) == NotebookCellType.Code)
            {
                (reparsedSkin.Language as string).Should().Be(originalSkin.Language as string);
            }

            output.WriteLine($"Cell {i}: Type={originalSkin.CellType}, Content matches: {(cells[i].Content as string)?.Trim() == (reparsedCells[i].Content as string)?.Trim()}");
        }
    }

    [Fact]
    public void NotebookControl_WithCells_ShouldStoreCells()
    {
        // Arrange
        var cell1 = NotebookCellControl.Code("var x = 1;", "csharp");
        var cell2 = NotebookCellControl.Markdown("# Header");
        var cell3 = NotebookCellControl.Code("print('hello')", "python");

        // Act
        var notebook = new NotebookControl()
            .WithCell(cell1)
            .WithCell(cell2)
            .WithCell(cell3);

        // Assert
        output.WriteLine($"Notebook has {notebook.Cells.Count} cells");
        notebook.Cells.Should().HaveCount(3);

        // Verify each cell
        var cells = notebook.Cells.ToList();

        var skin0 = cells[0].Skins.OfType<NotebookCellSkin>().First();
        (skin0.CellType as NotebookCellType?).Should().Be(NotebookCellType.Code);
        (skin0.Language as string).Should().Be("csharp");
        (cells[0].Content as string).Should().Be("var x = 1;");
        output.WriteLine($"Cell 0: {skin0.CellType} ({skin0.Language}) - {cells[0].Content}");

        var skin1 = cells[1].Skins.OfType<NotebookCellSkin>().First();
        (skin1.CellType as NotebookCellType?).Should().Be(NotebookCellType.Markdown);
        (cells[1].Content as string).Should().Be("# Header");
        output.WriteLine($"Cell 1: {skin1.CellType} - {cells[1].Content}");

        var skin2 = cells[2].Skins.OfType<NotebookCellSkin>().First();
        (skin2.CellType as NotebookCellType?).Should().Be(NotebookCellType.Code);
        (skin2.Language as string).Should().Be("python");
        (cells[2].Content as string).Should().Be("print('hello')");
        output.WriteLine($"Cell 2: {skin2.CellType} ({skin2.Language}) - {cells[2].Content}");
    }

    [Fact]
    public void NotebookControl_WithCellsFromParser_ShouldStoreCells()
    {
        // Arrange - Parse markdown
        var markdown = @"# Test Notebook

Some description.

```csharp --execute
Console.WriteLine(""Hello"");
```

```python --execute
print('World')
```";

        var parsedCells = NotebookParser.ParseMarkdown(markdown);
        output.WriteLine($"Parsed {parsedCells.Count} cells from markdown");

        // Act - Create notebook with parsed cells
        var notebook = new NotebookControl().WithCells(parsedCells);

        // Assert - Cells should be accessible via Cells property
        output.WriteLine($"Notebook.Cells has {notebook.Cells.Count} cells");
        notebook.Cells.Should().HaveCount(parsedCells.Count);

        // Verify each cell is accessible
        for (int i = 0; i < parsedCells.Count; i++)
        {
            var originalSkin = parsedCells[i].Skins.OfType<NotebookCellSkin>().FirstOrDefault();
            var notebookCellSkin = notebook.Cells[i].Skins.OfType<NotebookCellSkin>().FirstOrDefault();

            notebookCellSkin.Should().NotBeNull($"Cell {i} should have a skin");
            (notebookCellSkin!.CellType as NotebookCellType?).Should().Be(originalSkin?.CellType as NotebookCellType?);
            (notebook.Cells[i].Content as string).Should().Be(parsedCells[i].Content as string);

            output.WriteLine($"Cell {i}: Type={notebookCellSkin.CellType}, Content length={(notebook.Cells[i].Content as string)?.Length ?? 0}");
        }
    }

    [Fact]
    public void NotebookControl_EditCell_ThenSerialize_ShouldReflectChanges()
    {
        // Arrange - Create notebook from markdown
        var markdown = @"# Original Title

Original description.

```csharp --execute
var original = 1;
```";

        var cells = NotebookParser.ParseMarkdown(markdown);
        var notebook = new NotebookControl().WithCells(cells);

        output.WriteLine($"Original notebook has {notebook.Cells.Count} cells");

        // Act - Modify a cell (simulate user editing)
        var cellsList = notebook.Cells.ToList();

        // Modify the code cell content
        var codeCell = cellsList[1];
        var modifiedCodeCell = codeCell.WithContent("var modified = 42;\nConsole.WriteLine(modified);");
        cellsList[1] = modifiedCodeCell;

        // Create new notebook with modified cells
        var modifiedNotebook = new NotebookControl().WithCells(cellsList);

        // Serialize back to markdown
        var serialized = NotebookParser.SerializeToMarkdown(modifiedNotebook.Cells);
        output.WriteLine($"Serialized markdown:\n{serialized}");

        // Assert - Serialized should contain modified content
        serialized.Should().Contain("var modified = 42");
        serialized.Should().Contain("Console.WriteLine(modified)");
        serialized.Should().NotContain("var original = 1");
    }

    [Fact]
    public void ParseMarkdown_EmptyString_ShouldCreateEmptyCodeCell()
    {
        // Arrange
        var markdown = "";

        // Act
        var cells = NotebookParser.ParseMarkdown(markdown);

        // Assert
        output.WriteLine($"Parsed {cells.Count} cells from empty string");
        cells.Should().HaveCount(1);

        var skin = cells[0].Skins.OfType<NotebookCellSkin>().FirstOrDefault();
        (skin?.CellType as NotebookCellType?).Should().Be(NotebookCellType.Code);
        (cells[0].Content as string).Should().BeEmpty();
    }

    [Fact]
    public void NotebookCellControl_Code_ShouldHaveCorrectProperties()
    {
        // Act
        var cell = NotebookCellControl.Code("var x = 1;", "csharp");

        // Assert
        var skin = cell.Skins.OfType<NotebookCellSkin>().FirstOrDefault();
        skin.Should().NotBeNull();
        (skin!.CellType as NotebookCellType?).Should().Be(NotebookCellType.Code);
        (skin.Language as string).Should().Be("csharp");
        (cell.Content as string).Should().Be("var x = 1;");

        output.WriteLine($"CellType: {skin.CellType}");
        output.WriteLine($"Language: {skin.Language}");
        output.WriteLine($"Content: {cell.Content}");
    }

    [Fact]
    public void NotebookCellControl_Markdown_ShouldHaveCorrectProperties()
    {
        // Act
        var cell = NotebookCellControl.Markdown("# Hello World");

        // Assert
        var skin = cell.Skins.OfType<NotebookCellSkin>().FirstOrDefault();
        skin.Should().NotBeNull();
        (skin!.CellType as NotebookCellType?).Should().Be(NotebookCellType.Markdown);
        (skin.Language as string).Should().Be("markdown");
        (cell.Content as string).Should().Be("# Hello World");

        output.WriteLine($"CellType: {skin.CellType}");
        output.WriteLine($"Language: {skin.Language}");
        output.WriteLine($"Content: {cell.Content}");
    }
}
