using System.Linq;
using MeshWeaver.Data.Completion;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh.Completion;
using MeshWeaver.Messaging;
using MeshWeaver.ServiceProvider;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The portal registers <c>AddMeshNavigation</c> on the mesh hub itself
/// (<c>MemexConfiguration</c>), and until the Blazor.Graph view pack's conversion ships that
/// pack's mesh-hub configuration registers it too. Both fold into the same hub, so the
/// registration must dedupe: a doubled call registers exactly ONE of each autocomplete
/// provider, never two (which would answer every @-completion twice). Read off the hub's
/// registrations, so the test needs no mesh services to activate the providers.
/// </summary>
public class MeshNavigationRegisteredTwiceDedupesTest(ITestOutputHelper output) : HubTestBase(output)
{
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .AddMeshNavigation()
            .AddMeshNavigation();

    /// <summary>Two <c>AddMeshNavigation</c> calls on one hub register each provider once.</summary>
    [HubFact]
    public void AddMeshNavigation_Twice_RegistersEachProviderOnce()
    {
        var registry = MeshServiceProvider.From(GetHost().ServiceProvider)!;

        registry.RegistrationsOf(typeof(IAutocompleteProvider))
            .Count(r => (r.ImplementationType ?? r.Instance?.GetType()) == typeof(MeshNodeAutocompleteProvider))
            .Should().Be(1, "TryAddEnumerable dedupes the mesh-node provider across the two registrations");

        registry.RegistrationsOf(typeof(UnifiedReferenceAutocompleteProvider))
            .Should().HaveCount(1,
                "the manual ServiceType check dedupes the unified-reference provider across the two registrations");
    }
}
