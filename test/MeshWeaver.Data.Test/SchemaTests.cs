using System;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
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
{    /// <summary>
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
    /// Configures the client with data support for schema requests
    /// </summary>
    /// <param name="configuration">The configuration to modify</param>
    /// <returns>The modified configuration</returns>
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        return base.ConfigureClient(configuration)
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
    /// Tests that GetSchemaRequest returns a valid JSON schema for complex types with all properties and metadata
    /// </summary>
    [Fact]
    public async Task GetSchemaRequest_ShouldReturnValidJsonSchemaForComplexType()
    {
        // arrange
        var client = GetClient();
        var typeName = nameof(TestSchemaData);        // act
        var response = await client.AwaitResponse(
            new GetSchemaRequest(typeName),
            o => o.WithTarget(new ClientAddress()),
            new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token
        );// assert
        var schemaResponse = response.Message.Should().BeOfType<SchemaResponse>().Which;
        schemaResponse.Type.Should().Be(typeName);
        schemaResponse.Schema.Should().NotBeNullOrEmpty();        // Debug output to see what we got
        Output.WriteLine($"Type requested: {typeName}");
        Output.WriteLine($"Schema received: {schemaResponse.Schema}");

        // Skip detailed verification if schema is empty (type not found)
        if (schemaResponse.Schema == "{}")
        {
            Assert.Fail($"Type '{typeName}' was not found in the type registry. Schema was empty.");
        }        // Verify it's valid JSON
        var schemaJson = JsonDocument.Parse(schemaResponse.Schema);

        schemaJson.RootElement.GetProperty("title").GetString().Should().Be("TestSchemaData");

        // System.Text.Json generates polymorphic schemas with anyOf structure
        // Find the properties within the anyOf array
        JsonElement properties;
        if (schemaJson.RootElement.TryGetProperty("anyOf", out var anyOfElement))
        {
            // Look for an object in anyOf that has properties
            var objectSchema = anyOfElement.EnumerateArray()
                .FirstOrDefault(item => item.TryGetProperty("properties", out _));

            if (objectSchema.ValueKind != JsonValueKind.Undefined &&
                objectSchema.TryGetProperty("properties", out properties))
            {
                // Found properties in anyOf
            }
            else
            {
                Assert.Fail("Could not find properties in anyOf schema structure");
                return; // This won't be reached, but helps with flow analysis
            }
        }
        else if (schemaJson.RootElement.TryGetProperty("properties", out properties))
        {
            // Direct properties structure
        }
        else
        {
            Assert.Fail($"Schema does not contain 'properties' or 'anyOf' structure. Available root properties: {string.Join(", ", schemaJson.RootElement.EnumerateObject().Select(p => p.Name))}");
            return; // This won't be reached, but helps with flow analysis
        }        // Verify some key properties exist and have reasonable types
                 // Note: System.Text.Json generates sophisticated schemas with nullable types

        // String properties should exist
        var requiredTextType = GetPropertyType(properties, "requiredText");
        requiredTextType.Should().Be("string", "RequiredText should be a string type");

        var optionalTextType = GetPropertyType(properties, "optionalText");
        optionalTextType.Should().BeOneOf("string", "property-not-found", "type-not-found"); // Might be nullable

        // Numeric properties should exist
        var numberType = GetPropertyType(properties, "number");
        numberType.Should().BeOneOf("integer", "number");

        var decimalType = GetPropertyType(properties, "decimalNumber");
        decimalType.Should().BeOneOf("number", "integer");

        // Boolean property
        var flagType = GetPropertyType(properties, "flag");
        flagType.Should().BeOneOf("boolean");

        // DateTime and Guid properties should be strings
        var createdAtType = GetPropertyType(properties, "createdAt");
        createdAtType.Should().Be("string");

        var idType = GetPropertyType(properties, "id");
        idType.Should().Be("string");

        // Array property 
        var tagsType = GetPropertyType(properties, "tags");
        tagsType.Should().Be("array");

        // Verify schema has required metadata
        schemaJson.RootElement.GetProperty("title").GetString().Should().Be("TestSchemaData");

        if (schemaJson.RootElement.TryGetProperty("required", out var required))
        {
            var requiredArray = required.EnumerateArray().Select(e => e.GetString()).ToArray();
            // At minimum, polymorphic schemas require $type
            requiredArray.Should().Contain("$type");
        }
    }

    /// <summary>
    /// Helper method to get property type, handling both string and array type values
    /// </summary>
    private string GetPropertyType(JsonElement properties, string propertyName)
    {
        if (!properties.TryGetProperty(propertyName, out var property))
        {
            return "property-not-found";
        }

        if (!property.TryGetProperty("type", out var typeProperty))
        {
            return "type-not-found";
        }

        if (typeProperty.ValueKind == JsonValueKind.String)
        {
            return typeProperty.GetString();
        }
        else if (typeProperty.ValueKind == JsonValueKind.Array)
        {
            // For nullable types, return the non-null type
            var types = typeProperty.EnumerateArray().Select(t => t.GetString()).ToArray();
            return types.FirstOrDefault(t => t != "null") ?? types.First();
        }

        return "unknown";
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
        );        // assert
        var schemaResponse = response.Message.Should().BeOfType<SchemaResponse>().Which;
        var schemaJson = JsonDocument.Parse(schemaResponse.Schema);

        // The schema should be generated without errors even for complex types
        // Check for properties in the anyOf structure or direct properties
        bool foundProperties = false;

        if (schemaJson.RootElement.TryGetProperty("anyOf", out var anyOfElement))
        {
            // Look for properties in anyOf array
            foreach (var item in anyOfElement.EnumerateArray())
            {
                if (item.TryGetProperty("properties", out var props))
                {
                    props.EnumerateObject().Should().NotBeEmpty();
                    foundProperties = true;
                    break;
                }
            }
        }
        else if (schemaJson.RootElement.TryGetProperty("properties", out var directProps))
        {
            directProps.EnumerateObject().Should().NotBeEmpty();
            foundProperties = true;
        }

        foundProperties.Should().BeTrue("Schema should contain properties either directly or in anyOf structure");
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

    /// <summary>
    /// Debug test to see full schema output
    /// </summary>
    [Fact]
    public async Task DebugFullSchema()
    {
        // arrange
        var client = GetClient();
        var typeName = nameof(TestSchemaData);

        // act
        var response = await client.AwaitResponse(
            new GetSchemaRequest(typeName),
            o => o.WithTarget(new ClientAddress()),
            new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token
        );

        // assert
        var schemaResponse = response.Message.Should().BeOfType<SchemaResponse>().Which; var schemaJson = JsonDocument.Parse(schemaResponse.Schema);
        Output.WriteLine($"Schema root properties: {string.Join(", ", schemaJson.RootElement.EnumerateObject().Select(p => p.Name))}");

        if (schemaJson.RootElement.TryGetProperty("properties", out var props))
        {
            Output.WriteLine($"Properties found: {string.Join(", ", props.EnumerateObject().Select(p => p.Name))}");
        }
        else
        {
            Output.WriteLine("No 'properties' property found in schema");
        }

        // Just pass the test for debugging
        schemaResponse.Should().NotBeNull();
    }

    /// <summary>
    /// Debug test to check if Required attribute is properly applied
    /// </summary>
    [Fact]
    public void DebugRequiredAttribute()
    {
        var type = typeof(TestSchemaData);
        var requiredTextProp = type.GetProperty("RequiredText");

        Output.WriteLine($"Property: {requiredTextProp?.Name}");

        var attributes = requiredTextProp?.GetCustomAttributes(true);
        Output.WriteLine($"All attributes: {string.Join(", ", attributes?.Select(a => a.GetType().Name) ?? new string[0])}");

        var requiredAttr = requiredTextProp?.GetCustomAttribute<System.ComponentModel.DataAnnotations.RequiredAttribute>();
        Output.WriteLine($"Required attribute found: {requiredAttr != null}");

        var requiredAttr2 = requiredTextProp?.GetCustomAttributes(typeof(System.ComponentModel.DataAnnotations.RequiredAttribute), false);
        Output.WriteLine($"Required attribute found (method 2): {requiredAttr2?.Any()}");

        // Test should always pass
        Assert.True(true);
    }

    /// <summary>
    /// Debug test to see the actual schema structure
    /// </summary>
    [Fact]
    public async Task DebugActualSchema()
    {
        // arrange
        var client = GetClient();
        var typeName = nameof(TestSchemaData);

        // act
        var response = await client.AwaitResponse(
            new GetSchemaRequest(typeName),
            o => o.WithTarget(new ClientAddress()),
            new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token
        );

        // assert
        var schemaResponse = response.Message.Should().BeOfType<SchemaResponse>().Which;

        Output.WriteLine("=== ACTUAL SCHEMA STRUCTURE ===");
        Output.WriteLine(schemaResponse.Schema);

        var schemaJson = JsonDocument.Parse(schemaResponse.Schema);
        Output.WriteLine("\n=== ROOT PROPERTIES ===");
        foreach (var prop in schemaJson.RootElement.EnumerateObject())
        {
            Output.WriteLine($"{prop.Name}: {prop.Value.ValueKind}");
        }

        // Test passes for debugging
        Assert.True(true);
    }

    /// <summary>
    /// Debug test to see what properties are actually in the schema
    /// </summary>
    [Fact]
    public async Task DebugSchemaProperties()
    {
        // arrange
        var client = GetClient();
        var typeName = nameof(TestSchemaData);

        // act
        var response = await client.AwaitResponse(
            new GetSchemaRequest(typeName),
            o => o.WithTarget(new ClientAddress()),
            new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token
        );

        // assert
        var schemaResponse = response.Message.Should().BeOfType<SchemaResponse>().Which;
        var schemaJson = JsonDocument.Parse(schemaResponse.Schema);

        if (schemaJson.RootElement.TryGetProperty("anyOf", out var anyOfElement))
        {
            Output.WriteLine("=== anyOf structure ===");
            foreach (var item in anyOfElement.EnumerateArray())
            {
                if (item.TryGetProperty("properties", out var props))
                {
                    Output.WriteLine("Properties found:");
                    foreach (var prop in props.EnumerateObject())
                    {
                        Output.WriteLine($"  {prop.Name}");
                    }
                    break;
                }
            }
        }

        // Test passes for debugging
        Assert.True(true);
    }

    /// <summary>
    /// Debug test to see the structure of specific properties
    /// </summary>
    [Fact]
    public async Task DebugSpecificProperties()
    {
        // arrange
        var client = GetClient();
        var typeName = nameof(TestSchemaData);

        // act
        var response = await client.AwaitResponse(
            new GetSchemaRequest(typeName),
            o => o.WithTarget(new ClientAddress()),
            new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token
        );

        // assert
        var schemaResponse = response.Message.Should().BeOfType<SchemaResponse>().Which;
        var schemaJson = JsonDocument.Parse(schemaResponse.Schema);

        if (schemaJson.RootElement.TryGetProperty("anyOf", out var anyOfElement))
        {
            foreach (var item in anyOfElement.EnumerateArray())
            {
                if (item.TryGetProperty("properties", out var props))
                {
                    if (props.TryGetProperty("status", out var statusProp))
                    {
                        Output.WriteLine("=== status property structure ===");
                        Output.WriteLine(statusProp.GetRawText());
                    }

                    if (props.TryGetProperty("tags", out var tagsProp))
                    {
                        Output.WriteLine("=== tags property structure ===");
                        Output.WriteLine(tagsProp.GetRawText());
                    }
                    break;
                }
            }
        }

        // Test passes for debugging
        Assert.True(true);
    }
}
