using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit;
using Xunit.Abstractions;

namespace MeshWeaver.Data.Test;

/// <summary>
/// Complex test model for serialization testing
/// </summary>
public record SerializationTestData(
    [property: Required] string Name,
    int? NullableNumber,
    DateTime CreatedAt,
    SerializationTestEnum Status,
    List<string> Tags,
    NestedData Details
)
{
    public static SerializationTestData CreateSample() => new(
        Name: "Test Item",
        NullableNumber: 42,
        CreatedAt: new DateTime(2023, 1, 1, 12, 0, 0, DateTimeKind.Utc),
        Status: SerializationTestEnum.Active,
        Tags: new List<string> { "tag1", "tag2" },
        Details: new NestedData("Nested Value", true)
    );
}

public record NestedData(string Value, bool Flag);

public enum SerializationTestEnum
{
    Active,
    Inactive,
    Pending
}

/// <summary>
/// Tests for data serialization, schema generation, and type handling
/// </summary>
public class SerializationAndSchemaTest(ITestOutputHelper output) : HubTestBase(output)
{
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .AddData(data =>
                data.AddSource(dataSource =>
                    dataSource.WithType<SerializationTestData>(type =>
                        type.WithKey(instance => instance.Name)
                            .WithInitialData(_ => Task.FromResult(new[] { SerializationTestData.CreateSample() }.AsEnumerable()))
                    )
                    .WithType<NestedData>(type =>
                        type.WithKey(instance => instance.Value)
                    )
                )
            );
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration) =>
        base.ConfigureClient(configuration)
            .AddData(data =>
                data.AddHubSource(new HostAddress(), dataSource =>
                    dataSource
                        .WithType<SerializationTestData>()
                        .WithType<NestedData>())
            );

    [Fact]
    public async Task GetSchemaRequest_ForComplexType_ShouldIncludeAllProperties()
    {
        // arrange
        var client = GetClient();
        var typeName = typeof(SerializationTestData).FullName;

        // act
        var response = await client.AwaitResponse(
            new GetSchemaRequest(typeName),
            o => o.WithTarget(new ClientAddress()),
            new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token
        );

        // assert
        var schemaResponse = response.Message.Should().BeOfType<SchemaResponse>().Which;
        schemaResponse.Schema.Should().NotBe("{}");

        var schemaJson = JsonDocument.Parse(schemaResponse.Schema);
        var properties = schemaJson.RootElement.GetProperty("properties");

        // Verify all expected properties exist
        properties.TryGetProperty("name", out _).Should().BeTrue();
        properties.TryGetProperty("nullableNumber", out _).Should().BeTrue();
        properties.TryGetProperty("createdAt", out _).Should().BeTrue();
        properties.TryGetProperty("status", out _).Should().BeTrue();
        properties.TryGetProperty("tags", out _).Should().BeTrue();
        properties.TryGetProperty("details", out _).Should().BeTrue();

        // Verify required field
        var required = schemaJson.RootElement.GetProperty("required");
        var requiredArray = required.EnumerateArray().Select(e => e.GetString()).ToArray();
        requiredArray.Should().Contain("name");
    }

    [Fact]
    public async Task GetSchemaRequest_ShouldHandleEnumTypes()
    {
        // arrange
        var client = GetClient();
        var typeName = typeof(SerializationTestData).FullName;

        // act
        var response = await client.AwaitResponse(
            new GetSchemaRequest(typeName),
            o => o.WithTarget(new ClientAddress()),
            new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token
        );

        // assert
        var schemaResponse = response.Message.Should().BeOfType<SchemaResponse>().Which;
        var schemaJson = JsonDocument.Parse(schemaResponse.Schema);
        var properties = schemaJson.RootElement.GetProperty("properties");

        var statusProperty = properties.GetProperty("status");
        statusProperty.GetProperty("type").GetString().Should().Be("string");

        var enumValues = statusProperty.GetProperty("enum")
            .EnumerateArray()
            .Select(e => e.GetString())
            .ToArray();

        enumValues.Should().Contain(["Active", "Inactive", "Pending"]);
    }

    [Fact]
    public async Task GetSchemaRequest_ShouldHandleDateTimeTypes()
    {
        // arrange
        var client = GetClient();
        var typeName = typeof(SerializationTestData).FullName;

        // act
        var response = await client.AwaitResponse(
            new GetSchemaRequest(typeName),
            o => o.WithTarget(new ClientAddress()),
            new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token
        );

        // assert
        var schemaResponse = response.Message.Should().BeOfType<SchemaResponse>().Which;
        var schemaJson = JsonDocument.Parse(schemaResponse.Schema);
        var properties = schemaJson.RootElement.GetProperty("properties");

        var createdAtProperty = properties.GetProperty("createdAt");
        createdAtProperty.GetProperty("type").GetString().Should().Be("string");
        createdAtProperty.GetProperty("format").GetString().Should().Be("date-time");
    }

    [Fact]
    public async Task GetSchemaRequest_ShouldHandleNullableTypes()
    {
        // arrange
        var client = GetClient();
        var typeName = typeof(SerializationTestData).FullName;

        // act
        var response = await client.AwaitResponse(
            new GetSchemaRequest(typeName),
            o => o.WithTarget(new ClientAddress()),
            new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token
        );

        // assert
        var schemaResponse = response.Message.Should().BeOfType<SchemaResponse>().Which;
        var schemaJson = JsonDocument.Parse(schemaResponse.Schema);
        var properties = schemaJson.RootElement.GetProperty("properties");

        // NullableNumber should be present in properties but not in required
        properties.TryGetProperty("nullableNumber", out _).Should().BeTrue();

        var required = schemaJson.RootElement.GetProperty("required");
        var requiredArray = required.EnumerateArray().Select(e => e.GetString()).ToArray();
        requiredArray.Should().NotContain("nullableNumber");
    }

    [Fact]
    public async Task GetSchemaRequest_ShouldHandleArrayTypes()
    {
        // arrange
        var client = GetClient();
        var typeName = typeof(SerializationTestData).FullName;

        // act
        var response = await client.AwaitResponse(
            new GetSchemaRequest(typeName),
            o => o.WithTarget(new ClientAddress()),
            new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token
        );

        // assert
        var schemaResponse = response.Message.Should().BeOfType<SchemaResponse>().Which;
        var schemaJson = JsonDocument.Parse(schemaResponse.Schema);
        var properties = schemaJson.RootElement.GetProperty("properties");

        var tagsProperty = properties.GetProperty("tags");
        tagsProperty.GetProperty("type").GetString().Should().Be("array");
    }

    [Fact]
    public async Task GetDomainTypesRequest_ShouldIncludeAllRegisteredTypes()
    {
        // arrange
        var client = GetClient();

        // act
        var response = await client.AwaitResponse(
            new GetDomainTypesRequest(),
            o => o.WithTarget(new ClientAddress()),
            new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token
        );

        // assert
        var typesResponse = response.Message.Should().BeOfType<DomainTypesResponse>().Which;
        var typeNames = typesResponse.Types.Select(t => t.Name).ToArray();

        // Should include our test types
        typeNames.Should().Contain(name => name.Contains("SerializationTestData"));
        typeNames.Should().Contain(name => name.Contains("NestedData"));
    }

    [Fact]
    public async Task DataSerialization_ShouldPreserveComplexObjects()
    {
        // arrange
        var client = GetClient();
        var testData = new SerializationTestData(
            Name: "Serialization Test",
            NullableNumber: null,
            CreatedAt: DateTime.UtcNow,
            Status: SerializationTestEnum.Pending,
            Tags: new List<string> { "serialization", "test" },
            Details: new NestedData("Serialized Value", false)
        );

        // act
        var response = await client.AwaitResponse(
            DataChangeRequest.Update(new object[] { testData }),
            o => o.WithTarget(new ClientAddress()),
            new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token
        );

        // assert
        response.Message.Should().BeOfType<DataChangeResponse>();

        var retrievedData = await client
            .GetWorkspace()
            .GetObservable<SerializationTestData>()
            .Timeout(10.Seconds())
            .FirstOrDefaultAsync(items => items?.Any(x => x.Name == "Serialization Test") == true);

        var item = retrievedData.First(x => x.Name == "Serialization Test");
        item.NullableNumber.Should().BeNull();
        item.Status.Should().Be(SerializationTestEnum.Pending);
        item.Tags.Should().Equal("serialization", "test");
        item.Details.Value.Should().Be("Serialized Value");
        item.Details.Flag.Should().BeFalse();
    }

    [Fact]
    public async Task SchemaGeneration_WithInvalidPropertyTypes_ShouldHandleGracefully()
    {
        // arrange
        var client = GetClient();
        var typeName = "InvalidType.That.DoesNot.Exist";

        // act
        var response = await client.AwaitResponse(
            new GetSchemaRequest(typeName),
            o => o.WithTarget(new ClientAddress()),
            new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token
        );

        // assert
        var schemaResponse = response.Message.Should().BeOfType<SchemaResponse>().Which;
        schemaResponse.Type.Should().Be(typeName);
        schemaResponse.Schema.Should().Be("{}");
    }

    [Fact]
    public async Task TypeDescription_ShouldProvideUsefulMetadata()
    {
        // arrange
        var client = GetClient();

        // act
        var response = await client.AwaitResponse(
            new GetDomainTypesRequest(),
            o => o.WithTarget(new ClientAddress()),
            new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token
        );

        // assert
        var typesResponse = response.Message.Should().BeOfType<DomainTypesResponse>().Which;

        foreach (var typeDesc in typesResponse.Types)
        {
            typeDesc.Name.Should().NotBeNullOrEmpty();
            (typeDesc.DisplayName ?? typeDesc.Name).Should().NotBeNullOrEmpty();
            typeDesc.Description.Should().NotBeNullOrEmpty();
            typeDesc.Description.Should().Contain("Domain type for");
        }
    }

    [Fact]
    public async Task ComplexTypeUpdate_ShouldPreserveNestedStructure()
    {
        // arrange
        var client = GetClient();
        var originalData = SerializationTestData.CreateSample();
        var updatedData = originalData with
        {
            Details = originalData.Details with { Flag = false },
            Tags = new List<string> { "updated", "complex" }
        };

        // act
        var response = await client.AwaitResponse(
            DataChangeRequest.Update(new object[] { updatedData }),
            o => o.WithTarget(new ClientAddress()),
            new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token
        );

        // assert
        response.Message.Should().BeOfType<DataChangeResponse>();

        var retrievedData = await client
            .GetWorkspace()
            .GetObservable<SerializationTestData>()
            .Timeout(10.Seconds())
            .FirstOrDefaultAsync(items => items?.Any(x => x.Details.Flag == false) == true);

        var item = retrievedData.First(x => x.Name == originalData.Name);
        item.Details.Flag.Should().BeFalse();
        item.Tags.Should().Equal("updated", "complex");
        item.Details.Value.Should().Be("Nested Value"); // Should preserve unchanged nested values
    }
}
