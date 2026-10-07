using System;
using System.Linq;
using MeshWeaver.Hosting;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// What this replica could not TYPE, kept so <c>/health</c> can name it (2026-09-08): both memex
/// replicas served an empty client page for hours while <c>/health</c> answered a bare
/// <c>Degraded</c>. The registry counts per node type; the sentence names them.
/// </summary>
public class ContentDegradationRegistryTest
{
    [Fact]
    public void RecordsPerNodeType_AndTheSentenceNamesThem()
    {
        var registry = new ContentDegradationRegistry();
        registry.IsEmpty.Should().BeTrue();
        ContentDegradationRegistry.Describe(registry.Snapshot()).Should().Contain("every node content read on this replica typed");

        registry.Record("Crm/Client", "Globex", "MeshNodeStreamCache.GetStream");
        registry.Record("Crm/Client", "AcmeRe", "MeshNodeStreamCache.GetStream");
        registry.Record("Store/Install", "rbuergi/_Install/Crm", "MeshNodeStreamCache.GetQuery");

        var snapshot = registry.Snapshot();
        snapshot.Count.Should().Be(2, "one entry per node type, counts inside");
        var client = snapshot.Single(d => d.NodeType == "Crm/Client");
        client.Count.Should().Be(2);
        client.LastPath.Should().Be("AcmeRe");

        var sentence = ContentDegradationRegistry.Describe(snapshot);
        sentence.Should().Contain("2 node type(s)").And.Contain("Crm/Client ×2").And.Contain("Store/Install ×1")
            .And.Contain("not loaded here", "the sentence says WHY, not only what");
        // 🚨 The sentence used to name only two causes — a declined bundle and a missing assembly —
        // and BOTH were falsified on two live portals while the third went unnamed: a dynamic
        // NodeType this replica ADOPTED is never activated by the bake, so its content type is
        // never registered here (Plugins#2178, #2180). An instrument that asserts two causes as
        // fact sends its reader past the real one, so the commonest cause is named FIRST and the
        // instrument that discriminates is named with it.
        sentence.Should().Contain("ADOPTED rather than compiled",
            "the commonest cause is the one the sentence used not to mention");
        sentence.Should().Contain("bake-report DECIDES between them",
            "naming three causes without naming what tells them apart is a longer way to mislead");
    }

    [Fact]
    public void ACleanReadOfTheType_ClearsIt()
    {
        var registry = new ContentDegradationRegistry();
        registry.Record("Crm/Client", "Globex", "seam");
        registry.Clear("Crm/Client");
        registry.IsEmpty.Should().BeTrue("a degradation a module load cured stops being reported");
        registry.Clear(null);
        registry.Clear("never-recorded");
    }

    [Fact]
    public void ANullNodeType_IsStillCounted()
    {
        var registry = new ContentDegradationRegistry();
        registry.Record(null, "x", "seam");
        registry.Snapshot().Single().NodeType.Should().Be("(no node type)");
    }

    /// <summary>
    /// Plugins#2812: the sentence printed <c>Store/Tier ×377</c> with no time, so a count run up in
    /// the two minutes after boot read exactly like reads failing now. Each entry now carries its
    /// window, and a boot-window entry is recognisable from ONE probe.
    /// </summary>
    [Fact]
    public void TheSentence_CarriesEachEntrysWindow_SoABootWindowCountReadsAsHistory()
    {
        var boot = new DateTimeOffset(2026, 9, 21, 7, 45, 14, TimeSpan.Zero);
        var bootWindow = new ContentDegradation(
                "Store/Tier", "MeshNodeStreamCache.GetStream", 377, "Admin/Tiers/free", boot.AddSeconds(110))
            { FirstAt = boot.AddSeconds(2) };
        var live = new ContentDegradation(
                "Hosting/InstanceAction", "MeshNodeStreamCache.GetStream", 5, "Ops/Actions/a", boot.AddHours(3))
            { FirstAt = boot.AddHours(2) };

        var sentence = ContentDegradationRegistry.Describe([live, bootWindow]);

        sentence.Should().Contain(
            "Store/Tier ×377 between 2026-09-21T07:45:16Z and 2026-09-21T07:47:04Z (last Admin/Tiers/free)",
            "a window that closed two minutes after boot must be visible as such in one probe");
        sentence.Should().Contain(
            "Hosting/InstanceAction ×5 between 2026-09-21T09:45:14Z and 2026-09-21T10:45:14Z (last Ops/Actions/a)");
        sentence.Should().Contain("its ×count is cumulative",
            "the sentence must say which half is current and which is history");
    }

    [Fact]
    public void AnEntryBuiltWithoutAFirstInstant_FallsBackToItsLastOne()
    {
        var at = new DateTimeOffset(2026, 9, 21, 7, 47, 4, TimeSpan.Zero);
        var legacy = new ContentDegradation("Crm/Client", "seam", 3, "Globex", at);
        legacy.WindowStart.Should().Be(at);
        ContentDegradationRegistry.Describe([legacy])
            .Should().Contain("Crm/Client ×3 between 2026-09-21T07:47:04Z and 2026-09-21T07:47:04Z");
    }

    [Fact]
    public void RecordKeepsTheFirstInstant_AndAClearReopensTheWindow()
    {
        var registry = new ContentDegradationRegistry();
        registry.Record("Crm/Client", "Globex", "seam");
        var first = registry.Snapshot().Single();
        first.FirstAt.Should().Be(first.LastAt, "the first read opens the window");

        registry.Record("Crm/Client", "AcmeRe", "seam");
        var second = registry.Snapshot().Single();
        second.FirstAt.Should().Be(first.FirstAt, "later reads move LastAt, never the window's start");
        second.LastAt.Should().BeOnOrAfter(first.LastAt);

        registry.Clear("Crm/Client");
        registry.Record("Crm/Client", "Initech", "seam");
        var reopened = registry.Snapshot().Single();
        reopened.Count.Should().Be(1, "a cleared entry's count starts again");
        reopened.FirstAt.Should().Be(reopened.LastAt, "and so does its window");
    }
}
