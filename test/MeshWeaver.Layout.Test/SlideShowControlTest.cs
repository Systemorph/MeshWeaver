using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using MeshWeaver.Domain;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// Pins the frames-mode contract of <see cref="SlideShowControl"/> (client-side slide swapping):
/// the pre-rendered frames, the start index and the address-bar template must survive the hub
/// serializer byte-for-byte, because the Blazor view renders EVERY frame from this payload and
/// swaps them without any further server round trip — a frame lost or reordered on the wire is
/// a slide silently missing from the presentation.
/// </summary>
public class SlideShowControlTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutTypes();

    [Fact]
    public void Frames_mode_round_trips_through_the_hub_serializer()
    {
        var options = GetClient().JsonSerializerOptions;
        var control = new SlideShowControl
        {
            Frames = ImmutableList.Create(
                new SlideFrame("<h1>One</h1>", "linear-gradient(135deg,#0b1020,#4c1d95)"),
                new SlideFrame("<h1>Two</h1>", null)),
            StartIndex = 1,
            UrlTemplate = "/MyDeck/Present?i={0}",
            ExitHref = "/MyDeck",
        };

        var json = JsonSerializer.Serialize<UiControl>(control, options);
        Output.WriteLine(json);

        // The WIRE, not just the round trip: object equality after a deserialize would still hold
        // if the JSON shape drifted (a renamed or dropped $type, a differently-nested frames array),
        // because both ends move together. The view reads this JSON, so pin the JSON.
        json.Should().Contain("\"frames\":[",
            because: "the frames array is the payload the presenter renders every slide from");
        Regex.Matches(json, "\"\\$type\":\"SlideFrame\"").Count.Should().Be(2,
            because: "each frame must carry its discriminator — without it a hub that has not " +
                     "registered SlideFrame reads untyped JsonElements and the deck renders empty");
        json.Should().Contain("\"startIndex\":1", because: "the opening slide travels on the wire");
        json.Should().Contain("\"urlTemplate\":\"/MyDeck/Present?i={0}\"",
            because: "the address-bar template is what keeps a client-side swap deep-linkable");
        json.Should().NotContain("\"background\":null",
            because: "a slide with no background of its own omits the field rather than sending null");

        var back = JsonSerializer.Deserialize<UiControl>(json, options)
            .Should().BeOfType<SlideShowControl>().Subject;

        back.Frames.Should().NotBeNull();
        back.Frames!.Should().HaveCount(2);
        back.Frames![0].Should().Be(new SlideFrame("<h1>One</h1>", "linear-gradient(135deg,#0b1020,#4c1d95)"));
        back.Frames![1].Should().Be(new SlideFrame("<h1>Two</h1>", null));
        back.StartIndex.Should().Be(1);
        back.UrlTemplate.Should().Be("/MyDeck/Present?i={0}");
        back.ExitHref.Should().Be("/MyDeck");
    }

    /// <summary>
    /// <see cref="SlideFrame"/> must be registered EXPLICITLY by <c>AddLayoutTypes</c>, exactly like
    /// the other plain records that ride inside control state (KpiItem, TowerBand, ComparisonPair,
    /// Icon). It is not an <c>IUiControl</c>, so the reflection sweep cannot see it — and without an
    /// explicit registration the polymorphic writer emits an auto short-name that the RECEIVING hub
    /// adopts as a side effect of the read. That fallback makes the round-trip above pass while the
    /// contract is still missing, so this asserts the registry BEFORE anything is serialised: a hub
    /// that never happens to read a frame first would hand the view untyped JsonElements, and a deck
    /// whose frames read as absent presents no slides at all.
    /// </summary>
    [Fact]
    public void SlideFrame_is_explicitly_registered_on_the_hub()
    {
        var typeRegistry = GetClient().ServiceProvider.GetRequiredService<ITypeRegistry>();

        typeRegistry.TryGetCollectionName(typeof(SlideFrame), out var typeName).Should().BeTrue(
            "AddLayoutTypes must register SlideFrame explicitly — the IUiControl sweep cannot see it");
        typeName.Should().Be(nameof(SlideFrame));
        typeRegistry.TryGetType(nameof(SlideFrame), out var definition).Should().BeTrue();
        definition!.Type.Should().Be(typeof(SlideFrame));
    }

    [Fact]
    public void Href_mode_stays_the_legacy_wire_shape()
    {
        var options = GetClient().JsonSerializerOptions;
        var control = new SlideShowControl { NextHref = "/d/Present?i=1", ExitHref = "/d" };

        var json = JsonSerializer.Serialize<UiControl>(control, options);
        Output.WriteLine(json);

        // The progressive-enhancement contract, asserted as the BYTES an href-only client receives:
        // an href-mode slide show must serialise exactly as it did before frames mode existed. The
        // three new fields are absent, not null — React and React Native present from this payload
        // and must see nothing new in it.
        json.Should().Be(
            $$"""{"$type":"SlideShowControl","nextHref":"/d/Present?i=1","exitHref":"/d","moduleName":"{{ModuleSetup.ModuleName}}","apiVersion":"{{ModuleSetup.ApiVersion}}","skins":[]}""",
            because: "adding frames mode must not change one byte of the href-mode wire shape");

        var back = JsonSerializer.Deserialize<UiControl>(json, options)
            .Should().BeOfType<SlideShowControl>().Subject;

        back.Frames.Should().BeNull("no frames means the original href-driver behavior");
        back.NextHref.Should().Be("/d/Present?i=1");
        back.ExitHref.Should().Be("/d");
    }
}
