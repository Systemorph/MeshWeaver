using System.Collections.Immutable;
using System.Text.Json;
using FluentAssertions;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit;
using Xunit.Abstractions;

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
            .Add(null); // This might cause issues

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
        deserialized.Should().BeOfType<StackControl>();

        var deserializedStack = (StackControl)deserialized;
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
        serialized.Should().Contain("MeshWeaver.Layout.LayoutStackSkin");

        // Should not contain empty objects
        serialized.Should().NotContain("{}");

        // Should deserialize successfully
        var deserialized = JsonSerializer.Deserialize<UiControl>(serialized, client.JsonSerializerOptions);
        deserialized.Should().NotBeNull();
    }
}
