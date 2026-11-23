using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using MeshWeaver.Fixture;
using MeshWeaver.Layout.Chart;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// A platform type is registered ONCE, under its canonical name, from the platform
/// (<c>AddLayoutTypes</c>) — a hub that only has the layout types (an in-mesh NodeType hub, whose
/// host never calls the portal-side Radzen registration) must send <see cref="ChartControl"/> as
/// <c>ChartControl</c> and its series without auto-registering them.
/// </summary>
public class ChartCanonicalDiscriminatorTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutTypes();

    [Fact]
    public void ChartControl_is_sent_under_its_canonical_discriminator()
    {
        var hub = GetClient();
        var control = new ChartControl
        {
            Series = ImmutableList.Create<ChartSeries>(new LineSeries([1.0, 2.0], "A"), new ColumnSeries([3.0], "B")),
        };
        var json = JsonSerializer.Serialize<UiControl>(control, hub.JsonSerializerOptions);
        Output.WriteLine(json);

        json.Should().Contain("\"$type\":\"ChartControl\"");
        json.Should().NotContain("MeshWeaver.Layout.Chart");
        var registry = hub.Configuration.TypeRegistry;
        registry.TryGetCollectionName(typeof(ChartControl), out var name).Should().BeTrue();
        name.Should().Be("ChartControl");
        foreach (var t in new[] { typeof(LineSeries), typeof(BarSeries), typeof(ColumnSeries), typeof(PieSeries),
                     typeof(DoughnutSeries), typeof(RadarSeries), typeof(PolarSeries), typeof(ScatterSeries), typeof(BubbleSeries) })
            registry.Types.Any(k => k.Value.Type == t).Should().BeTrue(t.Name + " must be registered by the platform, not auto-registered on first write");
    }

    [Fact]
    public void Registering_ChartControl_again_by_full_name_keeps_the_canonical_name()
    {
        var hub = GetClient();
        hub.Configuration.WithType(typeof(ChartControl));
        var json = JsonSerializer.Serialize<UiControl>(new ChartControl(), hub.JsonSerializerOptions);
        json.Should().Contain("\"$type\":\"ChartControl\"");
    }

    [Fact]
    public void The_full_name_discriminator_still_resolves_as_a_read_alias()
    {
        // Persisted $type values written under the namespace-qualified name must still RESOLVE by name
        // (the registry's read alias), while the canonical output name stays the short one. (Reading a
        // full-name payload through the polymorphic UiControl base has never worked for ANY layout
        // control, which is registered under its short name only — see the PR notes.)
        var registry = GetClient().Configuration.TypeRegistry;
        registry.GetTypeDefinition("MeshWeaver.Layout.Chart.ChartControl")!.Type.Should().Be(typeof(ChartControl));
        registry.GetTypeDefinition("ChartControl")!.Type.Should().Be(typeof(ChartControl));
        registry.TryGetCollectionName(typeof(ChartControl), out var name).Should().BeTrue();
        name.Should().Be("ChartControl");
    }
}

/// <summary>
/// The portal registers <c>ChartControl</c> through <c>AddRadzenCharts</c> →
/// <c>MessageHubConfiguration.WithType(typeof(ChartControl))</c>, whose name DEFAULTS to the full
/// type name. When that runs BEFORE the layout types, the hub must still send the canonical name.
/// </summary>
public class ChartFullNameFirstTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).WithType(typeof(ChartControl)).AddLayoutTypes();

    [Fact]
    public void A_full_name_registration_made_before_the_layout_types_does_not_win()
    {
        var json = JsonSerializer.Serialize<UiControl>(new ChartControl(), GetClient().JsonSerializerOptions);
        Output.WriteLine(json);
        json.Should().Contain("\"$type\":\"ChartControl\"");
    }

    [Fact]
    public void The_full_name_discriminator_still_resolves_as_a_read_alias()
    {
        // Persisted $type values written under the namespace-qualified name must still RESOLVE by name
        // (the registry's read alias), while the canonical output name stays the short one. (Reading a
        // full-name payload through the polymorphic UiControl base has never worked for ANY layout
        // control, which is registered under its short name only — see the PR notes.)
        var registry = GetClient().Configuration.TypeRegistry;
        registry.GetTypeDefinition("MeshWeaver.Layout.Chart.ChartControl")!.Type.Should().Be(typeof(ChartControl));
        registry.GetTypeDefinition("ChartControl")!.Type.Should().Be(typeof(ChartControl));
        registry.TryGetCollectionName(typeof(ChartControl), out var name).Should().BeTrue();
        name.Should().Be("ChartControl");
    }
}
