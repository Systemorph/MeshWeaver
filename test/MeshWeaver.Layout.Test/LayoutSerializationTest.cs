using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using FluentAssertions;
using MeshWeaver.Domain;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace MeshWeaver.Layout.Test;

public class LayoutSerializationTest(ITestOutputHelper output) : HubTestBase(output)
{
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        return base.ConfigureClient(configuration).AddLayoutTypes();
    }

    [Fact]
    public void RealWorldLayoutControlDeserializationTest()
    {
        var client = GetClient();

        // Real-world JSON payload with polymorphic layout controls
        var json = """
        {
            "$type":"MeshWeaver.Layout.StackControl",
            "skin":{
                "$type":"MeshWeaver.Layout.LayoutStackSkin",
                "orientation":"Vertical"
            },
            "areas":[
                {
                    "$type":"MeshWeaver.Layout.NamedAreaControl",
                    "area":"Catalog/1",
                    "moduleName":"MeshWeaver.Layout",
                    "apiVersion":"1.0.0",
                    "id":"1",
                    "skins":[]
                },
                {
                    "$type":"MeshWeaver.Layout.NamedAreaControl",
                    "area":"Catalog/2",
                    "moduleName":"MeshWeaver.Layout",
                    "apiVersion":"1.0.0",
                    "id":"2",
                    "skins":[]
                },
                {
                    "$type":"MeshWeaver.Layout.NamedAreaControl",
                    "area":"Catalog/3",
                    "moduleName":"MeshWeaver.Layout",
                    "apiVersion":"1.0.0",
                    "id":"3",
                    "skins":[]
                },
                {
                    "$type":"MeshWeaver.Layout.NamedAreaControl",
                    "area":"Catalog/4",
                    "moduleName":"MeshWeaver.Layout",
                    "apiVersion":"1.0.0",
                    "id":"4",
                    "skins":[]
                },
                {
                    "$type":"MeshWeaver.Layout.NamedAreaControl",
                    "area":"Catalog/5",
                    "moduleName":"MeshWeaver.Layout",
                    "apiVersion":"1.0.0",
                    "id":"5",
                    "skins":[]
                },
                {
                    "$type":"MeshWeaver.Layout.NamedAreaControl",
                    "area":"Catalog/6",
                    "moduleName":"MeshWeaver.Layout",
                    "apiVersion":"1.0.0",
                    "id":"6",
                    "skins":[]
                }
            ]
        }
        """;        // This should deserialize successfully with the correct polymorphic types
        Action act = () =>
        {
            var deserialized = JsonSerializer.Deserialize<object>(json, client.JsonSerializerOptions);

            // Verify the deserialized object is of the correct type
            deserialized.Should().NotBeNull();
            deserialized.Should().BeOfType<StackControl>();

            var stackControl = (StackControl)deserialized;
            stackControl.Skin.Should().BeOfType<LayoutStackSkin>();
            stackControl.Areas.Should().HaveCount(6);
            stackControl.Areas.Should().AllBeOfType<NamedAreaControl>();

            var layoutStackSkin = (LayoutStackSkin)stackControl.Skin;
            layoutStackSkin.Orientation.Should().Be("Vertical");

            // Verify each area control
            for (int i = 0; i < 6; i++)
            {
                var areaControl = (NamedAreaControl)stackControl.Areas[i];
                areaControl.Area.Should().Be($"Catalog/{i + 1}");
                areaControl.ModuleName.Should().Be("MeshWeaver.Layout");
                areaControl.ApiVersion.Should().Be("1.0.0");
                areaControl.Id.Should().Be((i + 1).ToString());
                areaControl.Skins.Should().NotBeNull().And.BeEmpty();
            }
        };

        act.Should().NotThrow();
    }

    [Fact]
    public void UiControlPolymorphismSetupTest()
    {
        var client = GetClient();

        // Check if UiControl has derived types registered
        var typeRegistry = client.ServiceProvider.GetRequiredService<ITypeRegistry>();

        // Check if StackControl is registered
        var stackControlRegistered = typeRegistry.TryGetType("MeshWeaver.Layout.StackControl", out var stackControlInfo);
        stackControlRegistered.Should().BeTrue("StackControl should be registered for polymorphism");

        // Check if NamedAreaControl is registered  
        var namedAreaRegistered = typeRegistry.TryGetType("MeshWeaver.Layout.NamedAreaControl", out var namedAreaInfo);
        namedAreaRegistered.Should().BeTrue("NamedAreaControl should be registered for polymorphism");

        // Try to find all types that inherit from UiControl
        var allTypes = typeof(UiControl).Assembly.GetTypes()
            .Where(t => typeof(UiControl).IsAssignableFrom(t) && !t.IsAbstract)
            .ToList();

        allTypes.Should().NotBeEmpty("There should be concrete types that inherit from UiControl");
        allTypes.Should().Contain(t => t.Name == "StackControl");
        allTypes.Should().Contain(t => t.Name == "NamedAreaControl");
    }

    [Fact]
    public void Deserialize()
    {
        var serialized =
            """{"$type":"MeshWeaver.Layout.StackControl","skin":{"$type":"MeshWeaver.Layout.LayoutStackSkin","orientation":"Vertical"},"areas":[{"$type":"MeshWeaver.Layout.NamedAreaControl","area":"Catalog/1","moduleName":"MeshWeaver.Layout","apiVersion":"1.0.0","id":"1","skins":[]},{"$type":"MeshWeaver.Layout.NamedAreaControl","area":"Catalog/2","moduleName":"MeshWeaver.Layout","apiVersion":"1.0.0","id":"2","skins":[]},{"$type":"MeshWeaver.Layout.NamedAreaControl","area":"Catalog/3","moduleName":"MeshWeaver.Layout","apiVersion":"1.0.0","id":"3","skins":[]},{"$type":"MeshWeaver.Layout.NamedAreaControl","area":"Catalog/4","moduleName":"MeshWeaver.Layout","apiVersion":"1.0.0","id":"4","skins":[]},{"$type":"MeshWeaver.Layout.NamedAreaControl","area":"Catalog/5","moduleName":"MeshWeaver.Layout","apiVersion":"1.0.0","id":"5","skins":[]},{"$type":"MeshWeaver.Layout.NamedAreaControl","area":"Catalog/6","moduleName":"MeshWeaver.Layout","apiVersion":"1.0.0","id":"6","skins":[]},{"$type":"MeshWeaver.Layout.NamedAreaControl","area":"Catalog/7","moduleName":"MeshWeaver.Layout","apiVersion":"1.0.0","id":"7","skins":[]},{"$type":"MeshWeaver.Layout.NamedAreaControl","area":"Catalog/8","moduleName":"MeshWeaver.Layout","apiVersion":"1.0.0","id":"8","skins":[]},{"$type":"MeshWeaver.Layout.NamedAreaControl","area":"Catalog/9","moduleName":"MeshWeaver.Layout","apiVersion":"1.0.0","id":"9","skins":[]},{"$type":"MeshWeaver.Layout.NamedAreaControl","area":"Catalog/10","moduleName":"MeshWeaver.Layout","apiVersion":"1.0.0","id":"10","skins":[]},{"$type":"MeshWeaver.Layout.NamedAreaControl","area":"Catalog/11","moduleName":"MeshWeaver.Layout","apiVersion":"1.0.0","id":"11","skins":[]},{"$type":"MeshWeaver.Layout.NamedAreaControl","area":"Catalog/12","moduleName":"MeshWeaver.Layout","apiVersion":"1.0.0","id":"12","skins":[]},{"$type":"MeshWeaver.Layout.NamedAreaControl","area":"Catalog/13","moduleName":"MeshWeaver.Layout","apiVersion":"1.0.0","id":"13","skins":[]},{"$type":"MeshWeaver.Layout.NamedAreaControl","area":"Catalog/14","moduleName":"MeshWeaver.Layout","apiVersion":"1.0.0","id":"14","skins":[]},{"$type":"MeshWeaver.Layout.NamedAreaControl","area":"Catalog/15","moduleName":"MeshWeaver.Layout","apiVersion":"1.0.0","id":"15","skins":[]},{"$type":"MeshWeaver.Layout.NamedAreaControl","area":"Catalog/16","moduleName":"MeshWeaver.Layout","apiVersion":"1.0.0","id":"16","skins":[]},{"$type":"MeshWeaver.Layout.NamedAreaControl","area":"Catalog/17","moduleName":"MeshWeaver.Layout","apiVersion":"1.0.0","id":"17","skins":[]}],"moduleName":"MeshWeaver.Layout","apiVersion":"1.0.0","skins":[{"$type":"MeshWeaver.Layout.LayoutStackSkin","orientation":"Vertical"},{}],"pageTitle":"Articles"}""";
        var deserialized = JsonSerializer.Deserialize<UiControl>(serialized, GetClient().JsonSerializerOptions);
        deserialized.Should().NotBeNull("Deserialized object should not be null");
    }

    [Fact]
    public void NamedAreaControlPropertyTest()
    {
        // Test the NamedAreaControl property directly
        var namedArea = new NamedAreaControl("Catalog/1");

        // Check if the Area property is set correctly
        namedArea.Area.Should().Be("Catalog/1");
        namedArea.Area.Should().NotBeNull();

        Output.WriteLine($"NamedArea.Area: '{namedArea.Area}'");
        Output.WriteLine($"NamedArea.Area Type: {namedArea.Area?.GetType()}");

        // Test direct serialization of NamedAreaControl
        var client = Router.GetHostedHub(new ClientAddress(), ConfigureClient);
        var serialized = JsonSerializer.Serialize(namedArea, client.JsonSerializerOptions);

        Output.WriteLine($"Direct NamedAreaControl JSON: {serialized}");

        // Check if the JSON contains the area property
        serialized.Should().Contain("area");
        serialized.Should().Contain("Catalog/1");
    }
    [Fact]
    public void StackControlWithViewTest()
    {
        var client = Router.GetHostedHub(new ClientAddress(), ConfigureClient);

        // Create NamedAreaControl directly
        var namedArea = new NamedAreaControl("Catalog/1");
        Output.WriteLine($"Original NamedArea.Area: '{namedArea.Area}'");

        // Create StackControl and add the view
        var stackControl = new StackControl();
        stackControl = stackControl.WithView(namedArea);

        Output.WriteLine($"StackControl Areas Count: {stackControl.Areas?.Count}");

        if (stackControl.Areas?.Count > 0)
        {
            var firstArea = stackControl.Areas[0];
            Output.WriteLine($"First Area in Stack Type: {firstArea?.GetType()}");
            Output.WriteLine($"First Area in Stack Area Value: '{firstArea?.Area}'");
            Output.WriteLine($"Are they the same object? {ReferenceEquals(namedArea, firstArea)}");
        }

        // Serialize the stack control
        var serialized = JsonSerializer.Serialize(stackControl, client.JsonSerializerOptions);
        Output.WriteLine($"StackControl JSON: {serialized}");

        // Check if the JSON contains the area property
        serialized.Should().Contain("\"area\"");
        serialized.Should().Contain("Catalog/1");
    }

    [Fact]
    public void SerializationShouldNotProduceEmptySkinsAndShouldHandleThem()
    {
        var client = GetClient();

        // First, test that our implementation does not produce empty skins or nulls
        var stackControl = new StackControl();

        // Add a valid skin
        var validSkin = new LayoutStackSkin { Orientation = "Vertical" };

        // Test with various skin scenarios that might cause issues
        var skinsWithNull = ImmutableList<Skin>.Empty
            .Add(validSkin)
            .Add(null!); // This should be filtered out during serialization

        var stackWithNullSkin = stackControl with { Skins = skinsWithNull };

        // Serialize
        var serialized = JsonSerializer.Serialize(stackWithNullSkin, client.JsonSerializerOptions);

        Output.WriteLine($"Serialized JSON: {serialized}");

        // The serialized JSON should not contain empty objects "{}" or null literals
        serialized.Should().NotContain("{}");
        serialized.Should().NotContain("null");

        // Verify we can deserialize it back without errors
        var deserialized = JsonSerializer.Deserialize<UiControl>(serialized, client.JsonSerializerOptions);
        deserialized.Should().NotBeNull();

        // Second, test that we can handle legacy payloads with empty skin objects
        var legacyJsonWithEmptySkin = """{"$type":"MeshWeaver.Layout.StackControl","skin":{"$type":"MeshWeaver.Layout.LayoutStackSkin","orientation":"Vertical"},"areas":[],"moduleName":"MeshWeaver.Layout","apiVersion":"1.0.0","skins":[{"$type":"MeshWeaver.Layout.LayoutStackSkin","orientation":"Vertical"},{}],"pageTitle":"Articles"}""";

        // This should not throw an exception - empty objects should be handled gracefully
        Action deserializeLegacy = () =>
        {
            var legacyDeserialized = JsonSerializer.Deserialize<UiControl>(legacyJsonWithEmptySkin, client.JsonSerializerOptions);
            legacyDeserialized.Should().NotBeNull();
        };

        deserializeLegacy.Should().NotThrow("Deserialization should handle legacy payloads with empty skin objects");

    }
}
