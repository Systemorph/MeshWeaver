using System.Linq;
using Autofac;
using Autofac.Core;
using MeshWeaver.Data.Completion;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh.Completion;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The portal registers <c>AddMeshNavigation</c> on the mesh hub itself
/// (<c>MemexConfiguration</c>), and until the Blazor.Graph view pack's conversion ships that
/// pack's mesh-hub configuration registers it too. Both fold into the same hub, so the
/// registration must dedupe: a doubled call registers exactly ONE of each autocomplete
/// provider, never two (which would answer every @-completion twice). Read off the hub's own
/// component registry, so the test needs no mesh services to activate the providers.
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
        var registry = GetHost().ServiceProvider.GetRequiredService<ILifetimeScope>().ComponentRegistry;

        registry.RegistrationsFor(new TypedService(typeof(IAutocompleteProvider)))
            .Count(r => r.Activator.LimitType == typeof(MeshNodeAutocompleteProvider))
            .Should().Be(1, "TryAddEnumerable dedupes the mesh-node provider across the two registrations");

        registry.RegistrationsFor(new TypedService(typeof(UnifiedReferenceAutocompleteProvider)))
            .Should().HaveCount(1,
                "the manual ServiceType check dedupes the unified-reference provider across the two registrations");
    }
}
