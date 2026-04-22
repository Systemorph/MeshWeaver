using FluentAssertions;
using Memex.Portal.Shared;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Guards the Organization default-welcome markdown against regressing into the
/// previous "creepy pseudo-HTML" form the user asked us to get rid of. The point
/// of the default is to be plain markdown — inline styling, gradients, emoji glyph
/// entities, and card-style &lt;div&gt; grids belong in per-organization overrides,
/// not in the default every new organization ships with.
/// </summary>
public class OrganizationWelcomeTest
{
    [Fact]
    public void WelcomeMarkdown_IsPlainMarkdown_NoInlineStyledDivs()
    {
        var md = OrganizationNodeType.WelcomeMarkdown;

        md.Should().NotContain("style=",
            "default welcome must be plain markdown — no inline styles");
        md.Should().NotContain("<div",
            "no <div> wrappers — use headings/lists/paragraphs instead");
        md.Should().NotContain("linear-gradient",
            "no gradient banners — that's what per-org overrides are for");
        md.Should().NotContain("&rarr;", "no HTML entity arrows — use '→' or markdown");
        md.Should().NotContain("&mdash;");
        md.Should().NotContain("&rsquo;");
    }

    [Fact]
    public void WelcomeMarkdown_InvitesUserToPersonalizeAndChat()
    {
        var md = OrganizationNodeType.WelcomeMarkdown.ToLowerInvariant();

        md.Should().Contain("welcome", "default must actually say welcome");
        md.Should().Contain("chat", "default must invite the user to chat");
    }

    [Fact]
    public void Organization_HasBodyProperty_ForUserEditableMarkdown()
    {
        // The user asked for a markdown field on the Organization record so
        // operators can write rich welcome content without editing HTML.
        var property = typeof(Organization).GetProperty(nameof(Organization.Body));
        property.Should().NotBeNull("Organization.Body is the user-editable markdown field");
        property!.PropertyType.Should().Be(typeof(string),
            "Body is plain markdown text; rich content lives in index.md overrides");
    }
}
