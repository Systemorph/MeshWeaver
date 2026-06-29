using MeshWeaver.Graph;
using MeshWeaver.Blazor.Portal;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Guards the Space default-welcome markdown against regressing into the
/// previous "creepy pseudo-HTML" form. The default is plain markdown — inline
/// styling, gradients, emoji glyph entities, and card-style &lt;div&gt; grids
/// belong in per-Space overrides, not in the default every new Space ships with.
/// </summary>
public class SpaceWelcomeTest
{
    [Fact]
    public void WelcomeMarkdown_IsPlainMarkdown_NoInlineStyledDivs()
    {
        var md = SpaceNodeType.WelcomeMarkdown;

        md.Should().NotContain("style=",
            "default welcome must be plain markdown — no inline styles");
        md.Should().NotContain("<div",
            "no <div> wrappers — use headings/lists/paragraphs instead");
        md.Should().NotContain("linear-gradient",
            "no gradient banners — that's what per-space overrides are for");
        md.Should().NotContain("&rarr;", "no HTML entity arrows — use '→' or markdown");
        md.Should().NotContain("&mdash;");
        md.Should().NotContain("&rsquo;");
    }

    [Fact]
    public void WelcomeMarkdown_InvitesUserToPersonalizeAndChat()
    {
        var md = SpaceNodeType.WelcomeMarkdown.ToLowerInvariant();

        md.Should().Contain("welcome", "default must actually say welcome");
        md.Should().Contain("chat", "default must invite the user to chat");
    }

    [Fact]
    public void Space_HasBodyProperty_ForUserEditableMarkdown()
    {
        var property = typeof(Space).GetProperty(nameof(Space.Body));
        property.Should().NotBeNull("Space.Body is the user-editable markdown field");
        property!.PropertyType.Should().Be(typeof(string),
            "Body is plain markdown text; rich content lives in index.md overrides");
    }
}
