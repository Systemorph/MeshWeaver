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
        // New format: addressType/addressId/keyword/path
        var markdown = "@(\"app/test/data/MyCollection\")";
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
        // New format: addressType/addressId/keyword/path
        var markdown = "@(\"app/test/data/MyCollection/entity123\")";
        var extension = new LayoutAreaMarkdownExtension();
        var document = ParseMarkdown(markdown, extension);

        var layoutAreas = document.Descendants<LayoutAreaComponentInfo>().ToArray();
        layoutAreas.Should().HaveCount(1);

        var area = layoutAreas[0];
        area.Area.Should().Be(LayoutAreaMarkdownParser.DataAreaName);
        area.Id.Should().Be("MyCollection/entity123");
        area.Address.Should().Be("app/test");
    }

    [Fact]
    public void ParseDataContentReferenceDefault()
    {
        // New format: addressType/addressId/keyword
        var markdown = "@(\"app/test/data\")";
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
        // Colon syntax for content collections: address/content:path
        var markdown = "@(\"app/test/content:Documents/report.pdf\")";
        var extension = new LayoutAreaMarkdownExtension();
        var document = ParseMarkdown(markdown, extension);

        // File references are translated to LayoutAreaComponentInfo with $Content area
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
        // Colon syntax for content collections: address/content:path
        var markdown = "@(\"app/test/content:Documents@2024/report.pdf\")";
        var extension = new LayoutAreaMarkdownExtension();
        var document = ParseMarkdown(markdown, extension);

        var layoutAreas = document.Descendants<LayoutAreaComponentInfo>().ToArray();
        layoutAreas.Should().HaveCount(1);

        var area = layoutAreas[0];
        area.Area.Should().Be(LayoutAreaMarkdownParser.ContentAreaName);
        area.Id.Should().Be("Documents@2024/report.pdf");
        area.Address.Should().Be("app/test");
    }

    [Fact]
    public void ParseFileContentReferenceWithNestedPath()
    {
        // Colon syntax for content collections: address/content:path
        var markdown = "@(\"app/test/content:Documents/folder/subfolder/file.txt\")";
        var extension = new LayoutAreaMarkdownExtension();
        var document = ParseMarkdown(markdown, extension);

        var layoutAreas = document.Descendants<LayoutAreaComponentInfo>().ToArray();
        layoutAreas.Should().HaveCount(1);

        var area = layoutAreas[0];
        area.Area.Should().Be(LayoutAreaMarkdownParser.ContentAreaName);
        area.Id.Should().Be("Documents/folder/subfolder/file.txt");
        area.Address.Should().Be("app/test");
    }

    [Fact]
    public void ParseAreaContentReference()
    {
        // New format: addressType/addressId/keyword/path
        var markdown = "@(\"app/test/area/MyArea\")";
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
        // New format: addressType/addressId/keyword/path
        var markdown = "@(\"app/test/area/MyArea/item123\")";
        var extension = new LayoutAreaMarkdownExtension();
        var document = ParseMarkdown(markdown, extension);

        var layoutAreas = document.Descendants<LayoutAreaComponentInfo>().ToArray();
        layoutAreas.Should().HaveCount(1);

        var area = layoutAreas[0];
        area.Area.Should().Be("MyArea");
        area.Id.Should().Be("item123");
        area.Address.Should().Be("app/test");
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
        // New format: addressType/addressId/keyword/path
        var markdown = "@(\"app/test/data/Users\")";
        var extension = new LayoutAreaMarkdownExtension();
        var html = RenderMarkdown(markdown, extension);

        // Data references render as layout area markers
        html.Should().Contain("data-area='$Data'");
        html.Should().Contain("data-address='app/test'");
    }

    [Fact]
    public void RenderFileContentBlock()
    {
        // Colon syntax for content collections: address/content:path
        var markdown = "@(\"app/test/content:Docs/readme.md\")";
        var extension = new LayoutAreaMarkdownExtension();
        var html = RenderMarkdown(markdown, extension);

        // Content references render as layout area markers
        html.Should().Contain("data-area='$Content'");
        html.Should().Contain("data-address='app/test'");
    }

    [Fact]
    public void MixedReferenceTypes()
    {
        // Colon syntax for content collections, slash syntax for reserved keywords
        var markdown = @"
@(""app/test/data/Users"")
@(""app/test/content:Docs/file.txt"")
@(""app/test/area/Dashboard"")
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
        // New format: addressType/addressId/keyword/path
        var markdown = "@(\"pricing/MS-2024/data/PropertyRisk/risk1\")";
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
        // Colon syntax for content collections: address/content:path
        var markdown = "@(\"host/1/content:Submissions@MS-2024/folder/file.xlsx\")";
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
        // New format: addressType/addressId/keyword/path
        var markdown = "@app/test/data/Users";
        var extension = new LayoutAreaMarkdownExtension();
        var document = ParseMarkdown(markdown, extension);

        var layoutAreas = document.Descendants<LayoutAreaComponentInfo>().ToArray();
        layoutAreas.Should().HaveCount(1);
        layoutAreas[0].Area.Should().Be(LayoutAreaMarkdownParser.DataAreaName);
        layoutAreas[0].Id.Should().Be("Users");
        layoutAreas[0].Address.Should().Be("app/test");
    }

    [Fact]
    public void DirectPathSyntax_ContentReference()
    {
        // Colon syntax for content collections: address/content:path
        var markdown = "@app/test/content:Docs/readme.md";
        var extension = new LayoutAreaMarkdownExtension();
        var document = ParseMarkdown(markdown, extension);

        var layoutAreas = document.Descendants<LayoutAreaComponentInfo>().ToArray();
        layoutAreas.Should().HaveCount(1);
        layoutAreas[0].Area.Should().Be(LayoutAreaMarkdownParser.ContentAreaName);
        layoutAreas[0].Id.Should().Be("Docs/readme.md");
        layoutAreas[0].Address.Should().Be("app/test");
    }

    [Fact]
    public void DirectPathSyntax_MultipleReferences()
    {
        // Colon syntax for content collections, slash syntax for reserved keywords
        var markdown = @"
@app/test/Dashboard
@app/test/data/Users
@app/test/content:Docs/file.txt
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
        // Colon syntax for content collections: address/content:path
        var markdown = "@\"app/test/content:Docs/My Report 2025.pdf\"";
        var extension = new LayoutAreaMarkdownExtension();
        var document = ParseMarkdown(markdown, extension);

        var layoutAreas = document.Descendants<LayoutAreaComponentInfo>().ToArray();
        layoutAreas.Should().HaveCount(1);
        layoutAreas[0].Area.Should().Be(LayoutAreaMarkdownParser.ContentAreaName);
        layoutAreas[0].Id.Should().Be("Docs/My Report 2025.pdf");
        layoutAreas[0].Address.Should().Be("app/test");
    }

    [Fact]
    public void QuotedSyntax_DataReference()
    {
        // New format: addressType/addressId/keyword/path
        var markdown = "@\"app/test/data/User Accounts\"";
        var extension = new LayoutAreaMarkdownExtension();
        var document = ParseMarkdown(markdown, extension);

        var layoutAreas = document.Descendants<LayoutAreaComponentInfo>().ToArray();
        layoutAreas.Should().HaveCount(1);
        layoutAreas[0].Area.Should().Be(LayoutAreaMarkdownParser.DataAreaName);
        layoutAreas[0].Id.Should().Be("User Accounts");
        layoutAreas[0].Address.Should().Be("app/test");
    }

    [Fact]
    public void QuotedSyntax_MixedWithDirect()
    {
        // Colon syntax for content collections, slash syntax for reserved keywords
        var markdown = @"
@app/test/SimpleArea
@""app/test/content:Docs/Report with spaces.pdf""
@app/test/data/Products
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

    #region Double At (@@) Inline Syntax Tests

    [Fact]
    public void DoubleAt_SetsIsInlineTrue()
    {
        // @@ syntax should set IsInline = true
        var markdown = "@@app/test/MyArea";
        var extension = new LayoutAreaMarkdownExtension();
        var document = ParseMarkdown(markdown, extension);

        var layoutAreas = document.Descendants<LayoutAreaComponentInfo>().ToArray();
        layoutAreas.Should().HaveCount(1);
        layoutAreas[0].IsInline.Should().BeTrue();
        layoutAreas[0].Area.Should().Be("MyArea");
    }

    [Fact]
    public void SingleAt_SetsIsInlineFalse()
    {
        // @ syntax should set IsInline = false (hyperlink)
        var markdown = "@app/test/MyArea";
        var extension = new LayoutAreaMarkdownExtension();
        var document = ParseMarkdown(markdown, extension);

        var layoutAreas = document.Descendants<LayoutAreaComponentInfo>().ToArray();
        layoutAreas.Should().HaveCount(1);
        layoutAreas[0].IsInline.Should().BeFalse();
        layoutAreas[0].Area.Should().Be("MyArea");
    }

    [Fact]
    public void DoubleAt_WithDataReference()
    {
        var markdown = "@@app/test/data/Users";
        var extension = new LayoutAreaMarkdownExtension();
        var document = ParseMarkdown(markdown, extension);

        var layoutAreas = document.Descendants<LayoutAreaComponentInfo>().ToArray();
        layoutAreas.Should().HaveCount(1);
        layoutAreas[0].IsInline.Should().BeTrue();
        layoutAreas[0].Area.Should().Be(LayoutAreaMarkdownParser.DataAreaName);
        layoutAreas[0].Id.Should().Be("Users");
    }

    [Fact]
    public void DoubleAt_WithContentReference()
    {
        // Colon syntax for content collections: address/content:path
        var markdown = "@@app/test/content:Docs/report.pdf";
        var extension = new LayoutAreaMarkdownExtension();
        var document = ParseMarkdown(markdown, extension);

        var layoutAreas = document.Descendants<LayoutAreaComponentInfo>().ToArray();
        layoutAreas.Should().HaveCount(1);
        layoutAreas[0].IsInline.Should().BeTrue();
        layoutAreas[0].Area.Should().Be(LayoutAreaMarkdownParser.ContentAreaName);
        layoutAreas[0].Id.Should().Be("Docs/report.pdf");
    }

    [Fact]
    public void DoubleAt_WithQuotedPath()
    {
        // Colon syntax for content collections: address/content:path
        var markdown = "@@\"app/test/content:Docs/My Report.pdf\"";
        var extension = new LayoutAreaMarkdownExtension();
        var document = ParseMarkdown(markdown, extension);

        var layoutAreas = document.Descendants<LayoutAreaComponentInfo>().ToArray();
        layoutAreas.Should().HaveCount(1);
        layoutAreas[0].IsInline.Should().BeTrue();
        layoutAreas[0].Area.Should().Be(LayoutAreaMarkdownParser.ContentAreaName);
        layoutAreas[0].Id.Should().Be("Docs/My Report.pdf");
    }

    [Fact]
    public void DoubleAt_WithParenthesesSyntax()
    {
        var markdown = "@@(\"app/test/Dashboard\")";
        var extension = new LayoutAreaMarkdownExtension();
        var document = ParseMarkdown(markdown, extension);

        var layoutAreas = document.Descendants<LayoutAreaComponentInfo>().ToArray();
        layoutAreas.Should().HaveCount(1);
        layoutAreas[0].IsInline.Should().BeTrue();
        layoutAreas[0].Area.Should().Be("Dashboard");
    }

    [Fact]
    public void RenderSingleAt_RendersAsHyperlink()
    {
        var markdown = "@app/test/Dashboard";
        var extension = new LayoutAreaMarkdownExtension();
        var html = RenderMarkdown(markdown, extension);

        // Single @ should render as a hyperlink with ucr-link class
        html.Should().Contain("class='ucr-link'");
        html.Should().Contain("href=");
        html.Should().Contain("data-address='app/test'");
    }

    [Fact]
    public void RenderDoubleAt_RendersAsLayoutArea()
    {
        var markdown = "@@app/test/Dashboard";
        var extension = new LayoutAreaMarkdownExtension();
        var html = RenderMarkdown(markdown, extension);

        // Double @ should render as inline layout area div
        html.Should().Contain("class='layout-area'");
        html.Should().Contain("data-address='app/test'");
        html.Should().Contain("data-area='Dashboard'");
    }

    [Fact]
    public void MixedSingleAndDoubleAt()
    {
        // Note: @ references work at block level (start of line), not inline within text
        // Each reference must be on its own line for the block parser to handle it
        var markdown = @"@app/test/Dashboard

@@app/test/SalesByCategory

@app/test/OrderSummary";
        var extension = new LayoutAreaMarkdownExtension();
        var document = ParseMarkdown(markdown, extension);

        var layoutAreas = document.Descendants<LayoutAreaComponentInfo>().ToArray();
        layoutAreas.Should().HaveCount(3);

        // First should be hyperlink (IsInline = false, single @)
        layoutAreas[0].IsInline.Should().BeFalse();
        layoutAreas[0].Area.Should().Be("Dashboard");

        // Second should be inline (IsInline = true, double @@)
        layoutAreas[1].IsInline.Should().BeTrue();
        layoutAreas[1].Area.Should().Be("SalesByCategory");

        // Third should be hyperlink (IsInline = false, single @)
        layoutAreas[2].IsInline.Should().BeFalse();
        layoutAreas[2].Area.Should().Be("OrderSummary");
    }

    [Fact]
    public void MixedSingleAndDoubleAt_RendersDifferentHtml()
    {
        var markdown = @"
@app/test/Dashboard
@@app/test/SalesByCategory
";
        var extension = new LayoutAreaMarkdownExtension();
        var html = RenderMarkdown(markdown, extension);

        // Should have one hyperlink and one layout area div
        html.Should().Contain("class='ucr-link'");
        html.Should().Contain("class='layout-area'");
    }

    [Fact]
    public void BackwardCompatibility_LegacyAtSyntax()
    {
        // Existing @ syntax should work and render as hyperlink (IsInline = false by default)
        var markdown = "@(\"app/test/MyArea\")";
        var extension = new LayoutAreaMarkdownExtension();
        var document = ParseMarkdown(markdown, extension);

        var layoutAreas = document.Descendants<LayoutAreaComponentInfo>().ToArray();
        layoutAreas.Should().HaveCount(1);
        layoutAreas[0].IsInline.Should().BeFalse();
        layoutAreas[0].Area.Should().Be("MyArea");
    }

    #endregion

    #region Real Path Tests (Systemorph/Marketing style)

    [Fact]
    public void DirectPath_SingleSegment()
    {
        // @Systemorph - just namespace, no child path
        var markdown = "@Systemorph";
        var extension = new LayoutAreaMarkdownExtension();
        var document = ParseMarkdown(markdown, extension);

        var layoutAreas = document.Descendants<LayoutAreaComponentInfo>().ToArray();
        layoutAreas.Should().HaveCount(1);
        layoutAreas[0].Address.Should().Be("Systemorph");
        layoutAreas[0].Area.Should().BeNull();
        layoutAreas[0].IsInline.Should().BeFalse();
    }

    [Fact]
    public void DirectPath_TwoSegments()
    {
        // @Systemorph/Marketing - namespace/id format
        var markdown = "@Systemorph/Marketing";
        var extension = new LayoutAreaMarkdownExtension();
        var document = ParseMarkdown(markdown, extension);

        var layoutAreas = document.Descendants<LayoutAreaComponentInfo>().ToArray();
        layoutAreas.Should().HaveCount(1);
        layoutAreas[0].Address.Should().Be("Systemorph/Marketing");
        layoutAreas[0].Area.Should().BeNull(); // No area specified, just address
    }

    [Fact]
    public void DirectPath_ThreeSegments_DefaultsToArea()
    {
        // @Systemorph/Marketing/BeyondPoC - defaults to area reference
        var markdown = "@Systemorph/Marketing/BeyondPoC";
        var extension = new LayoutAreaMarkdownExtension();
        var document = ParseMarkdown(markdown, extension);

        var layoutAreas = document.Descendants<LayoutAreaComponentInfo>().ToArray();
        layoutAreas.Should().HaveCount(1);
        layoutAreas[0].Address.Should().Be("Systemorph/Marketing");
        layoutAreas[0].Area.Should().Be("BeyondPoC");
    }

    [Fact]
    public void DirectPath_FourSegments_AreaWithId()
    {
        // @ACME/ProductLaunch/Todo/SalesDeck - area with id
        var markdown = "@ACME/ProductLaunch/Todo/SalesDeck";
        var extension = new LayoutAreaMarkdownExtension();
        var document = ParseMarkdown(markdown, extension);

        var layoutAreas = document.Descendants<LayoutAreaComponentInfo>().ToArray();
        layoutAreas.Should().HaveCount(1);
        layoutAreas[0].Address.Should().Be("ACME/ProductLaunch");
        layoutAreas[0].Area.Should().Be("Todo");
        layoutAreas[0].Id.Should().Be("SalesDeck");
    }

    [Fact]
    public void DirectPath_WithDataKeyword()
    {
        // @Systemorph/Marketing/data/Posts
        var markdown = "@Systemorph/Marketing/data/Posts";
        var extension = new LayoutAreaMarkdownExtension();
        var document = ParseMarkdown(markdown, extension);

        var layoutAreas = document.Descendants<LayoutAreaComponentInfo>().ToArray();
        layoutAreas.Should().HaveCount(1);
        layoutAreas[0].Address.Should().Be("Systemorph/Marketing");
        layoutAreas[0].Area.Should().Be(LayoutAreaMarkdownParser.DataAreaName);
        layoutAreas[0].Id.Should().Be("Posts");
    }

    [Fact]
    public void DoubleAt_TwoSegments_InlineRender()
    {
        // @@Systemorph/Marketing - inline embed of address
        var markdown = "@@Systemorph/Marketing";
        var extension = new LayoutAreaMarkdownExtension();
        var document = ParseMarkdown(markdown, extension);

        var layoutAreas = document.Descendants<LayoutAreaComponentInfo>().ToArray();
        layoutAreas.Should().HaveCount(1);
        layoutAreas[0].IsInline.Should().BeTrue();
        layoutAreas[0].Address.Should().Be("Systemorph/Marketing");
    }

    [Fact]
    public void DoubleAt_ThreeSegments_InlineRender()
    {
        // @@Systemorph/Marketing/BeyondPoC - inline embed of area
        var markdown = "@@Systemorph/Marketing/BeyondPoC";
        var extension = new LayoutAreaMarkdownExtension();
        var document = ParseMarkdown(markdown, extension);

        var layoutAreas = document.Descendants<LayoutAreaComponentInfo>().ToArray();
        layoutAreas.Should().HaveCount(1);
        layoutAreas[0].IsInline.Should().BeTrue();
        layoutAreas[0].Address.Should().Be("Systemorph/Marketing");
        layoutAreas[0].Area.Should().Be("BeyondPoC");
    }

    [Fact]
    public void RenderSingleAt_TwoSegments_RendersAsHyperlink()
    {
        var markdown = "@Systemorph/Marketing";
        var extension = new LayoutAreaMarkdownExtension();
        var html = RenderMarkdown(markdown, extension);

        html.Should().Contain("class='ucr-link'");
        html.Should().Contain("href='/Systemorph/Marketing'");
        html.Should().Contain("data-raw-path='Systemorph/Marketing'");
    }

    [Fact]
    public void RenderDoubleAt_TwoSegments_RendersAsLayoutArea()
    {
        var markdown = "@@Systemorph/Marketing";
        var extension = new LayoutAreaMarkdownExtension();
        var html = RenderMarkdown(markdown, extension);

        html.Should().Contain("class='layout-area'");
        // UCR paths use data-raw-path for runtime resolution via IMeshCatalog
        html.Should().Contain("data-raw-path='Systemorph/Marketing'");
    }

    [Fact]
    public void MultipleReferences_MixedFormats()
    {
        var markdown = @"
@Systemorph/Marketing
@@Systemorph/Marketing/BeyondPoC
@ACME/ProductLaunch/Todo/SalesDeck
";
        var extension = new LayoutAreaMarkdownExtension();
        var document = ParseMarkdown(markdown, extension);

        var layoutAreas = document.Descendants<LayoutAreaComponentInfo>().ToArray();
        layoutAreas.Should().HaveCount(3);

        // First: two segments
        layoutAreas[0].Address.Should().Be("Systemorph/Marketing");
        layoutAreas[0].IsInline.Should().BeFalse();

        // Second: three segments with @@
        layoutAreas[1].Address.Should().Be("Systemorph/Marketing");
        layoutAreas[1].Area.Should().Be("BeyondPoC");
        layoutAreas[1].IsInline.Should().BeTrue();

        // Third: four segments with area/id
        layoutAreas[2].Address.Should().Be("ACME/ProductLaunch");
        layoutAreas[2].Area.Should().Be("Todo");
        layoutAreas[2].Id.Should().Be("SalesDeck");
        layoutAreas[2].IsInline.Should().BeFalse();
    }

    [Fact]
    public void PrefixColonSyntax_Content()
    {
        // New prefix:path format: address/content:file.svg
        var markdown = "@@MeshWeaver/UnifiedPath/content:logo.svg";
        var extension = new LayoutAreaMarkdownExtension();
        var document = ParseMarkdown(markdown, extension);

        var layoutArea = document.Descendants<LayoutAreaComponentInfo>().Single();
        layoutArea.Address.Should().Be("MeshWeaver/UnifiedPath");
        layoutArea.Area.Should().Be(LayoutAreaMarkdownParser.ContentAreaName);
        layoutArea.Id.Should().Be("logo.svg");
        layoutArea.IsInline.Should().BeTrue();
    }

    [Fact]
    public void PrefixColonSyntax_Data()
    {
        // New prefix:path format: address/data:collection
        var markdown = "@MeshWeaver/UnifiedPath/data:Posts";
        var extension = new LayoutAreaMarkdownExtension();
        var document = ParseMarkdown(markdown, extension);

        var layoutArea = document.Descendants<LayoutAreaComponentInfo>().Single();
        layoutArea.Address.Should().Be("MeshWeaver/UnifiedPath");
        layoutArea.Area.Should().Be(LayoutAreaMarkdownParser.DataAreaName);
        layoutArea.Id.Should().Be("Posts");
        layoutArea.IsInline.Should().BeFalse();
    }

    [Fact]
    public void PrefixColonSyntax_Schema()
    {
        // New prefix:path format: address/schema:typeName
        var markdown = "@@Systemorph/Marketing/schema:MeshNode";
        var extension = new LayoutAreaMarkdownExtension();
        var document = ParseMarkdown(markdown, extension);

        var layoutArea = document.Descendants<LayoutAreaComponentInfo>().Single();
        layoutArea.Address.Should().Be("Systemorph/Marketing");
        layoutArea.Area.Should().Be(LayoutAreaMarkdownParser.SchemaAreaName);
        layoutArea.Id.Should().Be("MeshNode");
        layoutArea.IsInline.Should().BeTrue();
    }

    [Fact]
    public void PrefixColonSyntax_DataSelfReference()
    {
        // Self reference: address/data: (nothing after colon means self)
        var markdown = "@@MeshWeaver/UnifiedPath/data:";
        var extension = new LayoutAreaMarkdownExtension();
        var document = ParseMarkdown(markdown, extension);

        var layoutArea = document.Descendants<LayoutAreaComponentInfo>().Single();
        layoutArea.Address.Should().Be("MeshWeaver/UnifiedPath");
        layoutArea.Area.Should().Be(LayoutAreaMarkdownParser.DataAreaName);
        layoutArea.Id.Should().BeNull(); // Empty after colon means self-reference
        layoutArea.IsInline.Should().BeTrue();
    }

    [Fact]
    public void PrefixColonSyntax_SchemaSelfReference()
    {
        // Self reference: address/schema: (show schema of current node)
        var markdown = "@@Systemorph/Marketing/schema:";
        var extension = new LayoutAreaMarkdownExtension();
        var document = ParseMarkdown(markdown, extension);

        var layoutArea = document.Descendants<LayoutAreaComponentInfo>().Single();
        layoutArea.Address.Should().Be("Systemorph/Marketing");
        layoutArea.Area.Should().Be(LayoutAreaMarkdownParser.SchemaAreaName);
        layoutArea.Id.Should().BeNull();
        layoutArea.IsInline.Should().BeTrue();
    }

    [Fact]
    public void PrefixColonSyntax_ContentSelfReference()
    {
        // Self reference: address/content: (show content of current node)
        var markdown = "@@MeshWeaver/UnifiedPath/content:";
        var extension = new LayoutAreaMarkdownExtension();
        var document = ParseMarkdown(markdown, extension);

        var layoutArea = document.Descendants<LayoutAreaComponentInfo>().Single();
        layoutArea.Address.Should().Be("MeshWeaver/UnifiedPath");
        layoutArea.Area.Should().Be(LayoutAreaMarkdownParser.ContentAreaName);
        layoutArea.Id.Should().BeNull();
        layoutArea.IsInline.Should().BeTrue();
    }

    [Fact]
    public void PrefixColonSyntax_Model()
    {
        // Model prefix: address/model:TypeName (show data model for type)
        var markdown = "@@Systemorph/Marketing/model:MeshNode";
        var extension = new LayoutAreaMarkdownExtension();
        var document = ParseMarkdown(markdown, extension);

        var layoutArea = document.Descendants<LayoutAreaComponentInfo>().Single();
        layoutArea.Address.Should().Be("Systemorph/Marketing");
        layoutArea.Area.Should().Be(LayoutAreaMarkdownParser.ModelAreaName);
        layoutArea.Id.Should().Be("MeshNode");
        layoutArea.IsInline.Should().BeTrue();
    }

    [Fact]
    public void PrefixColonSyntax_ModelSelfReference()
    {
        // Model self reference: address/model: (show data model for current node's type)
        var markdown = "@@Systemorph/Marketing/model:";
        var extension = new LayoutAreaMarkdownExtension();
        var document = ParseMarkdown(markdown, extension);

        var layoutArea = document.Descendants<LayoutAreaComponentInfo>().Single();
        layoutArea.Address.Should().Be("Systemorph/Marketing");
        layoutArea.Area.Should().Be(LayoutAreaMarkdownParser.ModelAreaName);
        layoutArea.Id.Should().BeNull();
        layoutArea.IsInline.Should().BeTrue();
    }

    [Fact]
    public void PrefixColonSyntax_Area()
    {
        // Area prefix with colon: address/area:AreaName
        var markdown = "@Systemorph/Marketing/area:Dashboard";
        var extension = new LayoutAreaMarkdownExtension();
        var document = ParseMarkdown(markdown, extension);

        var layoutArea = document.Descendants<LayoutAreaComponentInfo>().Single();
        layoutArea.Address.Should().Be("Systemorph/Marketing");
        layoutArea.Area.Should().Be("Dashboard");
        layoutArea.Id.Should().BeNull();
        layoutArea.IsInline.Should().BeFalse();
    }

    [Fact]
    public void PrefixColonSyntax_AreaWithId()
    {
        // Area prefix with id: address/area:AreaName/id
        var markdown = "@Systemorph/Marketing/area:Todo/SalesDeck";
        var extension = new LayoutAreaMarkdownExtension();
        var document = ParseMarkdown(markdown, extension);

        var layoutArea = document.Descendants<LayoutAreaComponentInfo>().Single();
        layoutArea.Address.Should().Be("Systemorph/Marketing");
        layoutArea.Area.Should().Be("Todo");
        layoutArea.Id.Should().Be("SalesDeck");
        layoutArea.IsInline.Should().BeFalse();
    }

    #endregion
}
