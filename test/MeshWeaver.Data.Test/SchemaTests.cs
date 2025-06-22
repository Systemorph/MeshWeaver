using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
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
/// Test record with various property types for schema testing
/// </summary>
public record TestSchemaData(
    [property: Required] string RequiredText,
    string OptionalText,
    int Number,
    double DecimalNumber,
    bool Flag,
    DateTime CreatedAt,
    Guid Id,
    TestEnum Status,
    string[] Tags
);

/// <summary>
/// Test enumeration with various status values for schema testing
/// </summary>
public enum TestEnum
{
    /// <summary>
    /// Active status
    /// </summary>
    Active,
    /// <summary>
    /// Inactive status
    /// </summary>
    Inactive,
    /// <summary>
    /// Pending status
    /// </summary>
    Pending
}

/// <summary>
/// Tests for schema generation and validation functionality
/// </summary>
public class SchemaTests(ITestOutputHelper output) : HubTestBase(output)
{
    /// <summary>
    /// Configures the host with test schema data for testing
    /// </summary>
    /// <param name="configuration">The configuration to modify</param>
    /// <returns>The modified configuration</returns>
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .AddData(data =>
                data.AddSource(dataSource =>
                    dataSource.WithType<TestSchemaData>(type => type.WithInitialData(_ => Task.FromResult(new TestSchemaData[]
                        {
                            new TestSchemaData(
                                RequiredText: "Test",
                                OptionalText: "Optional",
                                Number: 42,
                                DecimalNumber: 3.14,
                                Flag: true,
                                CreatedAt: DateTime.UtcNow,
                                Id: Guid.NewGuid(),
                                Status: TestEnum.Active,
                                Tags: new[] { "tag1", "tag2" }
                            )
                        }.AsEnumerable()))
                    )
                )
            );
    }

    /// <summary>
    /// Configures the client to connect to host schema data sources
    /// </summary>
    /// <param name="configuration">The configuration to modify</param>
    /// <returns>The modified configuration</returns>
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration) =>
        base.ConfigureClient(configuration)
            .AddData(data =>
                data.AddHubSource(new HostAddress(), dataSource => dataSource.WithType<TestSchemaData>())
            );

    /// <summary>
    /// Tests that GetSchemaRequest returns a valid JSON schema for complex types with all properties and metadata
    /// </summary>
    [Fact]
    public async Task GetSchemaRequest_ShouldReturnValidJsonSchemaForComplexType()
    {
        // arrange
        var client = GetClient();
        var typeName = nameof(TestSchemaData);

        // act
        var response = await client.AwaitResponse(
            new GetSchemaRequest(typeName),
            o => o.WithTarget(new HostAddress()),
            new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token
        );        // assert
        var schemaResponse = response.Message.Should().BeOfType<SchemaResponse>().Which;
        schemaResponse.Type.Should().Be(typeName);
        schemaResponse.Schema.Should().NotBeNullOrEmpty();

        // Debug output to see what we got
        Output.WriteLine($"Type requested: {typeName}");
        Output.WriteLine($"Schema received: {schemaResponse.Schema}");
        // Skip detailed verification if schema is empty (type not found)
        if (schemaResponse.Schema == "{}")
        {
            Assert.Fail($"Type '{typeName}' was not found in the type registry. Schema was empty.");
        }

        // Verify it's valid JSON
        var schemaJson = JsonDocument.Parse(schemaResponse.Schema);
        schemaJson.RootElement.GetProperty("type").GetString().Should().Be("object");
        schemaJson.RootElement.GetProperty("title").GetString().Should().Be("TestSchemaData");

        // Verify properties exist with correct types
        var properties = schemaJson.RootElement.GetProperty("properties");

        // String properties
        properties.GetProperty("requiredText").GetProperty("type").GetString().Should().Be("string");
        properties.GetProperty("optionalText").GetProperty("type").GetString().Should().Be("string");

        // Numeric properties
        properties.GetProperty("number").GetProperty("type").GetString().Should().Be("integer");
        properties.GetProperty("decimalNumber").GetProperty("type").GetString().Should().Be("number");

        // Boolean property
        properties.GetProperty("flag").GetProperty("type").GetString().Should().Be("boolean");

        // DateTime property
        properties.GetProperty("createdAt").GetProperty("type").GetString().Should().Be("string");
        properties.GetProperty("createdAt").GetProperty("format").GetString().Should().Be("date-time");

        // Guid property
        properties.GetProperty("id").GetProperty("type").GetString().Should().Be("string");
        properties.GetProperty("id").GetProperty("format").GetString().Should().Be("uuid");

        // Enum property
        properties.GetProperty("status").GetProperty("type").GetString().Should().Be("string");
        var enumValues = properties.GetProperty("status").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString()).ToArray();
        enumValues.Should().Contain(["Active", "Inactive", "Pending"]);

        // Array property
        properties.GetProperty("tags").GetProperty("type").GetString().Should().Be("array");

        // Verify required properties
        var required = schemaJson.RootElement.GetProperty("required");
        var requiredArray = required.EnumerateArray().Select(e => e.GetString()).ToArray();
        requiredArray.Should().Contain("requiredText"); // Because it has [Required] attribute
    }

    /// <summary>
    /// Tests that GetSchemaRequest returns an empty schema for unknown/unregistered types
    /// </summary>
    [Fact]
    public async Task GetSchemaRequest_ForUnknownType_ShouldReturnEmptySchema()
    {
        // arrange
        var client = GetClient();
        var unknownTypeName = "UnknownType.That.Does.Not.Exist";

        // act
        var response = await client.AwaitResponse(
            new GetSchemaRequest(unknownTypeName),
            o => o.WithTarget(new ClientAddress()),
            new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token
        );

        // assert
        var schemaResponse = response.Message.Should().BeOfType<SchemaResponse>().Which;
        schemaResponse.Type.Should().Be(unknownTypeName);
        schemaResponse.Schema.Should().Be("{}");
    }

    /// <summary>
    /// Tests that GetDomainTypesRequest returns all available registered types in the data workspace
    /// </summary>
    [Fact]
    public async Task GetDomainTypesRequest_ShouldReturnAvailableTypes()
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
        typesResponse.Types.Should().NotBeEmpty();

        // Should contain our test type
        var testType = typesResponse.Types.FirstOrDefault(t => t.Name.Contains("TestSchemaData"));
        testType.Should().NotBeNull();
        testType.DisplayName.Should().NotBeNullOrEmpty();
        testType.Description.Should().NotBeNullOrEmpty();
        testType.Description.Should().Contain("TestSchemaData");
    }

    /// <summary>
    /// Tests that GetDomainTypesRequest returns types in sorted order for consistent results
    /// </summary>
    [Fact]
    public async Task GetDomainTypesRequest_ShouldReturnSortedTypes()
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
        var types = typesResponse.Types.ToArray();

        // Verify types are sorted by display name
        var sortedTypes = types.OrderBy(t => t.DisplayName ?? t.Name).ToArray();
        for (int i = 0; i < types.Length; i++)
        {
            var actualName = types[i].DisplayName ?? types[i].Name;
            var expectedName = sortedTypes[i].DisplayName ?? sortedTypes[i].Name;
            actualName.Should().Be(expectedName, $"Type at index {i} should be sorted correctly");
        }
    }

    /// <summary>
    /// Tests that schema generation properly handles nullable types and optional properties
    /// </summary>
    [Fact]
    public async Task SchemaGeneration_ShouldHandleNullableTypes()
    {
        // This test verifies that nullable types are handled correctly in schema generation
        // arrange
        var client = GetClient();
        var typeName = typeof(TestSchemaData).FullName;

        // act
        var response = await client.AwaitResponse(
            new GetSchemaRequest(typeName),
            o => o.WithTarget(new ClientAddress()),
            new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token
        );

        // assert
        var schemaResponse = response.Message.Should().BeOfType<SchemaResponse>().Which;
        var schemaJson = JsonDocument.Parse(schemaResponse.Schema);

        // The schema should be generated without errors even for complex types
        var properties = schemaJson.RootElement.GetProperty("properties");
        properties.EnumerateObject().Should().NotBeEmpty();
    }

    /// <summary>
    /// Tests that type descriptions provide useful information about registered types
    /// </summary>
    [Fact]
    public async Task TypeDescription_ShouldProvideUsefulInformation()
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
            // Each type should have a name
            typeDesc.Name.Should().NotBeNullOrEmpty();

            // DisplayName should not be null (can fall back to Name)
            var displayName = typeDesc.DisplayName ?? typeDesc.Name;
            displayName.Should().NotBeNullOrEmpty();

            // Description should provide some context
            typeDesc.Description.Should().NotBeNullOrEmpty();
        }
    }

    /// <summary>
    /// Debug test to show all registered types for troubleshooting purposes
    /// </summary>
    [Fact]
    public async Task DebugAvailableTypes_ShouldShowRegisteredTypes()
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

        Output.WriteLine("Available types in registry:");
        foreach (var type in typesResponse.Types)
        {
            Output.WriteLine($"- Name: {type.Name}, DisplayName: {type.DisplayName}, Description: {type.Description}");
        }

        // This test always passes, it's just for debugging
        typesResponse.Types.Should().NotBeNull();
    }
}
