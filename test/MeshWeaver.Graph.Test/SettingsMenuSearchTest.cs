using System.Collections.Generic;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The Settings menu search box filters tabs by <see cref="SettingsMenuItemDefinition.Label"/>,
/// <see cref="SettingsMenuItemDefinition.Group"/>, AND <see cref="SettingsMenuItemDefinition.Keywords"/>
/// (the terms describing the fields inside each section), so a user can find a setting by what is
/// INSIDE a section, not only by the section's name. Drives <c>SettingsLayoutArea.FilterMenuItems</c>.
/// </summary>
public class SettingsMenuSearchTest
{
    private static SettingsMenuItemDefinition Item(
        string id, string label, string? group = null, IReadOnlyList<string>? keywords = null)
        => new(id, label, (_, stack, _) => stack, Group: group, Keywords: keywords);

    private static readonly IReadOnlyList<SettingsMenuItemDefinition> Items =
    [
        Item("Metadata", "Metadata", keywords: ["name", "icon", "category"]),
        Item("Access", "Access Control", group: "Security", keywords: ["roles", "permissions"]),
        Item("Appearance", "Appearance", keywords: ["theme", "dark mode"]),
    ];

    [Fact]
    public void EmptyQuery_ReturnsAllItemsUnchanged()
    {
        SettingsLayoutArea.FilterMenuItems(Items, "").Should().HaveCount(3);
        SettingsLayoutArea.FilterMenuItems(Items, "   ").Should().HaveCount(3);
        SettingsLayoutArea.FilterMenuItems(Items, null).Should().HaveCount(3);
    }

    [Fact]
    public void MatchesByLabel_CaseInsensitiveSubstring()
        => SettingsLayoutArea.FilterMenuItems(Items, "appea")
            .Should().ContainSingle().Which.Id.Should().Be("Appearance");

    [Fact]
    public void MatchesByGroup()
        => SettingsLayoutArea.FilterMenuItems(Items, "security")
            .Should().ContainSingle().Which.Id.Should().Be("Access");

    [Fact]
    public void MatchesByKeyword_FindsSectionByItsContent()
    {
        // "dark mode" appears in no label or group — only in Appearance's keywords.
        SettingsLayoutArea.FilterMenuItems(Items, "dark mode")
            .Should().ContainSingle().Which.Id.Should().Be("Appearance");
        // Keyword match is case-insensitive too.
        SettingsLayoutArea.FilterMenuItems(Items, "ROLES")
            .Should().ContainSingle().Which.Id.Should().Be("Access");
    }

    [Fact]
    public void NoMatch_ReturnsEmpty()
        => SettingsLayoutArea.FilterMenuItems(Items, "zzz-no-such-thing").Should().BeEmpty();

    [Fact]
    public void ItemsWithoutKeywords_StillMatchByLabel()
    {
        var noKeywords = new[] { Item("Plain", "Plain Tab") };
        SettingsLayoutArea.FilterMenuItems(noKeywords, "plain").Should().ContainSingle();
        SettingsLayoutArea.FilterMenuItems(noKeywords, "theme").Should().BeEmpty();
    }
}
