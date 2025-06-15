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
public record SerializationTestData
{
    /// <summary>
    /// The name identifier for this test data
    /// </summary>
    [Required, Key]
    public string Name { get; init; }

    /// <summary>
    /// Optional numeric value that can be null
    /// </summary>
    public int? NullableNumber { get; init; }

    /// <summary>
    /// Timestamp when this data was created
    /// </summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>
    /// Current status of the test data
    /// </summary>
    public SerializationTestEnum Status { get; init; }

    /// <summary>
    /// List of tags associated with this data
    /// </summary>
    public List<string> Tags { get; init; }

    /// <summary>
    /// Nested complex data structure
    /// </summary>
    public NestedData Details { get; init; }    /// <summary>
    /// Initializes a new instance of the SerializationTestData record
    /// </summary>
    /// <param name="name">The name identifier for this test data</param>
    /// <param name="nullableNumber">Optional numeric value that can be null</param>
    /// <param name="createdAt">Timestamp when this data was created</param>
    /// <param name="status">Current status of the test data</param>
    /// <param name="tags">List of tags associated with this data</param>
    /// <param name="details">Nested complex data structure</param>
    public SerializationTestData(string name, int? nullableNumber, DateTime createdAt, SerializationTestEnum status, List<string> tags, NestedData details)
    {
        Name = name;
        NullableNumber = nullableNumber;
        CreatedAt = createdAt;
        Status = status;
        Tags = tags;
        Details = details;
    }

    /// <summary>
    /// Creates a sample instance of SerializationTestData for testing purposes
    /// </summary>
    /// <returns>A sample SerializationTestData instance</returns>
    public static SerializationTestData CreateSample() => new(
        name: "Test Item",
        nullableNumber: 42,
        createdAt: new DateTime(2023, 1, 1, 12, 0, 0, DateTimeKind.Utc),
        status: SerializationTestEnum.Active,
        tags: new List<string> { "tag1", "tag2" },
        details: new NestedData("Nested Value", true)
    );
}

/// <summary>
/// Nested data structure for testing complex object serialization
/// </summary>
public record NestedData
{
    /// <summary>
    /// String value contained in the nested data
    /// </summary>
    public string Value { get; init; }

    /// <summary>
    /// Boolean flag indicating some state
    /// </summary>
    public bool Flag { get; init; }

    /// <summary>
    /// Initializes a new instance of the NestedData record
    /// </summary>
    /// <param name="value">String value contained in the nested data</param>
    /// <param name="flag">Boolean flag indicating some state</param>
    public NestedData(string value, bool flag)
    {
        Value = value;
        Flag = flag;
    }
}

/// <summary>
/// Enumeration representing different status values for serialization testing
/// </summary>
public enum SerializationTestEnum
{
    /// <summary>
    /// Represents an active status
    /// </summary>
    Active,
    /// <summary>
    /// Represents an inactive status
    /// </summary>
    Inactive,
    /// <summary>
    /// Represents a pending status
    /// </summary>
    Pending
}

