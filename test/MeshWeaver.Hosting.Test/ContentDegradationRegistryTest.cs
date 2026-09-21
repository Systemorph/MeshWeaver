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

        registry.Record("Crm/Client", "PartnerRe", "MeshNodeStreamCache.GetStream");
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
        registry.Record("Crm/Client", "PartnerRe", "seam");
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
}
