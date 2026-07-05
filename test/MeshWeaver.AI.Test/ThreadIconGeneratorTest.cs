#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Pins the deterministic thread identicon generator: same seed ⇒ identical SVG, different
/// seeds ⇒ distinct SVG, always valid + sanitiser-safe inline markup — and that the thread
/// builders (<see cref="ThreadNodeType.BuildThreadNode"/> /
/// <see cref="ThreadNodeType.BuildThreadWithMessages"/>) stamp that identicon on the node's
/// <c>Icon</c> so the catalog renders a distinct visual per thread.
/// </summary>
public class ThreadIconGeneratorTest
{
    [Fact]
    public void Generate_IsDeterministic_SameSeedSameSvg()
    {
        var a = ThreadIconGenerator.Generate("my-thread-a3f9");
        var b = ThreadIconGenerator.Generate("my-thread-a3f9");
        a.Should().Be(b, "the generator must be a pure function of its seed");
    }

    [Fact]
    public void Generate_DifferentSeeds_ProduceDifferentSvg()
    {
        var a = ThreadIconGenerator.Generate("setting-up-ci-cd-a3f9");
        var b = ThreadIconGenerator.Generate("enterprise-pricing-b7c2");
        a.Should().NotBe(b, "distinct threads must look distinct");
    }

    [Fact]
    public void Generate_ProducesValidInlineSvgMarkup()
    {
        var svg = ThreadIconGenerator.Generate("fix-login-bug-1234");

        svg.Should().StartWith("<svg", "the node card renders values that start with <svg inline");
        svg.Should().EndWith("</svg>");
        svg.Should().Contain("viewBox=\"0 0 80 80\"");
        svg.Should().Contain("<rect", "the identicon is composed of rect cells over a rounded background");
        // Sanitiser-safe: no active content, no event handlers.
        svg.Should().NotContain("<script");
        svg.Should().NotContain("foreignObject");
        svg.Should().NotContain(" on", "no on* event-handler attributes");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Generate_EmptyOrNullSeed_StillValid(string? seed)
    {
        var svg = ThreadIconGenerator.Generate(seed);
        svg.Should().StartWith("<svg").And.EndWith("</svg>");
        // Null and empty hash to the same bytes → identical, constant icon.
        svg.Should().Be(ThreadIconGenerator.Generate(""));
    }

    [Fact]
    public void BuildThreadNode_StampsDeterministicIdenticonOnIcon()
    {
        var node = ThreadNodeType.BuildThreadNode("User/rbuergi", "How do I set up CI/CD?", "rbuergi");

        node.Icon.Should().StartWith("<svg", "per-instance threads get an inline SVG identicon, not the type glyph");
        node.Icon.Should().Be(ThreadIconGenerator.Generate(node.Id),
            "the icon is seeded by the thread's own (stable) speaking id");
    }

    [Fact]
    public void BuildThreadWithMessages_StampsDeterministicIdenticonOnIcon()
    {
        var (node, _, _) = ThreadNodeType.BuildThreadWithMessages("User/rbuergi", "Fix the login bug", "rbuergi");

        node.Icon.Should().StartWith("<svg");
        node.Icon.Should().Be(ThreadIconGenerator.Generate(node.Id));
    }
}
