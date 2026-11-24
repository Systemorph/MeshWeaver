using System.Collections.Immutable;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Layout.Chart;
using MeshWeaver.Layout.Client;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// The generic tier of <see cref="LayoutClientConfiguration"/>: a map that claims a control by its
/// SHAPE (<see cref="LayoutClientConfiguration.WithGenericView"/>) is consulted only after every
/// specific map declined — so a view pack's registration for one concrete control wins over the base
/// pack's shape match in whichever order the two were registered.
///
/// <para><b>Why.</b> <see cref="ChartControl"/> is an <see cref="IContainerControl"/> with no
/// children. The base view pack's generic container arm sat in the ordinary first-match-wins list,
/// registered at boot (the pack is image-bound), while the Radzen pack — loaded from the module store —
/// is appended after it. Every chart therefore went to the container view, which drew its empty child
/// list: nothing on screen, and no fallback warning, because a map HAD accepted.</para>
/// </summary>
public class ViewDispatchGenericTierTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    private sealed class ContainerShapeView;

    private sealed class ChartView;

    private sealed class FallbackView;

    private static ViewDescriptor Descriptor<TView>(object instance) =>
        new(typeof(TView), ImmutableDictionary<string, object?>.Empty.Add("ViewModel", instance));

    /// <summary>The base pack's shape match: any container.</summary>
    private static ViewDescriptor? AnyContainer(object instance, ISynchronizationStream<JsonElement>? stream, string area)
        => instance is IContainerControl ? Descriptor<ContainerShapeView>(instance) : null;

    /// <summary>A view pack's specific registration: charts only.</summary>
    private static ViewDescriptor? ChartsOnly(object instance, ISynchronizationStream<JsonElement>? stream, string area)
        => instance is ChartControl ? Descriptor<ChartView>(instance) : null;

    /// <summary>The shape match registered FIRST (the base pack at boot) and the chart pack AFTER it
    /// (a store-loaded module): the chart still reaches the chart view.</summary>
    [Fact]
    public void ASpecificMapRegisteredAfterAShapeMatch_StillWins()
    {
        var configuration = new LayoutClientConfiguration(GetClient())
            .WithGenericView(AnyContainer, "Base:AnyContainer")
            .WithView(ChartsOnly, "Pack:ChartsOnly");

        configuration.GetViewDescriptor(new ChartControl(), null, "Chart")!.Type
            .Should().Be(typeof(ChartView), "a specific registration must win over a shape, whatever the order");
        configuration.ViewMapOwners.Should().Equal(["Base:AnyContainer", "Pack:ChartsOnly"],
            "the generic map is named among the owners like any other");
    }

    /// <summary>A container no specific map claims still goes to the shape match, ahead of the fallback.</summary>
    [Fact]
    public void AContainerNoSpecificMapClaims_GoesToTheShapeMatch_NotTheFallback()
    {
        var configuration = new LayoutClientConfiguration(GetClient())
            .WithGenericView(AnyContainer, "Base:AnyContainer")
            .WithView(ChartsOnly, "Pack:ChartsOnly")
            .WithFallbackView((i, _, _) => Descriptor<FallbackView>(i));

        configuration.GetViewDescriptor(Controls.Stack, null, "Stack")!.Type
            .Should().Be(typeof(ContainerShapeView));
        configuration.GetViewDescriptor(Controls.Html("x"), null, "Html")!.Type
            .Should().Be(typeof(FallbackView), "a control neither tier claims still reaches the fallback");
    }

    /// <summary>
    /// Negative control: the SAME shape match registered as an ordinary map (what the base pack did)
    /// claims the chart ahead of the later chart pack — the defect this tier removes.
    /// </summary>
    [Fact]
    public void TheShapeMatchAsAnOrdinaryMap_ClaimsTheChart()
    {
        var configuration = new LayoutClientConfiguration(GetClient())
            .WithView(AnyContainer, "Base:AnyContainer")
            .WithView(ChartsOnly, "Pack:ChartsOnly");

        configuration.GetViewDescriptor(new ChartControl(), null, "Chart")!.Type
            .Should().Be(typeof(ContainerShapeView), "first-match-wins in registration order");
    }
}
