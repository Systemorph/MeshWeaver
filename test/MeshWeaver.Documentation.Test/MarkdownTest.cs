#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using System.Linq;
using FluentAssertions;
using Markdig;
using Markdig.Syntax;
using MeshWeaver.Fixture;
using MeshWeaver.Markdown;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// Testing custom markdown extensions
/// </summary>
/// <param name="output"></param>
public class MarkdownTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string TestAddress = "app/test";
    /// <summary>
    /// This tests the rendering of Layout Area Markdown
    /// </summary>
    [HubFact]
    public void BasicLayoutArea()
    {
        // Define a sample markdown string
        var markdown = $"@(\"{TestAddress}/MyArea\")";
        var extension = new LayoutAreaMarkdownExtension();
        var document = ParseMarkdown(markdown, extension);
        var layoutAreas = document.Descendants<LayoutAreaComponentInfo>().ToArray();
        layoutAreas.Should().HaveCount(1);
        var area = layoutAreas[0];
        area.Area.Should().Be("MyArea");

    }


    private static MarkdownDocument ParseMarkdown<TExtension>(string markdown, TExtension markdownExtension
    ) where TExtension : class, IMarkdownExtension
    {
        var pipeline = new MarkdownPipelineBuilder().Use(markdownExtension).Build();
        return Markdig.Markdown.Parse(markdown, pipeline);
    }

    private static string RenderMarkdown<TExtension>(string markdown, TExtension markdownExtension
    ) where TExtension : class, IMarkdownExtension
    {
        var pipeline = new MarkdownPipelineBuilder().Use(markdownExtension).Build();
        var html = Markdig.Markdown.ToHtml(markdown, pipeline);
        return html;
    }
    /// <summary>
    /// This tests the rendering of two Layout Area Markdown
    /// </summary>

    [HubFact]
    public void TwoLayoutAreas()
    {
        // Define a sample markdown string
        var markdown = "@(\"app/demo/Area1\")\n@(\"app/demo/Area2\")";
        var extension = new LayoutAreaMarkdownExtension();
        var document = ParseMarkdown( markdown, extension);
        var areas = document.Descendants<LayoutAreaComponentInfo>().ToArray();

        areas.Should().HaveCount(2);
        var area1 = areas[0];
        var area2 = areas[1];
        area1.Area.Should().Be("Area1");
        area2.Area.Should().Be("Area2");

    }


    /// <summary>
    ///
    /// </summary>
    [Fact]
    public void ParseCodeBlock()
    {
        var codeBlock =
            @"```csharp execute
Console.WriteLine(""Hello World"");
```";
        var extension = new ExecutableCodeBlockExtension();
        var html = RenderMarkdown(codeBlock, extension);
    }

    #region Unified Content Reference Tests

    [Fact]
    public void ParseDataContentReference()
    {
        var markdown = "@(\"data:app/test/MyCollection\")";
        var extension = new LayoutAreaMarkdownExtension();
        var document = ParseMarkdown(markdown, extension);

        // Data references are now translated to LayoutAreaComponentInfo with $Data area
        var layoutAreas = document.Descendants<LayoutAreaComponentInfo>().ToArray();
        layoutAreas.Should().HaveCount(1);

        var area = layoutAreas[0];
        area.Area.Should().Be(LayoutAreaMarkdownParser.DataAreaName);
        area.Id.Should().Be("MyCollection");
        area.Address.Should().Be("app/test");
    }

    [Fact]
    public void ParseDataContentReferenceWithEntityId()
    {
        var markdown = "@(\"data:app/test/MyCollection/entity123\")";
        var extension = new LayoutAreaMarkdownExtension();
        var document = ParseMarkdown(markdown, extension);

        var layoutAreas = document.Descendants<LayoutAreaComponentInfo>().ToArray();
        layoutAreas.Should().HaveCount(1);

        var area = layoutAreas[0];
        area.Area.Should().Be(LayoutAreaMarkdownParser.DataAreaName);
        area.Id.Should().Be("MyCollection/entity123");
    }

    [Fact]
    public void ParseDataContentReferenceDefault()
    {
        var markdown = "@(\"data:app/test\")";
        var extension = new LayoutAreaMarkdownExtension();
        var document = ParseMarkdown(markdown, extension);

        var layoutAreas = document.Descendants<LayoutAreaComponentInfo>().ToArray();
        layoutAreas.Should().HaveCount(1);

        var area = layoutAreas[0];
        area.Area.Should().Be(LayoutAreaMarkdownParser.DataAreaName);
        area.Id.Should().BeNull();
        area.Address.Should().Be("app/test");
    }

    [Fact]
    public void ParseFileContentReference()
    {
        var markdown = "@(\"content:app/test/Documents/report.pdf\")";
        var extension = new LayoutAreaMarkdownExtension();
        var document = ParseMarkdown(markdown, extension);

        // File references are now translated to LayoutAreaComponentInfo with $Content area
        var layoutAreas = document.Descendants<LayoutAreaComponentInfo>().ToArray();
        layoutAreas.Should().HaveCount(1);

        var area = layoutAreas[0];
        area.Area.Should().Be(LayoutAreaMarkdownParser.ContentAreaName);
        area.Id.Should().Be("Documents/report.pdf");
        area.Address.Should().Be("app/test");
    }

    [Fact]
    public void ParseFileContentReferenceWithPartition()
    {
        var markdown = "@(\"content:app/test/Documents@2024/report.pdf\")";
        var extension = new LayoutAreaMarkdownExtension();
        var document = ParseMarkdown(markdown, extension);

        var layoutAreas = document.Descendants<LayoutAreaComponentInfo>().ToArray();
        layoutAreas.Should().HaveCount(1);

        var area = layoutAreas[0];
        area.Area.Should().Be(LayoutAreaMarkdownParser.ContentAreaName);
        area.Id.Should().Be("Documents@2024/report.pdf");
    }

    [Fact]
    public void ParseFileContentReferenceWithNestedPath()
    {
        var markdown = "@(\"content:app/test/Documents/folder/subfolder/file.txt\")";
        var extension = new LayoutAreaMarkdownExtension();
        var document = ParseMarkdown(markdown, extension);

        var layoutAreas = document.Descendants<LayoutAreaComponentInfo>().ToArray();
        layoutAreas.Should().HaveCount(1);

        var area = layoutAreas[0];
        area.Id.Should().Be("Documents/folder/subfolder/file.txt");
    }

    [Fact]
    public void ParseAreaContentReference()
    {
        var markdown = "@(\"area:app/test/MyArea\")";
        var extension = new LayoutAreaMarkdownExtension();
        var document = ParseMarkdown(markdown, extension);

        var layoutAreas = document.Descendants<LayoutAreaComponentInfo>().ToArray();
        layoutAreas.Should().HaveCount(1);

        var area = layoutAreas[0];
        area.Area.Should().Be("MyArea");
        area.Address.Should().Be("app/test");
    }

    [Fact]
    public void ParseAreaContentReferenceWithId()
    {
        var markdown = "@(\"area:app/test/MyArea/item123\")";
        var extension = new LayoutAreaMarkdownExtension();
        var document = ParseMarkdown(markdown, extension);

        var layoutAreas = document.Descendants<LayoutAreaComponentInfo>().ToArray();
        layoutAreas.Should().HaveCount(1);

        var area = layoutAreas[0];
        area.Area.Should().Be("MyArea");
        area.Id.Should().Be("item123");
    }

    [Fact]
    public void LegacyFormatStillWorks()
    {
        // Ensure backward compatibility with existing format
        var markdown = "@(\"app/test/MyArea\")";
        var extension = new LayoutAreaMarkdownExtension();
        var document = ParseMarkdown(markdown, extension);

        var layoutAreas = document.Descendants<LayoutAreaComponentInfo>().ToArray();
        layoutAreas.Should().HaveCount(1);
        layoutAreas[0].Area.Should().Be("MyArea");
    }

    [Fact]
    public void RenderDataContentBlock()
    {
        var markdown = "@(\"data:app/test/Users\")";
        var extension = new LayoutAreaMarkdownExtension();
        var html = RenderMarkdown(markdown, extension);

        // Data references now render as layout area markers (without quotes around attribute values)
        html.Should().Contain("data-area=$Data");
        html.Should().Contain("data-address='app/test'");
    }

    [Fact]
    public void RenderFileContentBlock()
    {
        var markdown = "@(\"content:app/test/Docs/readme.md\")";
        var extension = new LayoutAreaMarkdownExtension();
        var html = RenderMarkdown(markdown, extension);

        // Content references now render as layout area markers (without quotes around attribute values)
        html.Should().Contain("data-area=$Content");
        html.Should().Contain("data-address='app/test'");
    }

    [Fact]
    public void MixedReferenceTypes()
    {
        var markdown = @"
@(""data:app/test/Users"")
@(""content:app/test/Docs/file.txt"")
@(""area:app/test/Dashboard"")
";
        var extension = new LayoutAreaMarkdownExtension();
        var document = ParseMarkdown(markdown, extension);

        // All three are now LayoutAreaComponentInfo with different areas
        var layoutAreas = document.Descendants<LayoutAreaComponentInfo>().ToArray();
        layoutAreas.Should().HaveCount(3);
        layoutAreas.Count(a => a.Area == LayoutAreaMarkdownParser.DataAreaName).Should().Be(1);
        layoutAreas.Count(a => a.Area == LayoutAreaMarkdownParser.ContentAreaName).Should().Be(1);
        layoutAreas.Count(a => a.Area == "Dashboard").Should().Be(1);
    }

    [Fact]
    public void DataContentBlockPath()
    {
        var markdown = "@(\"data:pricing/MS-2024/PropertyRisk/risk1\")";
        var extension = new LayoutAreaMarkdownExtension();
        var document = ParseMarkdown(markdown, extension);

        var layoutAreas = document.Descendants<LayoutAreaComponentInfo>().ToArray();
        layoutAreas.Should().HaveCount(1);

        var area = layoutAreas[0];
        area.Area.Should().Be(LayoutAreaMarkdownParser.DataAreaName);
        area.Id.Should().Be("PropertyRisk/risk1");
        area.Address.Should().Be("pricing/MS-2024");
    }

    [Fact]
    public void FileContentBlockPath()
    {
        var markdown = "@(\"content:host/1/Submissions@MS-2024/folder/file.xlsx\")";
        var extension = new LayoutAreaMarkdownExtension();
        var document = ParseMarkdown(markdown, extension);

        var layoutAreas = document.Descendants<LayoutAreaComponentInfo>().ToArray();
        layoutAreas.Should().HaveCount(1);

        var area = layoutAreas[0];
        area.Area.Should().Be(LayoutAreaMarkdownParser.ContentAreaName);
        area.Id.Should().Be("Submissions@MS-2024/folder/file.xlsx");
        area.Address.Should().Be("host/1");
    }

    #endregion

    #region Direct Path Syntax Tests (without parentheses)

    [Fact]
    public void DirectPathSyntax_LayoutArea()
    {
        // @app/test/MyArea without parentheses defaults to area reference
        var markdown = "@app/test/MyArea";
        var extension = new LayoutAreaMarkdownExtension();
        var document = ParseMarkdown(markdown, extension);

        var layoutAreas = document.Descendants<LayoutAreaComponentInfo>().ToArray();
        layoutAreas.Should().HaveCount(1);
        layoutAreas[0].Area.Should().Be("MyArea");
        layoutAreas[0].Address.Should().Be("app/test");
    }

    [Fact]
    public void DirectPathSyntax_LayoutAreaWithId()
    {
        var markdown = "@app/Northwind/AnnualReportSummary?Year=2025";
        var extension = new LayoutAreaMarkdownExtension();
        var document = ParseMarkdown(markdown, extension);

        var layoutAreas = document.Descendants<LayoutAreaComponentInfo>().ToArray();
        layoutAreas.Should().HaveCount(1);
        layoutAreas[0].Area.Should().Be("AnnualReportSummary");
        layoutAreas[0].Id.Should().Be("Year=2025");
        layoutAreas[0].Address.Should().Be("app/Northwind");
    }

    [Fact]
    public void DirectPathSyntax_SalesGrowthSummary()
    {
        var markdown = "@app/Northwind/SalesGrowthSummary?Year=2025";
        var extension = new LayoutAreaMarkdownExtension();
        var document = ParseMarkdown(markdown, extension);

        var layoutAreas = document.Descendants<LayoutAreaComponentInfo>().ToArray();
        layoutAreas.Should().HaveCount(1);
        layoutAreas[0].Area.Should().Be("SalesGrowthSummary");
        layoutAreas[0].Id.Should().Be("Year=2025");
        layoutAreas[0].Address.Should().Be("app/Northwind");
    }

    [Fact]
    public void DirectPathSyntax_DataReference()
    {
        var markdown = "@data:app/test/Users";
        var extension = new LayoutAreaMarkdownExtension();
        var document = ParseMarkdown(markdown, extension);

        var layoutAreas = document.Descendants<LayoutAreaComponentInfo>().ToArray();
        layoutAreas.Should().HaveCount(1);
        layoutAreas[0].Area.Should().Be(LayoutAreaMarkdownParser.DataAreaName);
        layoutAreas[0].Id.Should().Be("Users");
    }

    [Fact]
    public void DirectPathSyntax_ContentReference()
    {
        var markdown = "@content:app/test/Docs/readme.md";
        var extension = new LayoutAreaMarkdownExtension();
        var document = ParseMarkdown(markdown, extension);

        var layoutAreas = document.Descendants<LayoutAreaComponentInfo>().ToArray();
        layoutAreas.Should().HaveCount(1);
        layoutAreas[0].Area.Should().Be(LayoutAreaMarkdownParser.ContentAreaName);
        layoutAreas[0].Id.Should().Be("Docs/readme.md");
    }

    [Fact]
    public void DirectPathSyntax_MultipleReferences()
    {
        var markdown = @"
@app/test/Dashboard
@data:app/test/Users
@content:app/test/Docs/file.txt
";
        var extension = new LayoutAreaMarkdownExtension();
        var document = ParseMarkdown(markdown, extension);

        var layoutAreas = document.Descendants<LayoutAreaComponentInfo>().ToArray();
        layoutAreas.Should().HaveCount(3);
        layoutAreas.Count(a => a.Area == "Dashboard").Should().Be(1);
        layoutAreas.Count(a => a.Area == LayoutAreaMarkdownParser.DataAreaName).Should().Be(1);
        layoutAreas.Count(a => a.Area == LayoutAreaMarkdownParser.ContentAreaName).Should().Be(1);
    }

    [Fact]
    public void DirectPathSyntax_WithTextAfter()
    {
        // Path should stop at whitespace
        var markdown = "@app/test/MyArea some text after";
        var extension = new LayoutAreaMarkdownExtension();
        var document = ParseMarkdown(markdown, extension);

        var layoutAreas = document.Descendants<LayoutAreaComponentInfo>().ToArray();
        layoutAreas.Should().HaveCount(1);
        layoutAreas[0].Area.Should().Be("MyArea");
    }

    #endregion

    #region Quoted Syntax Tests (without parentheses)

    [Fact]
    public void QuotedSyntax_LayoutAreaWithSpaces()
    {
        // @"path with spaces" syntax
        var markdown = "@\"app/test/My Area\"";
        var extension = new LayoutAreaMarkdownExtension();
        var document = ParseMarkdown(markdown, extension);

        var layoutAreas = document.Descendants<LayoutAreaComponentInfo>().ToArray();
        layoutAreas.Should().HaveCount(1);
        layoutAreas[0].Area.Should().Be("My Area");
    }

    [Fact]
    public void QuotedSyntax_ContentReferenceWithSpaces()
    {
        var markdown = "@\"content:app/test/Docs/My Report 2025.pdf\"";
        var extension = new LayoutAreaMarkdownExtension();
        var document = ParseMarkdown(markdown, extension);

        var layoutAreas = document.Descendants<LayoutAreaComponentInfo>().ToArray();
        layoutAreas.Should().HaveCount(1);
        layoutAreas[0].Area.Should().Be(LayoutAreaMarkdownParser.ContentAreaName);
        layoutAreas[0].Id.Should().Be("Docs/My Report 2025.pdf");
    }

    [Fact]
    public void QuotedSyntax_DataReference()
    {
        var markdown = "@\"data:app/test/User Accounts\"";
        var extension = new LayoutAreaMarkdownExtension();
        var document = ParseMarkdown(markdown, extension);

        var layoutAreas = document.Descendants<LayoutAreaComponentInfo>().ToArray();
        layoutAreas.Should().HaveCount(1);
        layoutAreas[0].Area.Should().Be(LayoutAreaMarkdownParser.DataAreaName);
        layoutAreas[0].Id.Should().Be("User Accounts");
    }

    [Fact]
    public void QuotedSyntax_MixedWithDirect()
    {
        var markdown = @"
@app/test/SimpleArea
@""content:app/test/Docs/Report with spaces.pdf""
@data:app/test/Products
";
        var extension = new LayoutAreaMarkdownExtension();
        var document = ParseMarkdown(markdown, extension);

        var layoutAreas = document.Descendants<LayoutAreaComponentInfo>().ToArray();
        layoutAreas.Should().HaveCount(3);
        layoutAreas.Count(a => a.Area == "SimpleArea").Should().Be(1);
        layoutAreas.Count(a => a.Area == LayoutAreaMarkdownParser.ContentAreaName).Should().Be(1);
        layoutAreas.Count(a => a.Area == LayoutAreaMarkdownParser.DataAreaName).Should().Be(1);
    }

    #endregion
}
