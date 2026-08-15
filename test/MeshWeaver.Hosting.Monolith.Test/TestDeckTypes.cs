using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// TEST-LOCAL Slide/Deck node-type registrations. The production types are the Publish pack's
/// dynamic <c>Publish/Slide</c>/<c>Publish/Deck</c> (in-mesh source, uninstallable in a hermetic
/// platform test) since the core built-ins were retired (#1589). The export/print pipeline under
/// test is node-type-agnostic — it works off <see cref="SlideContent"/>/<see cref="DeckContent"/>
/// and the suffix-aware <c>SlideNodeType.Matches</c>/<c>DeckNodeType.Matches</c>, which both
/// accept the bare names these registrations use — so a data-source-only registration is exactly
/// the surface the fixtures need.
/// </summary>
internal static class TestDeckTypes
{
    public static MeshNode Slide() => new(SlideNodeType.NodeType)
    {
        Name = "Slide (test-local)",
        HubConfiguration = config => config
            .AddMeshDataSource(s => s.WithContentType<SlideContent>()),
    };

    public static MeshNode Deck() => new(DeckNodeType.NodeType)
    {
        Name = "Deck (test-local)",
        HubConfiguration = config => config
            .AddMeshDataSource(s => s.WithContentType<DeckContent>()),
    };
}
