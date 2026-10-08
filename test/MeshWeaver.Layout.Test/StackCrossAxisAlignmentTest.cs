using System;
using System.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Layout.Client;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// #6036 — a vertical <see cref="StackControl"/> stretches its children across its column by default,
/// and <see cref="HorizontalAlignment.Stretch"/> exists so a module can ask for it without a string
/// literal.
///
/// <para>The defect: the Blazor client renders a stack as a FluentStack whose own default is
/// <c>HorizontalAlignment.Left</c> (<c>align-items: start</c>), so each child of a vertical stack was
/// as wide as its own content. A markdown body holding one wide table measured 3,211 px inside a
/// 980 px column, and the pane's <c>overflow-x: clip</c> cut the rest off with no scrollbar. The
/// default now travels in the control itself, so every client reads the same alignment.</para>
///
/// <para>What the client does with the value is by NAME — the layout serialiser writes enums as
/// strings, and the views parse them into the UI library's enum with
/// <see cref="LayoutClientExtensions.ConvertSingle{T}"/>. So the tests pin the serialised name and
/// its round trip, not a numeric value (the two enums order their members differently).</para>
/// </summary>
public class StackCrossAxisAlignmentTest(ITestOutputHelper output) : HubTestBase(output)
{
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutTypes();

    private static LayoutStackSkin SkinOf(StackControl stack) => stack.Skin;

    [Fact]
    public void VerticalStack_DefaultsItsCrossAxisToStretch()
    {
        SkinOf(Controls.Stack).HorizontalAlignment.Should().Be(HorizontalAlignment.Stretch,
            "a vertical stack's children must be as wide as the column, or a wide markdown table clips (#6036)");
    }

    [Fact]
    public void VerticalStack_SerialisesStretchByName()
    {
        var client = GetClient();

        var json = JsonSerializer.Serialize<UiControl>(Controls.Stack.WithView(Controls.Markdown("| a |")),
            client.JsonSerializerOptions);
        Output.WriteLine(json);

        json.Should().Contain("\"horizontalAlignment\":\"Stretch\"",
            "the client binds the skin value into its own enum by name, so the default must be on the wire");
    }

    [Fact]
    public void Stretch_SurvivesTheWireAndConvertsByName()
    {
        var client = GetClient();

        // Rendering puts the typed skin at the head of Skins — that list is what the client reads.
        var rendered = Controls.Stack with { Skins = [Controls.Stack.Skin] };
        var json = JsonSerializer.Serialize<UiControl>(rendered, client.JsonSerializerOptions);
        var roundTripped = JsonSerializer.Deserialize<UiControl>(json, client.JsonSerializerOptions);
        var skin = roundTripped.Should().BeOfType<StackControl>().Which.Skins!.OfType<LayoutStackSkin>().Single();
        Output.WriteLine($"wire value: {skin.HorizontalAlignment} ({skin.HorizontalAlignment?.GetType().Name})");

        // Exactly what LayoutStackView.DataBind does with the deserialised skin value.
        client.ConvertSingle<HorizontalAlignment>(skin.HorizontalAlignment, null, HorizontalAlignment.Left)
            .Should().Be(HorizontalAlignment.Stretch);
        client.ConvertSingle<HorizontalAlignment>("stretch", null, HorizontalAlignment.Left)
            .Should().Be(HorizontalAlignment.Stretch, "the documented lowercase form must read the same way");
    }

    [Fact]
    public void ExplicitAlignment_WinsOverTheDefault()
    {
        SkinOf(Controls.Stack.WithHorizontalAlignment(HorizontalAlignment.Start)).HorizontalAlignment
            .Should().Be(HorizontalAlignment.Start);
        SkinOf(Controls.Stack.WithHorizontalAlignment("center")).HorizontalAlignment
            .Should().Be("center");
    }

    [Fact]
    public void HorizontalStack_DerivesNoDefault()
    {
        // On a horizontal stack HorizontalAlignment is the MAIN axis — justify-content — where the
        // client's own default (start) is what every horizontal stack already renders.
        SkinOf(Controls.Stack.WithOrientation(Orientation.Horizontal)).HorizontalAlignment.Should().BeNull();
        SkinOf(Controls.Stack.WithOrientation("horizontal")).HorizontalAlignment.Should().BeNull();
    }

    [Fact]
    public void DataBoundOrientation_DerivesNoDefault()
    {
        // The orientation is not known until the client resolves the binding, so nothing is guessed.
        SkinOf(Controls.Stack.WithOrientation(new JsonPointerReference("orientation"))).HorizontalAlignment
            .Should().BeNull();
    }

    /// <summary>
    /// Every member name must exist in the Fluent UI <c>HorizontalAlignment</c> the Blazor client binds
    /// into (Microsoft.FluentUI.AspNetCore.Components 4.14.4, read from the assembly: Left, Start,
    /// Center, Right, End, Stretch, SpaceBetween) — a name the client cannot parse degrades to its
    /// default, silently. Core does not reference the UI library, so its names are listed here.
    /// </summary>
    [Fact]
    public void EveryMemberName_IsOneTheClientEnumCarries()
    {
        string[] fluentNames = ["Left", "Start", "Center", "Right", "End", "Stretch", "SpaceBetween"];

        Enum.GetNames<HorizontalAlignment>().Should().BeSubsetOf(fluentNames);
        Enum.GetNames<HorizontalAlignment>().Should().Contain(nameof(HorizontalAlignment.Stretch));
    }
}
