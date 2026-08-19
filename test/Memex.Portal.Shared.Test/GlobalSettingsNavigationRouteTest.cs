using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Memex.Portal.Shared.Settings;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Markdown;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// Every in-app link to the global settings page must land on a path the mesh actually registers.
///
/// <para>It did not: the settings node is registered at <c>_Setting</c>
/// (<see cref="GlobalSettingsNodeType.SettingsPath"/>) while four call sites navigated to the plural
/// lowercase form (<c>_settings</c>), so the About page and What's New answered <i>"Page not found:
/// does not match any registered address pattern"</i> from the profile menu and the build chip
/// (#1817). Nothing failed — no exception, no log, no test — because the two halves lived in
/// different projects and nothing had ever compared them.</para>
///
/// <para>So this test compares them, for the WHOLE family rather than the one string that was
/// reported: it takes each settings tab the portal actually seeds
/// (<see cref="PlatformSettingsTabAreas.Seeds"/> — About, What's New, Privacy, Invitations, Inbox,
/// Updates, Published, Token Usage) plus the compiled API-tokens tab, builds that tab's link exactly
/// as the portal does, and resolves it through the real <see cref="MeshWeaver.Mesh.Services.IPathResolver"/>
/// on a live mesh. A new tab is covered the moment it is seeded, and a future rename of the settings
/// node breaks here instead of in the browser.</para>
///
/// <para>The resolution is asserted the way navigation consumes it — prefix = the settings node,
/// then <c>ParseAreaAndId</c> on the remainder — because a link can resolve to a node and still
/// address the wrong area, which renders a blank pane rather than a 404.</para>
/// </summary>
public class GlobalSettingsNavigationRouteTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>
    /// Every tab a settings link can point at. Read from the seeds themselves (not a copy) so the
    /// list cannot go stale, plus <see cref="ApiTokensSettingsTab"/>, which still registers through
    /// the compiled provider lane.
    /// </summary>
    private static IEnumerable<string> TabIds =>
        PlatformSettingsTabAreas.Seeds.Select(s => s.Id).Append(ApiTokensSettingsTab.TabId);

    [Fact]
    public async Task EverySettingsTabLink_ResolvesToTheGlobalSettingsNodeAndItsArea()
    {
        var offenders = new List<string>();

        foreach (var tabId in TabIds)
        {
            var href = GlobalSettingsNodeType.TabHref(tabId);
            var resolution = await PathResolver.ResolveNavigationPath(href).Should().Emit();

            if (resolution is null)
            {
                offenders.Add($"{tabId,-14} → {href} does not match any registered address pattern");
                continue;
            }

            var (area, id) = LayoutAreaMarkdownParser.ParseAreaAndId(resolution.Remainder);
            if (resolution.Prefix.Trim('/') != GlobalSettingsNodeType.SettingsPath
                || area != GlobalSettingsLayoutArea.GlobalSettingsArea
                || id != tabId)
            {
                offenders.Add(
                    $"{tabId,-14} → {href} resolved to node '{resolution.Prefix}' area '{area}' id '{id}'");
            }
        }

        Assert.True(offenders.Count == 0,
            "A settings tab link does not address the registered settings node + area. The node is "
            + $"'{GlobalSettingsNodeType.SettingsPath}' and the area is "
            + $"'{GlobalSettingsLayoutArea.GlobalSettingsArea}' — build links with "
            + "GlobalSettingsNodeType.TabHref so they cannot drift from the registration (#1817):\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>The bare Settings link (the profile menu's fallback for a user with no user node).</summary>
    [Fact]
    public async Task SettingsHref_ResolvesToTheGlobalSettingsNodeItself()
    {
        var resolution = await PathResolver
            .ResolveNavigationPath(GlobalSettingsNodeType.SettingsHref).Should().Emit();

        Assert.NotNull(resolution);
        Assert.Equal(GlobalSettingsNodeType.SettingsPath, resolution!.Prefix.Trim('/'));
        Assert.True(string.IsNullOrEmpty(resolution.Remainder),
            $"'{GlobalSettingsNodeType.SettingsHref}' must resolve to the node exactly, "
            + $"but left remainder '{resolution.Remainder}'.");
    }

    /// <summary>
    /// The negative control, and the reason this bug was invisible: the retired plural/lowercase
    /// spelling resolves to NOTHING. It is not an alias, and it must never become one — lowercase
    /// <c>_settings</c> is a reserved satellite/schema segment in the cross-schema Postgres/Snowflake
    /// routing, so accepting it here would collide with an unrelated meaning. Without this case the
    /// test above could pass on a resolver that happily matched anything.
    /// </summary>
    [Fact]
    public async Task TheRetiredPluralSpelling_ResolvesToNothing()
    {
        var retired = "/_" + "settings/GlobalSettings/" + AboutSettingsTab.TabId;

        var resolution = await PathResolver.ResolveNavigationPath(retired).Should().Emit();

        Assert.True(resolution is null,
            $"'{retired}' resolved to '{resolution?.Prefix}' — the plural/lowercase spelling is a "
            + "reserved schema segment, not a second name for the settings node.");
    }
}
