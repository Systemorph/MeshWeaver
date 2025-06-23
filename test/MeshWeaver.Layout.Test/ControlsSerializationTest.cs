using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit.Abstractions;

namespace MeshWeaver.Layout.Test;

public class ControlsSerializationTest(ITestOutputHelper output) : HubTestBase(output)
{
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration).AddLayout(x => x);
    }


    private const string benchmark =
        $$$"""{"$type":"MeshWeaver.Layout.HtmlControl","data":"Hello World","moduleName":"{{{ModuleSetup.ModuleName}}}","apiVersion":"{{{ModuleSetup.ApiVersion}}}","skins":[]}""";
    [HubFact]
    public void BasicSerialization()
    {
        var host = GetHost();
        var serialized = JsonSerializer.Serialize(new HtmlControl("Hello World"), host.JsonSerializerOptions);
        serialized.Should().Be(benchmark);


    }

    [HubFact]
    public void OptionCollectionSerialization()
    {
        var host = GetHost();

        // Create a collection of Option<int> objects
        var options = new List<Option>
        {
            new Option<int>(1, "One"),
            new Option<int>(2, "Two"),
            new Option<int>(3, "Three")
        };

        IReadOnlyCollection<Option> readOnlyOptions = options.AsReadOnly();

        // Serialize the collection
        var serialized = JsonSerializer.Serialize(readOnlyOptions, host.JsonSerializerOptions);
        Output.WriteLine($"Serialized: {serialized}");

        // Deserialize back to IReadOnlyCollection<Option>
        var deserialized = JsonSerializer.Deserialize<IReadOnlyCollection<Option>>(serialized, host.JsonSerializerOptions);

        // Verify the results
        deserialized.Should().NotBeNull();
        deserialized.Should().HaveCount(3);

        var deserializedList = deserialized.ToList();
        deserializedList[0].Should().BeOfType<Option<int>>();
        deserializedList[0].Text.Should().Be("One");
        ((Option<int>)deserializedList[0]).Item.Should().Be(1);

        deserializedList[1].Should().BeOfType<Option<int>>();
        deserializedList[1].Text.Should().Be("Two");
        ((Option<int>)deserializedList[1]).Item.Should().Be(2);

        deserializedList[2].Should().BeOfType<Option<int>>();
        deserializedList[2].Text.Should().Be("Three");
        ((Option<int>)deserializedList[2]).Item.Should().Be(3);
    }

    [HubFact]
    public void OptionReadOnlyListSerialization()
    {
        var host = GetHost();

        // Create a collection of mixed Option types
        var options = new List<Option>
        {
            new Option<string>("hello", "Hello"),
            new Option<int>(42, "Forty-Two"),
            new Option<bool>(true, "True")
        };

        IReadOnlyList<Option> readOnlyList = options.AsReadOnly();

        // Serialize the list
        var serialized = JsonSerializer.Serialize(readOnlyList, host.JsonSerializerOptions);
        Output.WriteLine($"Serialized: {serialized}");

        // Deserialize back to IReadOnlyList<Option>
        var deserialized = JsonSerializer.Deserialize<IReadOnlyList<Option>>(serialized, host.JsonSerializerOptions);

        // Verify the results
        deserialized.Should().NotBeNull();
        deserialized.Should().HaveCount(3);

        deserialized[0].Should().BeOfType<Option<string>>();
        deserialized[0].Text.Should().Be("Hello");
        ((Option<string>)deserialized[0]).Item.Should().Be("hello");

        deserialized[1].Should().BeOfType<Option<int>>();
        deserialized[1].Text.Should().Be("Forty-Two");
        ((Option<int>)deserialized[1]).Item.Should().Be(42);

        deserialized[2].Should().BeOfType<Option<bool>>();
        deserialized[2].Text.Should().Be("True");
        ((Option<bool>)deserialized[2]).Item.Should().Be(true);
    }

    [HubFact]
    public void OptionEnumerableSerialization()
    {
        var host = GetHost();

        // Create an enumerable of Option objects
        var options = new[]
        {
            new Option<int>(100, "One Hundred"),
            new Option<int>(200, "Two Hundred")
        }.AsEnumerable();

        IEnumerable<Option> enumerable = options;

        // Serialize the enumerable
        var serialized = JsonSerializer.Serialize(enumerable, host.JsonSerializerOptions);
        Output.WriteLine($"Serialized: {serialized}");

        // Deserialize back to IEnumerable<Option>
        var deserialized = JsonSerializer.Deserialize<IEnumerable<Option>>(serialized, host.JsonSerializerOptions);
        // Verify the results
        deserialized.Should().NotBeNull();
        var deserializedArray = deserialized.ToArray();
        deserializedArray.Should().HaveCount(2);

        deserializedArray[0].Should().BeOfType<Option<int>>();
        deserializedArray[0].Text.Should().Be("One Hundred");
        ((Option<int>)deserializedArray[0]).Item.Should().Be(100);

        deserializedArray[1].Should().BeOfType<Option<int>>();
        deserializedArray[1].Text.Should().Be("Two Hundred");
        ((Option<int>)deserializedArray[1]).Item.Should().Be(200);
    }

    [HubFact]
    public void SingleOptionSerialization()
    {
        var host = GetHost();

        // Create a single Option<int> object
        var option = new Option<int>(42, "Forty-Two");

        // Serialize the single option
        var serialized = JsonSerializer.Serialize(option, host.JsonSerializerOptions);
        Output.WriteLine($"Single Option Serialized: {serialized}");

        // Deserialize back to Option
        var deserialized = JsonSerializer.Deserialize<Option>(serialized, host.JsonSerializerOptions);

        // Verify the results
        deserialized.Should().NotBeNull();
        deserialized.Should().BeOfType<Option<int>>();
        deserialized.Text.Should().Be("Forty-Two");
        ((Option<int>)deserialized).Item.Should().Be(42);
    }
}
