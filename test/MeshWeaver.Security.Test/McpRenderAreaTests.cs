using FluentAssertions;
using MeshWeaver.Blazor.AI;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using ModelContextProtocol.Protocol;
using Xunit;

namespace MeshWeaver.Security.Test;

/// <summary>
/// Tests for <see cref="McpMeshPlugin.RenderArea"/> — the MCP-UI entry point that returns
/// an interactive layout-area view as an embedded resource for hosts that render them
/// (Claude.ai web/desktop, ChatGPT Apps) and a plain URL for text-only hosts.
///
/// The method is pure given <c>baseUrl</c>; these tests exercise URL shape, content-block
/// composition, input validation, and encoding — not the underlying layout rendering.
/// </summary>
public class McpRenderAreaTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder).AddMcp();

    private McpMeshPlugin CreatePlugin() => new(Mesh);

    [Fact]
    public void RenderArea_ReturnsThreeContentBlocks()
    {
        var plugin = CreatePlugin();

        var result = plugin.RenderArea("Northwind", "SalesByCategory");

        result.IsError.Should().NotBe(true);
        result.Content.Should().HaveCount(3, "one EmbeddedResourceBlock + one ResourceLinkBlock + one TextContentBlock fallback");
        result.Content![0].Should().BeOfType<EmbeddedResourceBlock>();
        result.Content[1].Should().BeOfType<ResourceLinkBlock>();
        result.Content[2].Should().BeOfType<TextContentBlock>();
    }

    [Fact]
    public void RenderArea_EmbeddedResource_IsHtmlWithIframePointingAtAreaUrl()
    {
        var plugin = CreatePlugin();

        var result = plugin.RenderArea("Northwind", "SalesByCategory");

        var embedded = (EmbeddedResourceBlock)result.Content![0];
        var text = embedded.Resource.Should().BeOfType<TextResourceContents>().Subject;
        text.MimeType.Should().Be("text/html");
        text.Uri.Should().Be("ui://mesh/Northwind/SalesByCategory", "MCP-UI identifies UI resources via ui:// scheme");
        text.Text.Should().Contain("<iframe");
        text.Text.Should().Contain("http://localhost:5000/Northwind/SalesByCategory",
            "the iframe src must point at the real layout-area URL on the Memex portal");
    }

    [Fact]
    public void RenderArea_ResourceLinkBlock_MatchesAreaUrl()
    {
        var plugin = CreatePlugin();

        var result = plugin.RenderArea("Northwind", "SalesByCategory");

        var link = (ResourceLinkBlock)result.Content![1];
        link.Uri.Should().Be("http://localhost:5000/Northwind/SalesByCategory");
        link.MimeType.Should().Be("text/html");
        link.Name.Should().Be("SalesByCategory");
    }

    [Fact]
    public void RenderArea_TextFallback_ContainsOpenInBrowserUrl()
    {
        var plugin = CreatePlugin();

        var result = plugin.RenderArea("Northwind", "SalesByCategory");

        var text = (TextContentBlock)result.Content![2];
        text.Text.Should().Contain("http://localhost:5000/Northwind/SalesByCategory",
            "text-only hosts fall back to the link text");
    }

    [Fact]
    public void RenderArea_StripsLeadingAtSign_FromPath()
    {
        var plugin = CreatePlugin();

        var withAt = plugin.RenderArea("@Northwind", "SalesByCategory");
        var without = plugin.RenderArea("Northwind", "SalesByCategory");

        var withAtLink = (ResourceLinkBlock)withAt.Content![1];
        var withoutLink = (ResourceLinkBlock)without.Content![1];
        withAtLink.Uri.Should().Be(withoutLink.Uri, "leading @ is stripped by ResolvePath — same URL either way");
    }

    [Fact]
    public void RenderArea_WithNestedPath_PreservesSlashes()
    {
        var plugin = CreatePlugin();

        var result = plugin.RenderArea("@Systemorph/FutuRe/EuropeRe/AcmeSubmission2025", "Triangle");

        var link = (ResourceLinkBlock)result.Content![1];
        link.Uri.Should().Be("http://localhost:5000/Systemorph/FutuRe/EuropeRe/AcmeSubmission2025/Triangle");
    }

    [Fact]
    public void RenderArea_EmptyPath_ReturnsError()
    {
        var plugin = CreatePlugin();

        var result = plugin.RenderArea("", "Triangle");

        result.IsError.Should().BeTrue();
        var text = (TextContentBlock)result.Content![0];
        text.Text.Should().Contain("path is required");
    }

    [Fact]
    public void RenderArea_WhitespacePath_ReturnsError()
    {
        var plugin = CreatePlugin();

        var result = plugin.RenderArea("   ", "Triangle");

        result.IsError.Should().BeTrue();
    }

    [Fact]
    public void RenderArea_EmptyAreaName_ReturnsError()
    {
        var plugin = CreatePlugin();

        var result = plugin.RenderArea("Northwind", "");

        result.IsError.Should().BeTrue();
        var text = (TextContentBlock)result.Content![0];
        text.Text.Should().Contain("areaName is required");
    }

    [Fact]
    public void RenderArea_IframeHtml_EscapesAngleBracketsInAreaName()
    {
        // Defense-in-depth: even if an attacker-controlled areaName sneaks through,
        // the iframe HTML must not render raw <script> — HtmlEncode before embedding.
        var plugin = CreatePlugin();

        var result = plugin.RenderArea("Northwind", "<script>alert(1)</script>");

        var embedded = (EmbeddedResourceBlock)result.Content![0];
        var text = (TextResourceContents)embedded.Resource;
        text.Text.Should().NotContain("<script>alert(1)</script>",
            "angle brackets in areaName must be HTML-encoded inside the iframe HTML");
        text.Text.Should().Contain("&lt;script&gt;", "expected HTML-encoded form");
    }

    [Fact]
    public void RenderArea_IframeHtml_IsSelfContainedDocument()
    {
        var plugin = CreatePlugin();

        var result = plugin.RenderArea("Northwind", "Overview");

        var embedded = (EmbeddedResourceBlock)result.Content![0];
        var text = (TextResourceContents)embedded.Resource;
        text.Text.Should().StartWith("<!doctype html>", "MCP-UI hosts iframe the HTML — it must be a full document");
        text.Text.Should().Contain("<html");
        text.Text.Should().Contain("</html>");
    }
}
