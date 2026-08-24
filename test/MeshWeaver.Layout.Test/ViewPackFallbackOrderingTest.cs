using System.Collections.Generic;
using MeshWeaver.Blazor;
using MeshWeaver.Fixture;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using Microsoft.Extensions.DependencyInjection;
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

/// <summary>
/// Pins the SKIN half of the same contract, against the REAL base registry
/// (<c>AddBlazor()</c>'s default mapping): a skin the registry does not know is a DECLINE that
/// later-registered pack maps get to claim, and only when nobody claims it does the fallback
/// slot render. Before the decline existed, the registry's skin switch THREW on its terminal arm
/// and the surrounding catch converted the throw into a NON-NULL error-card descriptor —
/// first-match-wins stopped dead, and a pack owning the skin (the EntityViews extraction's
/// EditorSkin/EditFormSkin/PropertySkin) could never render it. Behaviour note, deliberate: a
/// skin NO pack owns now renders as the escaped-HTML fallback instead of a loud error card —
/// the same last-resort an unknown CONTROL has always had.
/// </summary>
public class UnknownSkinDeclineTest(ITestOutputHelper output) : HubTestBase(output)
{
    private sealed record ProbeSkin : Skin<ProbeSkin>;

    /// <summary>The view type the fake pack map returns — only its identity matters here.</summary>
    private sealed class PackSkinView;

    private static UiControl SkinnedControl() => Controls.Html("<p>probe</p>").AddSkin(new ProbeSkin());

    [Fact]
    public void UnknownSkin_WithLaterPackMapRegistered_ThePackMapWins()
    {
        // The pack map is registered AFTER AddBlazor() — the late-registration case the old
        // throwing terminal arm structurally killed.
        var client = GetClient(c => c
            .AddBlazor()
            .AddViews(layout => layout.WithView((instance, _, _) =>
            {
                if (instance is not UiControl control)
                    return null;
                control.PopSkin(out var skin);
                return skin is ProbeSkin
                    ? new ViewDescriptor(typeof(PackSkinView), new Dictionary<string, object?>())
                    : null;
            })));
        var layoutClient = client.ServiceProvider.GetRequiredService<ILayoutClient>();

        var descriptor = layoutClient.GetViewDescriptor(SkinnedControl(), null, "area");

        Assert.NotNull(descriptor);
        Assert.Equal(typeof(PackSkinView), descriptor!.Type);
    }

    [Fact]
    public void UnknownSkin_WithNoPackMap_RendersThroughTheFallbackSlot()
    {
        var client = GetClient(c => c.AddBlazor());
        var layoutClient = client.ServiceProvider.GetRequiredService<ILayoutClient>();

        var descriptor = layoutClient.GetViewDescriptor(SkinnedControl(), null, "area");

        // The escaped-HTML fallback slot answers — NOT an error card raised from inside the
        // default mapping, and not null: the skin is unknown everywhere, so the last resort runs.
        Assert.NotNull(descriptor);
        Assert.Equal(typeof(MeshWeaver.Blazor.Components.HtmlView), descriptor!.Type);

        // The error card is ALSO an HtmlView, so the type alone cannot tell the two apart —
        // inspect the payload: the fallback carries the control's escaped ToString, the old
        // throwing path carried a "Rendering error:" card. This is what makes the test able to
        // fail if the terminal arm ever throws again.
        var payload = Assert.IsType<HtmlControl>(descriptor.Parameters[LayoutClientConfiguration.ViewModel]);
        var html = payload.Data?.ToString() ?? string.Empty;
        Assert.DoesNotContain("Rendering error", html);
        Assert.Contains("HtmlControl", html);
    }
}