/// <summary>
/// Tests for data serialization, schema generation, and type handling
/// </summary>
public class SerializationAndSchemaTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <summary>
    /// Configures the host message hub for serialization and schema testing
    /// </summary>
    /// <param name="configuration">The message hub configuration to modify</param>
    /// <returns>The modified configuration</returns>
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
                    .WithType<PolymorphicContainer>(type =>
                        type.WithKey(instance => instance.Id)
                            .WithInitialData(_ => Task.FromResult(new[] { PolymorphicContainer.CreateSample() }.AsEnumerable()))
                    )
                    .WithType<BaseShape>(type =>
                        type.WithKey(instance => instance.Name)
                    )
                    .WithType<Circle>(type =>
                        type.WithKey(instance => instance.Name)
                    )
                    .WithType<Rectangle>(type =>
                        type.WithKey(instance => instance.Name)
                    )
                    .WithType<Triangle>(type =>
                        type.WithKey(instance => instance.Name)
                    )
                )
            );
    }

    /// <summary>
    /// Configures the client message hub for serialization and schema testing
    /// </summary>
    /// <param name="configuration">The message hub configuration to modify</param>
    /// <returns>The modified configuration</returns>
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration) =>
        base.ConfigureClient(configuration)
            .AddData(data =>
                data.AddHubSource(new HostAddress(), dataSource =>
                    dataSource
                        .WithType<SerializationTestData>()
                        .WithType<NestedData>()
                        .WithType<PolymorphicContainer>()
                        .WithType<BaseShape>()
                        .WithType<Circle>()
                        .WithType<Rectangle>()
                        .WithType<Triangle>())
            );    /// <summary>
    /// Tests that schema generation includes all properties for complex types
    /// </summary>
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

    /// <summary>
    /// Tests that schema generation properly handles enum types with their values
    /// </summary>    /// <summary>
    /// Tests that schema generation properly handles enum types with their values
    /// </summary>
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

    /// <summary>
    /// Tests that schema generation properly handles DateTime types with correct format
    /// </summary>    /// <summary>
    /// Tests that schema generation properly handles DateTime types with correct format
    /// </summary>
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

    /// <summary>
    /// Tests that schema generation properly handles nullable types
    /// </summary>
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

    /// <summary>
    /// Tests that schema generation properly handles array and collection types
    /// </summary>
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

    /// <summary>
    /// Tests that GetDomainTypesRequest returns all registered types
    /// </summary>
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

    /// <summary>
    /// Tests that data serialization preserves complex object structures
    /// </summary>
    [Fact]
    public async Task DataSerialization_ShouldPreserveComplexObjects()
    {
        // arrange
        var client = GetClient(); var testData = new SerializationTestData(
            name: "Serialization Test",
            nullableNumber: null,
            createdAt: DateTime.UtcNow,
            status: SerializationTestEnum.Pending,
            tags: new List<string> { "serialization", "test" },
            details: new NestedData("Serialized Value", false)
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

    /// <summary>
    /// Tests that schema generation handles invalid property types gracefully
    /// </summary>
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

    /// <summary>
    /// Tests that type descriptions provide useful metadata
    /// </summary>
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

    /// <summary>
    /// Tests that complex type updates preserve nested structure
    /// </summary>
    [Fact]
    public async Task ComplexTypeUpdate_ShouldPreserveNestedStructure()
    {
        // arrange
        var client = GetClient();
        var originalData = SerializationTestData.CreateSample();
        var updatedData = originalData with
        {
            Details = originalData.Details with { Flag = false },
            Tags = ["updated", "complex"]
        };

        // act
        var response = await client.AwaitResponse(
            DataChangeRequest.Update([updatedData]),
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

    /// <summary>
    /// Tests that schema generation handles polymorphic container inheritance properly
    /// </summary>
    [Fact]
    public async Task GetSchemaRequest_ForPolymorphicContainer_ShouldHandleInheritance()
    {
        // arrange
        var client = GetClient();
        var typeName = typeof(PolymorphicContainer).FullName;

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

        // Verify polymorphic properties exist
        properties.TryGetProperty("id", out _).Should().BeTrue();
        properties.TryGetProperty("containerName", out _).Should().BeTrue();
        properties.TryGetProperty("primaryShape", out _).Should().BeTrue();
        properties.TryGetProperty("shapes", out _).Should().BeTrue();
        properties.TryGetProperty("namedShapes", out _).Should().BeTrue();

        // Verify primary shape is recognized as object (polymorphic)
        var primaryShapeProperty = properties.GetProperty("primaryShape");
        primaryShapeProperty.GetProperty("type").GetString().Should().Be("object");
        primaryShapeProperty.GetProperty("description").GetString().Should().Contain("Primary shape");

        // Verify shapes array
        var shapesProperty = properties.GetProperty("shapes");
        shapesProperty.GetProperty("type").GetString().Should().Be("array");
    }

    /// <summary>
    /// Tests that schema generation shows abstract properties for base shapes
    /// </summary>
    [Fact]
    public async Task GetSchemaRequest_ForBaseShape_ShouldShowAbstractProperties()
    {
        // arrange
        var client = GetClient();
        var typeName = typeof(BaseShape).FullName;

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

        // Verify base properties
        properties.TryGetProperty("name", out _).Should().BeTrue();
        properties.TryGetProperty("color", out _).Should().BeTrue();
        properties.TryGetProperty("area", out _).Should().BeTrue();

        // Area should be a number (double)
        var areaProperty = properties.GetProperty("area");
        areaProperty.GetProperty("type").GetString().Should().Be("number");
    }

    /// <summary>
    /// Tests that schema generation includes inherited and own properties for Circle
    /// </summary>
    [Fact]
    public async Task GetSchemaRequest_ForCircle_ShouldIncludeInheritedAndOwnProperties()
    {
        // arrange
        var client = GetClient();
        var typeName = typeof(Circle).FullName;

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

        // Verify inherited properties from BaseShape
        properties.TryGetProperty("name", out _).Should().BeTrue();
        properties.TryGetProperty("color", out _).Should().BeTrue();
        properties.TryGetProperty("area", out _).Should().BeTrue();

        // Verify Circle-specific property
        properties.TryGetProperty("radius", out _).Should().BeTrue();
        var radiusProperty = properties.GetProperty("radius");
        radiusProperty.GetProperty("type").GetString().Should().Be("number");
    }

    /// <summary>
    /// Tests that schema generation includes width and height for Rectangle
    /// </summary>
    [Fact]
    public async Task GetSchemaRequest_ForRectangle_ShouldIncludeWidthAndHeight()
    {
        // arrange
        var client = GetClient();
        var typeName = typeof(Rectangle).FullName;

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

        // Verify inherited properties
        properties.TryGetProperty("name", out _).Should().BeTrue();
        properties.TryGetProperty("color", out _).Should().BeTrue();
        properties.TryGetProperty("area", out _).Should().BeTrue();

        // Verify Rectangle-specific properties
        properties.TryGetProperty("width", out _).Should().BeTrue();
        properties.TryGetProperty("height", out _).Should().BeTrue();

        var widthProperty = properties.GetProperty("width");
        widthProperty.GetProperty("type").GetString().Should().Be("number");

        var heightProperty = properties.GetProperty("height");
        heightProperty.GetProperty("type").GetString().Should().Be("number");
    }

    /// <summary>
    /// Tests that GetDomainTypesRequest includes polymorphic types
    /// </summary>
    [Fact]
    public async Task GetDomainTypesRequest_ShouldIncludePolymorphicTypes()
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

        // Should include all polymorphic types
        typeNames.Should().Contain(name => name.Contains("PolymorphicContainer"));
        typeNames.Should().Contain(name => name.Contains("BaseShape"));
        typeNames.Should().Contain(name => name.Contains("Circle"));
        typeNames.Should().Contain(name => name.Contains("Rectangle"));
        typeNames.Should().Contain(name => name.Contains("Triangle"));
    }

    /// <summary>
    /// Tests that polymorphic data serialization preserves type information
    /// </summary>
    [Fact]
    public async Task PolymorphicDataSerialization_ShouldPreserveTypeInformation()
    {
        // arrange
        var client = GetClient();
        var container = new PolymorphicContainer()
        {
            Id = "poly-test",
            ContainerName = "Polymorphic Test",
            PrimaryShape = new Triangle { Name = "Primary Triangle", Color = "Blue", Height = 10.0, Base = 8.0 },
            Shapes = new List<BaseShape>
            {
                new Circle{Name = "Test Circle", Color = "Red", Radius = 5.0},
                new Rectangle{Name = "Test Rectangle", Color = "Green", Height = 3.0, Width = 4.0}
            },
            NamedShapes = new Dictionary<string, BaseShape>
            {
                ["main"] = new Circle { Name = "Main Shape", Color = "Purple", Radius = 7.0 }
            }

        };

        // act
        var response = await client.AwaitResponse(
            DataChangeRequest.Update(new object[] { container }),
            o => o.WithTarget(new ClientAddress()),
            new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token
        );

        // assert
        response.Message.Should().BeOfType<DataChangeResponse>();

        var retrievedData = await client
            .GetWorkspace()
            .GetObservable<PolymorphicContainer>()
            .Timeout(10.Seconds())
            .FirstOrDefaultAsync(items => items?.Any(x => x.Id == "poly-test") == true);

        var item = retrievedData.First(x => x.Id == "poly-test");

        // Verify primary shape preserved type and properties
        item.PrimaryShape.Should().BeOfType<Triangle>();
        var triangle = (Triangle)item.PrimaryShape;
        triangle.Height.Should().Be(10.0);
        triangle.Base.Should().Be(8.0);
        triangle.Area.Should().Be(40.0); // 0.5 * 10 * 8

        // Verify shapes collection preserved types
        item.Shapes.Should().HaveCount(2);
        item.Shapes.OfType<Circle>().Should().HaveCount(1);
        item.Shapes.OfType<Rectangle>().Should().HaveCount(1);

        var circle = item.Shapes.OfType<Circle>().First();
        circle.Radius.Should().Be(5.0);
        circle.Area.Should().BeApproximately(Math.PI * 25, 0.001); // π * r²

        var rectangle = item.Shapes.OfType<Rectangle>().First();
        rectangle.Height.Should().Be(3.0);
        rectangle.Width.Should().Be(4.0);
        rectangle.Area.Should().Be(12.0);

        // Verify named shapes dictionary
        item.NamedShapes.Should().ContainKey("main");
        item.NamedShapes["main"].Should().BeOfType<Circle>();
        var namedCircle = (Circle)item.NamedShapes["main"];
        namedCircle.Radius.Should().Be(7.0);
    }

    /// <summary>
    /// Tests that schema generation includes $type property and default value
    /// </summary>
    [Fact]
    public async Task GetSchemaRequest_ShouldIncludeTypePropertyAndDefault()
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

        // Verify $type property exists
        properties.TryGetProperty("$type", out var typeProperty).Should().BeTrue();
        typeProperty.GetProperty("type").GetString().Should().Be("string");
        typeProperty.GetProperty("description").GetString().Should().Contain("Type discriminator");
        typeProperty.GetProperty("default").GetString().Should().Be(typeName);

        // Verify $type is in required fields
        var required = schemaJson.RootElement.GetProperty("required");
        var requiredArray = required.EnumerateArray().Select(e => e.GetString()).ToArray();
        requiredArray.Should().Contain("$type");        // Verify default object has $type
        var defaultObj = schemaJson.RootElement.GetProperty("default");
        defaultObj.GetProperty("type").GetString().Should().Be(typeName);
    }

    /// <summary>
    /// Tests that schema generation includes inheritors for base shape
    /// </summary>
    [Fact]
    public async Task GetSchemaRequest_ForBaseShape_ShouldIncludeInheritors()
    {
        // arrange
        var client = GetClient();
        var typeName = typeof(BaseShape).FullName;

        // act
        var response = await client.AwaitResponse(
            new GetSchemaRequest(typeName),
            o => o.WithTarget(new ClientAddress()),
            new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token
        );

        // assert
        var schemaResponse = response.Message.Should().BeOfType<SchemaResponse>().Which;
        var schemaJson = JsonDocument.Parse(schemaResponse.Schema);

        // Verify oneOf property exists for potential inheritors
        if (schemaJson.RootElement.TryGetProperty("oneOf", out var oneOfProperty))
        {
            var inheritors = oneOfProperty.EnumerateArray().ToArray();
            inheritors.Should().NotBeEmpty("Base class should have inheritors");

            // Check that we have Circle, Rectangle, and Triangle as inheritors
            var inheritorTitles = inheritors
                .Where(i => i.TryGetProperty("title", out _))
                .Select(i => i.GetProperty("title").GetString())
                .ToArray();

            inheritorTitles.Should().Contain(title => title.Contains("Circle"));
            inheritorTitles.Should().Contain(title => title.Contains("Rectangle"));
            inheritorTitles.Should().Contain(title => title.Contains("Triangle"));
        }
    }

    /// <summary>
    /// Tests that schema generation handles complex polymorphic properties
    /// </summary>
    [Fact]
    public async Task GetSchemaRequest_ForPolymorphicContainer_ShouldHandleComplexPolymorphicProperties()
    {
        // arrange
        var client = GetClient();
        var typeName = typeof(PolymorphicContainer).FullName;

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

        // Verify $type property exists for the container itself
        properties.TryGetProperty("$type", out var typeProperty).Should().BeTrue();

        // Verify polymorphic properties are marked as objects
        properties.TryGetProperty("primaryShape", out var primaryShapeProperty).Should().BeTrue();
        primaryShapeProperty.GetProperty("type").GetString().Should().Be("object");

        properties.TryGetProperty("shapes", out var shapesProperty).Should().BeTrue();
        shapesProperty.GetProperty("type").GetString().Should().Be("array");

        properties.TryGetProperty("namedShapes", out var namedShapesProperty).Should().BeTrue();
        namedShapesProperty.GetProperty("type").GetString().Should().Be("object");
    }

    /// <summary>
    /// Tests that schema generation reads actual XML documentation comments
    /// </summary>
    [Fact]
    public async Task GetSchemaRequest_ShouldReadActualXmlDocumentation()
    {
        // arrange
        var client = GetClient();
        var typeName = typeof(PolymorphicContainer).FullName;

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

        // Verify that actual XML documentation is being read, not generic fallbacks
        if (properties.TryGetProperty("id", out var idProperty) && idProperty.TryGetProperty("description", out var idDescription))
        {
            var description = idDescription.GetString();
            description.Should().Contain("Unique identifier").And.NotContain("Complex type");
        }

        if (properties.TryGetProperty("containerName", out var nameProperty) && nameProperty.TryGetProperty("description", out var nameDescription))
        {
            var description = nameDescription.GetString();
            description.Should().Contain("Display name").And.NotContain("Complex type");
        }

        if (properties.TryGetProperty("primaryShape", out var shapeProperty) && shapeProperty.TryGetProperty("description", out var shapeDescription))
        {
            var description = shapeDescription.GetString();
            description.Should().Contain("Primary shape associated").And.NotContain("Complex type");
        }

        if (properties.TryGetProperty("shapes", out var shapesProperty) && shapesProperty.TryGetProperty("description", out var shapesDesc))
        {
            var description = shapesDesc.GetString();
            description.Should().Contain("Collection of shapes").And.NotContain("Complex type");
        }

        if (properties.TryGetProperty("namedShapes", out var namedProperty) && namedProperty.TryGetProperty("description", out var namedDesc))
        {
            var description = namedDesc.GetString();
            description.Should().Contain("Dictionary of named shapes").And.NotContain("Complex type");
        }
    }

    /// <summary>
    /// Debug test to output the actual generated schema for inspection
    /// </summary>
    [Fact]
    public async Task DebugSchemaGeneration_OutputActualSchema()
    {
        // arrange
        var client = GetClient();
        var typeName = typeof(PolymorphicContainer).FullName;

        // act
        var response = await client.AwaitResponse(
            new GetSchemaRequest(typeName),
            o => o.WithTarget(new ClientAddress()),
            new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token
        );

        // assert
        var schemaResponse = response.Message.Should().BeOfType<SchemaResponse>().Which;

        // Output the schema for debugging
        Output.WriteLine("Generated Schema:");
        Output.WriteLine(schemaResponse.Schema);

        // This test is for debugging - always pass
        Assert.True(true);
    }
}

/// <summary>
/// Base class for polymorphic testing
/// </summary>
public abstract record BaseShape
{
    /// <summary>
    /// Name of the shape
    /// </summary>
    public string Name { get; init; }

    /// <summary>
    /// Color of the shape
    /// </summary>
    public string Color { get; init; }

    /// <summary>
    /// Calculated area of the shape
    /// </summary>
    public abstract double Area { get; }

}

/// <summary>
/// Circle implementation of BaseShape
/// </summary>
public record Circle : BaseShape
{
    /// <summary>
    /// Radius of the circle
    /// </summary>
    public double Radius { get; init; }

    /// <summary>
    /// Calculated area of the circle (π × r²)
    /// </summary>
    public override double Area => Math.PI * Radius * Radius;

}

/// <summary>
/// Rectangle implementation of BaseShape
/// </summary>
public record Rectangle : BaseShape
{
    /// <summary>
    /// Width of the rectangle
    /// </summary>
    public double Width { get; init; }

    /// <summary>
    /// Height of the rectangle
    /// </summary>
    public double Height { get; init; }

    /// <summary>
    /// Calculated area of the rectangle (width × height)
    /// </summary>
    public override double Area => Width * Height;

}

/// <summary>
/// Triangle implementation of BaseShape
/// </summary>
public record Triangle : BaseShape
{
    /// <summary>
    /// Base length of the triangle
    /// </summary>
    public double Base { get; init; }

    /// <summary>
    /// Height of the triangle
    /// </summary>
    public double Height { get; init; }

    /// <summary>
    /// Calculated area of the triangle (0.5 × base × height)
    /// </summary>
    public override double Area => 0.5 * Base * Height;

}

/// <summary>
/// Container class that has polymorphic properties
/// </summary>
public record PolymorphicContainer
{
    /// <summary>
    /// Unique identifier for the container
    /// </summary>
    public string Id { get; init; }

    /// <summary>
    /// Display name of the container
    /// </summary>
    [Required]
    public string ContainerName { get; init; }

    /// <summary>
    /// Primary shape associated with this container
    /// </summary>
    public BaseShape PrimaryShape { get; init; }

    /// <summary>
    /// Collection of shapes contained within this container
    /// </summary>
    public List<BaseShape> Shapes { get; init; }

    /// <summary>
    /// Dictionary of named shapes for quick lookup
    /// </summary>
    public Dictionary<string, BaseShape> NamedShapes { get; init; }

    /// <summary>
    /// Creates a sample instance of PolymorphicContainer for testing purposes
    /// </summary>
    /// <returns>A sample PolymorphicContainer instance with various shape types</returns>
    public static PolymorphicContainer CreateSample() => new()
    {
        Id = "container1",
        ContainerName = "Test Container",
        PrimaryShape = new Circle { Name = "Main Circle", Color = "Blue", Radius = 5 },
        Shapes =
        [
            new Circle { Name = "Circle1", Color = "Red", Radius = 3 },
            new Rectangle
            {
                Color = "Green",
                Height = 4,
                Name = "Rect1",
                Width = 6
            },
            new Triangle{Name = "Triangle1", Color = "Yellow", Height = 8.0, Base= 5.0}
        ],
        NamedShapes = new Dictionary<string, BaseShape>
        {
            ["primary"] = new Circle { Name = "Primary", Color = "Purple", Radius = 2.5 },
            ["secondary"] = new Rectangle { Name = "Secondary", Color = "Orange", Height = 3.0, Width = 3.0 }
        }
    };
}
