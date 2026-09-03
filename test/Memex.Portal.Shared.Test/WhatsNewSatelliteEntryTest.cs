using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Memex.Portal.Shared.Settings;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MeshWeaver.Fixture;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// A release note authored OUTSIDE this repository must reach the one What's New feed (#2539).
///
/// <para>The feed used to be a single <c>path:Doc/WhatsNew scope:children</c> listing, and that
/// path exists only here — so a satellite repo (Plugins, Education, Reinsurance, SocialMedia,
/// Memex) had no route to file an entry from its own PR, and a user-noticeable fix landing there
/// was simply missing from the changelog. The failure is silent and it COMPOUNDS: every extraction
/// moves more user-visible behaviour out of core, so the feed drifts toward being a platform-only
/// changelog while reading as a complete one.</para>
///
/// <para>The contract a satellite writes against is exactly what this test performs: a node
/// carrying <c>nodeType: WhatsNew</c> in its front matter, living anywhere in its own tree.</para>
/// </summary>
public class WhatsNewSatelliteEntryTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    [Fact]
    public async Task An_entry_outside_Doc_reaches_the_feed()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var ns = $"SatelliteRepo{Guid.NewGuid():N}"[..20];

        await meshService.CreateNode(new MeshNode("2026-08-28-a-satellite-fix", ns)
        {
            NodeType = WhatsNewSettingsTab.EntryNodeType,
            Name = "A fix that shipped from a satellite repo",
            State = MeshNodeState.Active,
        }).Should().Within(30.Seconds()).Emit();

        var listed = await Mesh
            .GetQuery("whatsnew:test-listing", WhatsNewSettingsTab.ListingQueries)
            .Where(nodes => nodes.Any(n =>
                string.Equals(n.NodeType, WhatsNewSettingsTab.EntryNodeType, StringComparison.Ordinal)))
            .FirstAsync()
            .Timeout(30.Seconds())
            .Await();

        listed.Should().Contain(
            n => n.Name == "A fix that shipped from a satellite repo",
            "an entry declaring nodeType:WhatsNew must reach the feed from any namespace — "
            + "otherwise a satellite repo can only skip the entry, and the changelog silently "
            + "becomes platform-only as more code leaves core");
    }

    /// <summary>
    /// The platform's own 612 entries live under <c>Doc/WhatsNew</c> and carry no explicit node
    /// type. Their lane must survive: this is a UNION, deliberately, not a migration.
    /// </summary>
    [Fact]
    public void The_platform_lane_is_still_declared()
    {
        WhatsNewSettingsTab.ListingQueries.Should().Contain(
            $"path:{WhatsNewSettingsTab.WhatsNewNamespace} scope:children",
            "the 612 existing entries carry no nodeType — dropping this lane would empty the feed");
        // The type lane is mesh-wide BY DESIGN (a satellite files entries in its own tree), so it
        // DECLARES the fan-out — fan-out is opt-in since Plugins #1231, and an undeclared
        // path-less read is refused at runtime (#3202).
        WhatsNewSettingsTab.ListingQueries.Should().Contain(
            MeshWideQuery.OfType(WhatsNewSettingsTab.EntryNodeType),
            "the satellite lane is what #2539 adds, and it says out loud that it spans every partition");
    }
}
