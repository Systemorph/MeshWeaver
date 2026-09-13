// <meshweaver>
// Id: Testing/Layout/OptionPolymorphismTest
// DisplayName: Testing/Layout/OptionPolymorphismTest — migrated from xunit (convert-xunit-to-inmesh.py)
// </meshweaver>
#nullable enable
using MeshWeaver.Reactive.Assertions;
using MeshWeaver.Testing.InMesh;
using System;
﻿using System.Collections.Generic;
using System.Text.Json;
using MeshWeaver.Domain;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

public class OptionPolymorphismTest(MeshTestContext context) : InMeshTestBase(output)
{
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration) =>
        configuration.AddLayout(x => x).AddLayoutTypes();

    [MeshFact]
    public void OptionShouldSerializeWithTypeDiscriminator()
    {
        var host = GetHost();
        var typeRegistry = host.ServiceProvider.GetRequiredService<ITypeRegistry>();
        var serializerOptions = host.JsonSerializerOptions;

        // Create an Option<string>
        var option = new Option<string>("test", "Test Text");
        var optionType = option.GetType();

        // Check if the type is registered
        var hasTypeName = typeRegistry.TryGetCollectionName(optionType, out var typeName);
        hasTypeName.Should().BeTrue();
        typeName.Should().NotBeNullOrEmpty();        // Serialize the option
        var json = JsonSerializer.Serialize((Option)option, serializerOptions);
        Output.WriteLine($"Serialized Option<string>: {json}");

        // Check if the JSON contains $type
        json.Should().Contain("$type");

        // Deserialize back
        var deserialized = JsonSerializer.Deserialize<Option>(json, serializerOptions);
        deserialized.Should().NotBeNull();
        deserialized.Should().BeOfType<Option<string>>();
        ((Option<string>)deserialized!).Text.Should().Be("Test Text");
    }

    [MeshFact]
    public void OptionCollectionShouldSerializeWithTypeDiscriminator()
    {
        var host = GetHost();
        var serializerOptions = host.JsonSerializerOptions;

        // Create a collection of options
        var options = new object[]
        {
            new Option<string>("One", "One"),
            new Option<string>("Two", "Two")
        };

        // Serialize the collection
        var json = JsonSerializer.Serialize(options, serializerOptions);
        Output.WriteLine($"Serialized Option<string>[]: {json}");

        // Check if the JSON contains $type for each option
        json.Should().Contain("$type");

        // Deserialize back
        var deserialized = JsonSerializer.Deserialize<Option[]>(json, serializerOptions);
        deserialized.Should().NotBeNull();
        deserialized.Should().HaveCount(2);
        deserialized![0].Should().BeOfType<Option<string>>();
        deserialized[1].Should().BeOfType<Option<string>>();
    }
}
