using System.Collections.Generic;
using MeshWeaver.Fixture;
using MeshWeaver.Layout.Client;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// Pins the view-pack registration contract: a view map registered AFTER the host's default
/// mapping still wins over the last-resort fallback, because the fallback lives in its own slot
/// (<see cref="LayoutClientConfiguration.WithFallbackView"/>) consulted only once every map has
/// declined. Before this contract existed, the core default mapping ended in a terminal
/// fallback arm, so registration order was load-bearing: a pack registered after
/// <c>AddBlazor()</c> was silently dead and its controls rendered as escaped HTML.
/// </summary>
public class ViewPackFallbackOrderingTest(ITestOutputHelper output) : HubTestBase(output)
{
    private sealed record PackControl;

    [Fact]
    public void LateRegisteredPackMap_WinsOverFallback_AndFallbackCatchesTheRest()
    {
        var config = new LayoutClientConfiguration(GetClient())
            // The host's default mapping: declines what it does not know (returns null).
            .WithView((_, _, _) => null)
            // The host's last-resort fallback — its own slot, never part of the ordered maps.
            .WithFallbackView((_, _, _) =>
                new ViewDescriptor(typeof(string), new Dictionary<string, object?>()))
            // A view pack registered AFTER the default mapping — the late-registration case.
            .WithView((i, _, _) => i is PackControl
                ? new ViewDescriptor(typeof(int), new Dictionary<string, object?>())
                : null);

        // A late-registered pack map must be consulted before the fallback.
        var packDescriptor = config.GetViewDescriptor(new PackControl(), null, "area");
        Assert.NotNull(packDescriptor);
        Assert.Equal(typeof(int), packDescriptor!.Type);

        // The fallback slot catches instances no map claims.
        var fallbackDescriptor = config.GetViewDescriptor(new object(), null, "area");
        Assert.NotNull(fallbackDescriptor);
        Assert.Equal(typeof(string), fallbackDescriptor!.Type);
    }
}
