using FluentAssertions;
using Markdig;
using Xunit;

namespace MeshWeaver.Markdown.Test;

/// <summary>
/// Tests for <see cref="PathUtils"/>: relative path resolution and satellite partition stripping.
/// Satellite partitions (segments starting with '_', e.g., _Thread, _Comment) are stripped
/// so that links in satellite content resolve relative to the main entity.
/// </summary>
public class PathUtilsTest
{
    // ---------- Satellite partition stripping (tested via ResolveRelativePath) ----------

    [Theory]
    [InlineData("X", "A/B/_Thread/slug/msg", "A/B/X")]
    [InlineData("X", "A/B/_Comment/abc123", "A/B/X")]
    [InlineData("X", "A/B/_Tracking/change1", "A/B/X")]
    [InlineData("X", "A/_Thread/slug", "A/X")]
    [InlineData("X", "_Thread/slug/msg", "X")]
    [InlineData("X", "A/B/C", "A/B/C/X")]  // no satellite — unchanged
    public void ResolveRelativePath_StripsSatellitePartitions(string path, string basePath, string expected)
        => PathUtils.ResolveRelativePath(path, basePath).Should().Be(expected);

    // ---------- ResolveRelativePath with satellite partitions ----------

    [Fact]
    public void ResolveRelativePath_ThreadContext_ResolvesRelativeToMainEntity()
    {
        // A relative link "FinalReport" in a thread message should resolve
        // to the main entity's namespace, not the thread path.
        var result = PathUtils.ResolveRelativePath(
            "FinalReport",
            "PartnerRe/AIConsulting/_Thread/we-will-now-work-on-97af/d75effc1");

        result.Should().Be("PartnerRe/AIConsulting/FinalReport");
    }

    [Fact]
    public void ResolveRelativePath_CommentContext_ResolvesRelativeToMainEntity()
    {
        var result = PathUtils.ResolveRelativePath(
            "Appendix",
            "Doc/Architecture/_Comment/abc123");

        result.Should().Be("Doc/Architecture/Appendix");
    }

    [Fact]
    public void ResolveRelativePath_ParentTraversal_InThreadContext()
    {
        // "../OtherProject" from PartnerRe/AIConsulting/_Thread/... should go up from AIConsulting
        var result = PathUtils.ResolveRelativePath(
            "../OtherProject",
            "PartnerRe/AIConsulting/_Thread/slug/msgId");

        result.Should().Be("PartnerRe/OtherProject");
    }

    [Fact]
    public void ResolveRelativePath_DotSlash_InThreadContext()
    {
        var result = PathUtils.ResolveRelativePath(
            "./FinalReport",
            "PartnerRe/AIConsulting/_Thread/slug/msgId");

        result.Should().Be("PartnerRe/AIConsulting/FinalReport");
    }

    // ---------- ResolveRelativePath (non-satellite, existing behavior) ----------

    [Fact]
    public void ResolveRelativePath_NormalContext_ResolvesRelatively()
    {
        var result = PathUtils.ResolveRelativePath(
            "DataModeling",
            "Doc/Architecture");

        result.Should().Be("Doc/Architecture/DataModeling");
    }

    [Fact]
    public void ResolveRelativePath_ParentTraversal_NormalContext()
    {
        var result = PathUtils.ResolveRelativePath(
            "../DataMesh/NodeTypes",
            "Doc/Architecture/BusinessRules");

        result.Should().Be("Doc/Architecture/DataMesh/NodeTypes");
    }

    [Theory]
    [InlineData("/absolute/path")]
    [InlineData("https://example.com")]
    [InlineData("#anchor")]
    [InlineData("mailto:test@example.com")]
    public void ResolveRelativePath_SkipsNonRelativePaths(string path)
        => PathUtils.ResolveRelativePath(path, "Doc/Architecture").Should().Be(path);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ResolveRelativePath_NullOrEmptyBase_ReturnsPathUnchanged(string? basePath)
        => PathUtils.ResolveRelativePath("FinalReport", basePath).Should().Be("FinalReport");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ResolveRelativePath_NullOrEmptyPath_ReturnsAsIs(string? path)
        => PathUtils.ResolveRelativePath(path!, "Doc/Architecture").Should().Be(path!);

    // ---------- End-to-end: LinkUrlCleanupExtension in thread context ----------

    [Fact]
    public void LinkCleanup_ThreadContext_RelativeLinkResolvesToMainEntity()
    {
        // Simulate rendering markdown in a thread message bubble.
        // The currentNodePath is the thread message's full path.
        var threadMsgPath = "PartnerRe/AIConsulting/_Thread/thread-slug/msgId";
        var pipeline = new MarkdownPipelineBuilder()
            .Use(new LinkUrlCleanupExtension(threadMsgPath))
            .Build();

        var html = Markdig.Markdown.ToHtml("[Final Report](FinalReport)", pipeline);

        html.Should().Contain("href=\"/PartnerRe/AIConsulting/FinalReport\"",
            "relative link in thread should resolve to main entity path");
    }

    [Fact]
    public void LinkCleanup_ThreadContext_AbsoluteLinkUnchanged()
    {
        var threadMsgPath = "PartnerRe/AIConsulting/_Thread/slug/msgId";
        var pipeline = new MarkdownPipelineBuilder()
            .Use(new LinkUrlCleanupExtension(threadMsgPath))
            .Build();

        var html = Markdig.Markdown.ToHtml("[Report](/PartnerRe/AIConsulting/FinalReport)", pipeline);

        html.Should().Contain("href=\"/PartnerRe/AIConsulting/FinalReport\"",
            "absolute links should remain unchanged");
    }

    [Fact]
    public void LinkCleanup_ThreadContext_AtPrefixedLinkResolvesToMainEntity()
    {
        var threadMsgPath = "PartnerRe/AIConsulting/_Thread/slug/msgId";
        var pipeline = new MarkdownPipelineBuilder()
            .Use(new LinkUrlCleanupExtension(threadMsgPath))
            .Build();

        // @SiblingDoc — after stripping '@', should resolve relative to main entity
        var html = Markdig.Markdown.ToHtml("[doc](@SiblingDoc)", pipeline);

        html.Should().Contain("href=\"/PartnerRe/AIConsulting/SiblingDoc\"",
            "@-prefixed relative link in thread should resolve to main entity path");
    }

    [Fact]
    public void LinkCleanup_ThreadContext_ExternalLinkUnchanged()
    {
        var threadMsgPath = "Org/Project/_Thread/slug/msgId";
        var pipeline = new MarkdownPipelineBuilder()
            .Use(new LinkUrlCleanupExtension(threadMsgPath))
            .Build();

        var html = Markdig.Markdown.ToHtml("[Google](https://google.com)", pipeline);

        html.Should().Contain("href=\"https://google.com\"");
    }

    [Fact]
    public void LinkCleanup_NonThreadContext_RelativeLinkResolvesNormally()
    {
        // Normal (non-satellite) context should still work as before.
        var pipeline = new MarkdownPipelineBuilder()
            .Use(new LinkUrlCleanupExtension("Doc/Architecture"))
            .Build();

        var html = Markdig.Markdown.ToHtml("[Data](DataModeling)", pipeline);

        html.Should().Contain("href=\"/Doc/Architecture/DataModeling\"");
    }
}
