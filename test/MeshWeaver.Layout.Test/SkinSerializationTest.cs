using System.Collections.Immutable;
using System.Text.Json;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Layout.Test;

public class SkinSerializationTest(ITestOutputHelper output) : HubTestBase(output)
{
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        return base.ConfigureClient(configuration).AddLayoutTypes();
    }

    [Fact]
    public void SerializationShouldNotProduceEmptySkinObjects()
    {
        var client = GetClient();

        // Create a StackControl with some skins including potentially problematic ones
        var stackControl = new StackControl();

        // Add a valid skin
        var validSkin = new LayoutStackSkin { Orientation = "Vertical" };

        // Test with various skin scenarios
        var skinsWithNull = ImmutableList<Skin>.Empty
            .Add(validSkin)
            .Add(null!); // This might cause issues

        var stackWithNullSkin = stackControl with { Skins = skinsWithNull };

        // Serialize
        var serialized = JsonSerializer.Serialize(stackWithNullSkin, client.JsonSerializerOptions);

        Output.WriteLine($"Serialized JSON: {serialized}");

        // The serialized JSON should not contain empty objects "{}"
        serialized.Should().NotContain("{}");

        // It should also not contain "null" in the skins array
        serialized.Should().NotContain("null");

        // Verify we can deserialize it back without errors
        var deserialized = JsonSerializer.Deserialize<UiControl>(serialized, client.JsonSerializerOptions);
        deserialized.Should().NotBeNull();
        var deserializedStack = deserialized.Should().BeOfType<StackControl>().Which;
        deserializedStack.Skins.Should().NotBeNull();
        // Should only contain the valid skin, null/empty ones should be filtered out
        deserializedStack.Skins.Should().HaveCount(1);
        deserializedStack.Skins[0].Should().BeOfType<LayoutStackSkin>();
    }

    [Fact]
    public void EmptySkinsArrayShouldBeValid()
    {
        var client = GetClient();

        var stackControl = new StackControl();
        var serialized = JsonSerializer.Serialize(stackControl, client.JsonSerializerOptions);

        Output.WriteLine($"Empty skins serialized: {serialized}");

        // Should not contain empty objects
        serialized.Should().NotContain("{}");

        // Should deserialize successfully
        var deserialized = JsonSerializer.Deserialize<UiControl>(serialized, client.JsonSerializerOptions);
        deserialized.Should().NotBeNull();
    }

    [Fact]
    public void SkinWithEmptyPropertiesShouldNotSerializeAsEmptyObject()
    {
        var client = GetClient();

        // Create a skin with minimal properties
        var skin = new LayoutStackSkin(); // No orientation set

        var stackControl = new StackControl();
        var stackWithSkin = stackControl with { Skins = ImmutableList<Skin>.Empty.Add(skin) };

        var serialized = JsonSerializer.Serialize(stackWithSkin, client.JsonSerializerOptions);

        Output.WriteLine($"Minimal skin serialized: {serialized}");

        // Even a skin with default properties should have a $type discriminator
        serialized.Should().Contain("$type");
        serialized.Should().Contain("LayoutStackSkin");

        // Should not contain empty objects
        serialized.Should().NotContain("{}");

        // Should deserialize successfully
        var deserialized = JsonSerializer.Deserialize<UiControl>(serialized, client.JsonSerializerOptions);
        deserialized.Should().NotBeNull();
    }

    [Fact]
    public void DeserializeToBaseUiControlTypeShouldWork()
    {
        var client = GetClient();

        // Create a StackControl with skins
        var stackControl = new StackControl();
        var validSkin = new LayoutStackSkin { Orientation = "Horizontal" };
        var stackWithSkin = stackControl with { Skins = ImmutableList<Skin>.Empty.Add(validSkin) };

        // Serialize the StackControl
        var serialized = JsonSerializer.Serialize(stackWithSkin, client.JsonSerializerOptions);

        Output.WriteLine($"Serialized StackControl: {serialized}");

        // The JSON should contain type discriminators for polymorphic deserialization
        serialized.Should().Contain("$type");
        serialized.Should().Contain("StackControl");

        // Deserialize explicitly to the base UiControl type
        var deserializedAsBase = JsonSerializer.Deserialize<UiControl>(serialized, client.JsonSerializerOptions);

        // Should successfully deserialize to the correct concrete type
        deserializedAsBase.Should().NotBeNull();

        // Narrow to StackControl and verify properties
        var deserializedStack = deserializedAsBase.Should().BeOfType<StackControl>().Which;
        deserializedStack.Skins.Should().NotBeNull();
        deserializedStack.Skins.Should().HaveCount(1);
        deserializedStack.Skins[0].Should().BeOfType<LayoutStackSkin>();

        var deserializedSkin = (LayoutStackSkin)deserializedStack.Skins[0];
        deserializedSkin.Orientation.Should().Be("Horizontal");
    }

    /// <summary>
    /// The expander is the OUTERMOST skin of the control it is added to, and its declaration (title,
    /// summary, open or collapsed) survives the wire — the renderer reads all three from the skin.
    /// </summary>
    [Fact]
    public void ExpanderSkin_RoundTrips_AsTheOutermostSkin()
    {
        var client = GetClient();

        var section = Controls.Stack
            .WithView(Controls.Markdown("body"), "Body")
            .AddSkin(Skins.Expander("Details").WithSummary("12 fields").WithExpanded(false));

        var serialized = JsonSerializer.Serialize((UiControl)section, client.JsonSerializerOptions);
        Output.WriteLine($"Serialized expander: {serialized}");
        serialized.Should().Contain("ExpanderSkin");

        var deserialized = JsonSerializer.Deserialize<UiControl>(serialized, client.JsonSerializerOptions);
        var popped = deserialized.Should().BeOfType<StackControl>().Which.PopSkin(out var outer);
        var expander = outer.Should().BeOfType<ExpanderSkin>(
            "an expander added last must be popped first, so it wraps the whole container").Which;
        expander.Title.Should().Be("Details");
        expander.Summary.Should().Be("12 fields");
        expander.Expanded.Should().Be(false);
        popped.Skins.OfType<ExpanderSkin>().Should().BeEmpty("popping removes exactly the expander");
    }

    /// <summary><see cref="Skins.Expander"/> declares a section open unless told otherwise.</summary>
    [Fact]
    public void ExpanderSkin_DefaultsToOpen()
    {
        var skin = Skins.Expander("Request");
        skin.Title.Should().Be("Request");
        skin.Expanded.Should().BeNull("null is the declared-open default the renderers honour");
        skin.Summary.Should().BeNull();
    }
}
