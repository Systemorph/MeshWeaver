using System.Linq;
using System.Text.Json;
using MeshWeaver.Fixture;
using MeshWeaver.Layout.Client;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// #6036 — <see cref="HorizontalAlignment.Stretch"/> exists so a stack that hosts content with no
/// width of its own (a markdown body holding a wide table) can ask for <c>align-items: stretch</c>
/// without a string literal; and it is an explicit OPT-IN, so no existing layout shifts.
///
/// <para>What the client does with the value is by NAME — the layout serialiser writes enums as
/// strings, and the views parse them into the UI library's enum with
/// <see cref="LayoutClientExtensions.ConvertSingle{T}"/>. So the tests pin the serialised name and
/// its round trip, not a numeric value (the two enums order their members differently). The
/// framework's own stretched sites are pinned in MeshWeaver.Graph.Test
/// (<c>MarkdownColumnStretchTest</c>).</para>
/// </summary>
public class StackCrossAxisAlignmentTest(ITestOutputHelper output) : HubTestBase(output)
{
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutTypes();

    [Fact]
    public void UnsetAlignment_StaysNull_SoNoExistingLayoutShifts()
    {
        Controls.Stack.Skin.HorizontalAlignment.Should().BeNull(
            "Stretch is an explicit opt-in; an unset stack keeps the client's start alignment");
        Controls.Stack.WithOrientation(Orientation.Horizontal).Skin.HorizontalAlignment.Should().BeNull();
    }

    [Fact]
    public void UnsetAlignment_IsNotOnTheWire()
    {
        var json = JsonSerializer.Serialize<UiControl>(Controls.Stack, GetClient().JsonSerializerOptions);
        Output.WriteLine(json);

        json.Should().NotContain("horizontalAlignment");
    }

    [Fact]
    public void Stretch_SerialisesByName()
    {
        var json = JsonSerializer.Serialize<UiControl>(
            Controls.Stack.WithHorizontalAlignment(HorizontalAlignment.Stretch).WithView(Controls.Markdown("| a |")),
            GetClient().JsonSerializerOptions);
        Output.WriteLine(json);

        json.Should().Contain("\"horizontalAlignment\":\"Stretch\"",
            "the client binds the skin value into its own enum by name");
    }

    [Fact]
    public void Stretch_SurvivesTheWireAndConvertsByName()
    {
        var client = GetClient();

        // Rendering puts the typed skin at the head of Skins — that list is what the client reads.
        var stack = Controls.Stack.WithHorizontalAlignment(HorizontalAlignment.Stretch);
        var rendered = stack with { Skins = [stack.Skin] };
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
}
