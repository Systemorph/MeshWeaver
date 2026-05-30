using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;
using Xunit;

using System.Reactive.Threading.Tasks;
namespace MeshWeaver.Data.Test;

/// <summary>
/// Test data record for data plugin testing
/// </summary>
/// <param name="Id">Unique identifier for the data item</param>
/// <param name="Text">Text content of the data item</param>
public record MyData(
    string Id,
    [property: Required] string Text)
{
    /// <summary>
    /// Initial test data collection for seeding tests
    /// </summary>
    public static MyData[] InitialData = [new("1", "A"), new("2", "B")];

}

/// <summary>
/// Tests for data plugin functionality including CRUD operations, schema generation, and data synchronization
/// </summary>
/// <param name="output">Test output helper for logging</param>
public class DataTest(ITestOutputHelper output) : HubTestBase(output)
{

    private ImmutableDictionary<object, object> storage = ImmutableDictionary<object, object>.Empty;

    /// <summary>
    /// Configures the host message hub for data plugin testing
    /// </summary>
    /// <param name="configuration">The message hub configuration to modify</param>
    /// <returns>The modified configuration</returns>
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .AddData(data =>
                data.AddSource(
                    dataSource =>
                        dataSource.WithType<MyData>(type =>
                            type.WithKey(instance => instance.Id)
                                .WithInitialData(InitializeMyData)
                                .WithUpdate(SaveMyData)
                        )
                )
            );
    }

    /// <summary>
    /// Configures the client to connect to host data sources for MyData type
    /// </summary>
    /// <param name="configuration">The configuration to modify</param>
    /// <returns>The modified configuration</returns>
    protected override MessageHubConfiguration ConfigureClient(
        MessageHubConfiguration configuration
    ) =>
        base.ConfigureClient(configuration)
            .AddData(data =>
                data.AddHubSource(CreateHostAddress(), dataSource => dataSource.WithType<MyData>())
            );

    /// <summary>
    /// Tests basic data plugin initialization and data loading
    /// </summary>
    [Fact]
    public void InitializeTest()
    {
        var workspace = GetHost().GetWorkspace();
        var response = workspace
            .GetObservable<MyData>()
            .Should().Within(10.Seconds())
            .Emit();
        response.Should().BeEquivalentTo(MyData.InitialData, GetHost().JsonSerializerOptions);
    }


    /// <summary>
    /// Tests data update operations through the data plugin
    /// </summary>
    [Fact]
    public void Update()
    {
        // arrange
        var client = GetClient();
        var updateItems = new object[] { new MyData("1", "AAA"), new MyData("3", "CCC"), };

        var clientWorkspace = client.GetWorkspace();
        var data = clientWorkspace
                .GetObservable<MyData>()
                .Should().Within(10.Seconds())
                .Emit()!
            .OrderBy(a => a.Id)
            .ToArray();

        data.Should().HaveCount(2);

        // act
        var updateResponse = client.Observe(DataChangeRequest.Update(updateItems), o => o.WithTarget(CreateClientAddress()))
            .Should().Within(3.Seconds()).Emit();

        // asserts
        updateResponse.Message.Should().BeOfType<DataChangeResponse>();
        var expectedItems = new MyData[] { new("1", "AAA"), new("2", "B"), new("3", "CCC") };

        data = clientWorkspace
                .GetObservable<MyData>()
                .Should().Within(10.Seconds())
                .Match(x => x.Count == 3)!
            .OrderBy(a => a.Id)
            .ToArray();

        data.ToArray().Should().BeEquivalentTo(expectedItems, GetHost().JsonSerializerOptions);
        data = GetHost()
                .GetWorkspace()
                .GetObservable<MyData>()
                .Should().Within(10.Seconds())
                .Match(x => x.Count == 3)!
            .OrderBy(a => a.Id)
            .ToArray();

        data.ToArray().Should().BeEquivalentTo(expectedItems, GetHost().JsonSerializerOptions);
        // The host workspace surfacing count == 3 means the committed update
        // (which runs SaveMyData → storage) has already landed — no Task.Delay needed.
        storage.Values.Cast<MyData>().OrderBy(x => x.Id).Should().BeEquivalentTo(expectedItems, GetHost().JsonSerializerOptions);
    }

    /// <summary>
    /// Tests data deletion operations through the data plugin
    /// </summary>
    [Fact]
    public void Delete()
    {
        // arrange
        var client = GetClient();

        var data = GetHost()
            .GetWorkspace()
            .GetObservable<MyData>()
            .Should().Within(10.Seconds())
            .Emit();
        data.Should().BeEquivalentTo(MyData.InitialData, GetHost().JsonSerializerOptions);

        var toBeDeleted = data.Take(1).ToArray();
        var expectedItems = data.Skip(1).ToArray();
        // act
        var deleteResponse = client.Observe(DataChangeRequest.Delete(toBeDeleted, "TestUser"), o => o.WithTarget(CreateClientAddress()))
            .Should().Within(10.Seconds()).Emit();
        deleteResponse.Message.Status.Should().Be(DataChangeStatus.Committed);

        // asserts — verify through host workspace (client filters out own changes via echo prevention)
        data = GetHost()
            .GetWorkspace()
            .GetObservable<MyData>()
            .Should().Within(10.Seconds())
            .Match(i => i.Count == 1);
        data.Should().BeEquivalentTo(expectedItems, GetHost().JsonSerializerOptions);
        // count == 1 surfacing means the commit (SaveMyData → storage) already ran.
        storage.Values.Should().BeEquivalentTo(expectedItems, GetHost().JsonSerializerOptions);
    }

    /// <summary>
    /// Text change constant for testing purposes
    /// </summary>
    public const string TextChange = nameof(TextChange);

    /// <summary>
    /// Local import request record for activity testing
    /// </summary>
    public record LocalImportRequest : IRequest<ActivityLog>;

    /// <summary>
    /// Tests workspace variable usage functionality
    /// </summary>
    [Fact]
    public void CheckUsagesFromWorkspaceVariable()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var myInstance = workspace
            .GetObservable<MyData>("1")
            .Should().Within(10.Seconds())
            .Match(i => i is not null);
        myInstance!.Text.Should().NotBe(TextChange);

        // act
        myInstance = myInstance with
        {
            Text = TextChange
        };
        client.Observe(DataChangeRequest.Update([myInstance]), o => o.WithTarget(CreateClientAddress()))
            .Should().Within(10.Seconds()).Emit();

        var hostWorkspace = GetHost().GetWorkspace();

        var instance = hostWorkspace
            .GetObservable<MyData>("1")
            .Should().Within(10.Seconds())
            .Match(i => i?.Text == TextChange);
        instance.Should().NotBeNull();
        // host observing the changed text means the commit (SaveMyData → storage) already ran.
        storage.Values.Should().Contain(i => ((MyData)i).Text == TextChange);
    }

    /// <summary>
    /// Initializes test data for MyData type
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation</param>
    /// <returns>A task that represents the asynchronous operation, containing the initialized data</returns>
    private Task<IEnumerable<MyData>> InitializeMyData(CancellationToken cancellationToken)
    {
        storage = MyData.InitialData.ToImmutableDictionary(x => (object)x.Id, x => (object)x);
        return Task.FromResult<IEnumerable<MyData>>(MyData.InitialData);
    }

    /// <summary>
    /// Saves updated MyData instances to storage
    /// </summary>
    /// <param name="instanceCollection">The collection of instances to save</param>
    /// <returns>The saved instance collection</returns>
    private InstanceCollection SaveMyData(InstanceCollection instanceCollection)
    {
        // Simple file logging to verify if this method is called
        var logPath = "/tmp/savedata.log";
        var itemsInfo = string.Join(", ", instanceCollection.Instances.Select(kvp => $"{kvp.Key}:{((MyData)kvp.Value).Text}"));
        File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} SaveMyData called with {instanceCollection.Instances.Count} instances: {itemsInfo}\n");

        storage = instanceCollection.Instances;
        return instanceCollection;
    }
    /// <summary>
    /// Tests validation failure scenarios and error handling
    /// </summary>
    [Fact]
    public void ValidationFailure()
    {
        // arrange
        var client = GetClient();
        var updateItems = new object[] { new MyData("5", null!) };

        // act
        var updateResponse = client.Observe(DataChangeRequest.Update(updateItems), o => o.WithTarget(CreateClientAddress()))
            .Should().Within(3.Seconds()).Emit();

        // asserts
        var response = updateResponse.Message.Should().BeOfType<DataChangeResponse>().Which;
        response.Status.Should().Be(DataChangeStatus.Failed);
        var log = response.Log;
        log.Status.Should().Be(ActivityStatus.Failed);
        var members = log
            .Messages
            .Where(m => m.LogLevel > LogLevel.Information)
            .Should().ContainSingle()
            .Which.Scopes!
            .FirstOrDefault(s => s.Key == "members");
        members.Value.Should().BeOfType<string[]>().Which.Single().Should().Be("Text");
    }

    /// <summary>
    /// Tests collection reference reduction operations
    /// </summary>
    [Fact]
    public void ReduceCollectionReference()
    {
        var host = GetHost();
        var collection = host.GetWorkspace().GetStream(new CollectionReference(nameof(MyData)))!
            .Select(c => c.Value!.Instances.Values)
            .Should().Emit();

        collection.Should().BeEquivalentTo(MyData.InitialData, GetHost().JsonSerializerOptions);
    }

    [Fact]
    public void ReduceSchemaReference()
    {
        var host = GetHost();
        var result = host.GetWorkspace()
            .GetStream(new SchemaReference(nameof(MyData)))!
            .Select(c => c.Value)
            .Should().Within(10.Seconds())
            .Emit();

        var schemaInfo = result.Should().BeOfType<SchemaInfo>().Which;
        schemaInfo.Type.Should().Be(nameof(MyData));
        schemaInfo.Schema.Should().NotBeNullOrEmpty();
        schemaInfo.Schema.Should().NotBe("{}");

        var schemaJson = JsonDocument.Parse(schemaInfo.Schema);
        GetPropertyType(schemaJson.RootElement).Should().Contain("object");
    }

    [Fact]
    public void ReduceSchemaReference_NullType_ReturnsDefaultSchema()
    {
        var host = GetHost();
        var result = host.GetWorkspace()
            .GetStream(new SchemaReference(null))!
            .Select(c => c.Value)
            .Should().Within(10.Seconds())
            .Emit();

        var schemaInfo = result.Should().BeOfType<SchemaInfo>().Which;
        schemaInfo.Type.Should().NotBeNullOrEmpty();
        schemaInfo.Schema.Should().NotBeNullOrEmpty();
        schemaInfo.Schema.Should().NotBe("{}");
    }

    [Fact]
    public void ReduceSchemaReference_UnknownType_ReturnsEmptySchema()
    {
        var host = GetHost();
        var result = host.GetWorkspace()
            .GetStream(new SchemaReference("NonExistentType"))!
            .Select(c => c.Value)
            .Should().Within(10.Seconds())
            .Emit();

        var schemaInfo = result.Should().BeOfType<SchemaInfo>().Which;
        schemaInfo.Type.Should().Be("NonExistentType");
        schemaInfo.Schema.Should().Be("{}");
    }

    [Fact]
    public void ReduceDataModelReference()
    {
        var host = GetHost();
        var result = host.GetWorkspace()
            .GetStream(new DataModelReference())!
            .Select(c => c.Value)
            .Should().Within(10.Seconds())
            .Emit();

        result.Should().BeAssignableTo<IEnumerable<TypeDescription>>();
        var types = ((IEnumerable<TypeDescription>)result!).ToArray();
        types.Should().NotBeEmpty();
        types.Should().Contain(t => t.Name.Contains("MyData"));
    }

    [Fact]
    public void ReduceNodeTypeReference()
    {
        var host = GetHost();
        var stream = host.GetWorkspace()
            .GetStream(new NodeTypeReference(), x => x.ReturnNullWhenNotPresent());
        ((object?)stream).Should().NotBeNull();
    }

    /// <summary>
    /// Tests that GetSchemaRequest returns a valid JSON schema for MyData type
    /// </summary>
    [Fact]
    public void GetSchemaRequest_ShouldReturnValidJsonSchema()
    {
        // arrange
        var client = GetClient();
        var typeName = typeof(MyData).FullName!;

        // act
        var response = client.Observe(new GetDataRequest(new SchemaReference(typeName)), o => o.WithTarget(CreateClientAddress()))
            .Should().Within(10.Seconds()).Emit();

        // assert
        response.Message.Should().BeOfType<GetDataResponse>();
        var schemaInfo = response.Message.Data.Should().BeOfType<SchemaInfo>().Which;
        schemaInfo.Type.Should().Be(typeName);
        schemaInfo.Schema.Should().NotBeNullOrEmpty();
        schemaInfo.Schema.Should().NotBe("{}");

        // Verify it's valid JSON
        var schemaJson = JsonDocument.Parse(schemaInfo.Schema);
        schemaJson.Should().NotBeNull();

        // Verify it represents an object type (most likely)
        GetPropertyType(schemaJson.RootElement).Should().Contain("object");

        // Verify that it has some meaningful content - not just an empty object
        schemaJson.RootElement.EnumerateObject().Should().NotBeEmpty();
    }

    /// <summary>
    /// Tests that SchemaReference returns empty schema for unknown types
    /// </summary>
    [Fact]
    public void SchemaReference_ForUnknownType_ShouldReturnEmptySchema()
    {
        // arrange
        var client = GetClient();
        var unknownTypeName = "UnknownType";

        // act
        var response = client.Observe(new GetDataRequest(new SchemaReference(unknownTypeName)), o => o.WithTarget(CreateClientAddress()))
            .Should().Within(10.Seconds()).Emit();

        // assert
        response.Message.Should().BeOfType<GetDataResponse>();
        var schemaInfo = response.Message.Data.Should().BeOfType<SchemaInfo>().Which;
        schemaInfo.Type.Should().Be(unknownTypeName);
        schemaInfo.Schema.Should().Be("{}");
    }

    /// <summary>
    /// Tests that GetDomainTypesRequest returns all available registered types
    /// </summary>
    [Fact]
    public void GetDomainTypesRequest_ShouldReturnAvailableTypes()
    {
        // arrange
        var client = GetClient();

        // act
        var response = client.Observe(new GetDomainTypesRequest(), o => o.WithTarget(CreateClientAddress()))
            .Should().Within(10.Seconds()).Emit();

        // assert
        var typesResponse = response.Message.Should().BeOfType<DomainTypesResponse>().Which;
        typesResponse.Types.Should().NotBeEmpty();

        // Should contain our test type
        var myDataType = typesResponse.Types.FirstOrDefault(t => t.Name.Contains("MyData"));
        myDataType.Should().NotBeNull();
        myDataType!.DisplayName.Should().NotBeNullOrEmpty();
        myDataType.Description.Should().NotBeNullOrEmpty();
    }

    /// <summary>
    /// Tests that GetDomainTypesRequest returns types in sorted order
    /// </summary>
    [Fact]
    public void GetDomainTypesRequest_ShouldReturnSortedTypes()
    {
        // arrange
        var client = GetClient();

        // act
        var response = client.Observe(new GetDomainTypesRequest(), o => o.WithTarget(CreateClientAddress()))
            .Should().Within(10.Seconds()).Emit();
        // assert
        var typesResponse = response.Message.Should().BeOfType<DomainTypesResponse>().Which;
        var types = typesResponse.Types.ToArray();

        // Verify types are sorted by display name
        var sortedTypes = types.OrderBy(t => t.DisplayName).ToArray();
        types.Select(t => t.DisplayName).Should().Equal(sortedTypes.Select(t => t.DisplayName));
    }

    /// <summary>
    /// Tests update operations with invalid data and validation error handling
    /// </summary>
    [Fact]
    public void UpdateWithInvalidData_ShouldReturnValidationErrors()
    {
        // arrange
        var client = GetClient();
        var invalidItems = new object[]
        {
            new MyData("1", null!), // Required field is null
            new MyData("", "Valid text") // Empty ID
        };

        // act
        var updateResponse = client.Observe(DataChangeRequest.Update(invalidItems), o => o.WithTarget(CreateClientAddress()))
            .Should().Within(10.Seconds()).Emit();

        // assert
        var response = updateResponse.Message.Should().BeOfType<DataChangeResponse>().Which;
        response.Status.Should().Be(DataChangeStatus.Failed);
        response.Log.Status.Should().Be(ActivityStatus.Failed);
        response.Log.Messages.Should().NotBeEmpty();
    }

    /// <summary>
    /// Tests updating non-existent records creates new records
    /// </summary>
    [Fact]
    public void UpdateNonExistentRecord_ShouldCreateNewRecord()
    {
        // arrange
        var client = GetClient();
        var newItem = new MyData("999", "New Item");

        // Verify item doesn't exist initially
        var initialData = GetHost()
            .GetWorkspace()
            .GetObservable<MyData>()
            .Should().Within(10.Seconds())
            .Emit();
        initialData.Should().NotContain(x => x.Id == "999");

        // act
        var updateResponse = client.Observe(DataChangeRequest.Update(new object[] { newItem }), o => o.WithTarget(CreateClientAddress()))
            .Should().Within(10.Seconds()).Emit();

        // assert
        updateResponse.Message.Should().BeOfType<DataChangeResponse>();
        var updatedData = GetHost()
            .GetWorkspace()
            .GetObservable<MyData>()
            .Should().Within(10.Seconds())
            .Match(x => x.Any(item => item.Id == "999"));
        updatedData.Should().Contain(x => x.Id == "999" && x.Text == "New Item");
    }

    /// <summary>
    /// Tests handling of multiple simultaneous update operations
    /// </summary>
    [Fact]
    public void MultipleSimultaneousUpdates_ShouldHandleCorrectly()
    {
        // arrange
        var client = GetClient();
        var updates1 = new object[] { new MyData("10", "Update 1") };
        var updates2 = new object[] { new MyData("11", "Update 2") };
        var updates3 = new object[] { new MyData("12", "Update 3") };

        // act - send multiple updates simultaneously (merged so all three
        // subscriptions fire concurrently; buffer until all three responses land)
        var responses = Observable.Merge(
                client.Observe(DataChangeRequest.Update(updates1), o => o.WithTarget(CreateClientAddress())),
                client.Observe(DataChangeRequest.Update(updates2), o => o.WithTarget(CreateClientAddress())),
                client.Observe(DataChangeRequest.Update(updates3), o => o.WithTarget(CreateClientAddress())))
            .Take(3)
            .ToList()
            .Should().Within(10.Seconds()).Emit();

        // assert
        responses.Should().AllSatisfy(response =>
            response.Message.Should().BeOfType<DataChangeResponse>());

        var finalData = GetHost()
            .GetWorkspace()
            .GetObservable<MyData>()
            .Should().Within(10.Seconds())
            .Match(x => x.Count >= 5); // Initial 2 + 3 new items

        finalData.Should().Contain(x => x.Id == "10" && x.Text == "Update 1");
        finalData.Should().Contain(x => x.Id == "11" && x.Text == "Update 2");
        finalData.Should().Contain(x => x.Id == "12" && x.Text == "Update 3");
    }

    /// <summary>
    /// Tests schema request behavior with null or empty type parameters - returns default type schema
    /// </summary>
    [Fact]
    public void SchemaReference_WithNullOrEmptyType_ShouldReturnDefaultTypeSchema()
    {
        // arrange
        var client = GetClient();

        // act & assert for null - with new implementation, null type returns the first registered type's schema
        var responseNull = client.Observe(new GetDataRequest(new SchemaReference(null)), o => o.WithTarget(CreateClientAddress()))
            .Should().Within(10.Seconds()).Emit();

        responseNull.Message.Should().BeOfType<GetDataResponse>();
        var schemaInfoNull = responseNull.Message.Data.Should().BeOfType<SchemaInfo>().Which;
        schemaInfoNull.Schema.Should().NotBeNullOrEmpty();

        // act & assert for empty string
        var responseEmpty = client.Observe(new GetDataRequest(new SchemaReference("")), o => o.WithTarget(CreateClientAddress()))
            .Should().Within(10.Seconds()).Emit();

        responseEmpty.Message.Should().BeOfType<GetDataResponse>();
        var schemaInfoEmpty = responseEmpty.Message.Data.Should().BeOfType<SchemaInfo>().Which;
        schemaInfoEmpty.Schema.Should().NotBeNullOrEmpty();
    }

    /// <summary>
    /// Tests data synchronization consistency between client and host
    /// </summary>
    [Fact]
    public void DataSynchronization_BetweenClientAndHost_ShouldStayConsistent()
    {
        // arrange
        var client = GetClient();
        var host = GetHost();
        var updateItem = new MyData("1", "Updated Text");

        // Get initial state
        var initialClientData = client
            .GetWorkspace()
            .GetObservable<MyData>()
            .Should().Within(10.Seconds())
            .Emit();

        var initialHostData = host
            .GetWorkspace()
            .GetObservable<MyData>()
            .Should().Within(10.Seconds())
            .Emit();

        initialClientData.Should().BeEquivalentTo(initialHostData, GetHost().JsonSerializerOptions);

        // act - update from client
        client.Observe(DataChangeRequest.Update([updateItem]), o => o.WithTarget(CreateClientAddress()))
            .Should().Within(10.Seconds()).Emit();

        // assert - both client and host should have the same updated data
        var updatedClientData = client
            .GetWorkspace()
            .GetObservable<MyData>()
            .Should().Within(10.Seconds())
            .Match(x => x.Any(item => item.Text == "Updated Text"));

        var updatedHostData = host
            .GetWorkspace()
            .GetObservable<MyData>()
            .Should().Within(10.Seconds())
            .Match(x => x.Any(item => item.Text == "Updated Text"));

        updatedClientData.Should().BeEquivalentTo(updatedHostData, GetHost().JsonSerializerOptions);
        updatedClientData.Should().Contain(x => x.Id == "1" && x.Text == "Updated Text");
    }

    /// <summary>
    /// Tests collection reference operations with specific data types
    /// </summary>
    [Fact]
    public void CollectionReference_WithSpecificType_ShouldReturnCorrectData()
    {
        // arrange
        var host = GetHost();
        var collectionRef = new CollectionReference(nameof(MyData));

        // act
        var stream = host.GetWorkspace().GetStream(collectionRef);
        var collection = stream!
            .Select(c => c.Value!.Instances.Values.Cast<MyData>().ToArray())
            .Should().Within(10.Seconds())
            .Emit();

        // assert
        collection.Should().BeEquivalentTo(MyData.InitialData, GetHost().JsonSerializerOptions);
        collection.Should().AllBeOfType<MyData>();
    }

    /// <summary>
    /// Tests GetDataRequest for retrieving collection data
    /// </summary>
    [Fact]
    public void GetDataRequest_ForCollection_ShouldReturnData()
    {
        // arrange
        var client = GetClient();
        var collectionRef = new CollectionReference(nameof(MyData));

        // act
        var response = client.Observe(new GetDataRequest(collectionRef), o => o.WithTarget(CreateHostAddress()))
            .Should().Within(10.Seconds()).Emit();

        // assert
        var dataResponse = response.Message.Should().BeOfType<GetDataResponse>().Which;
        dataResponse.Data.Should().NotBeNull();
        dataResponse.Version.Should().BeGreaterThan(0);

        // Verify the returned data is valid JSON and contains expected data
    }

    /// <summary>
    /// Tests GetDataRequest for retrieving specific entity data
    /// </summary>
    [Fact]
    public void GetDataRequest_ForEntity_ShouldReturnSpecificEntity()
    {
        // arrange
        var client = GetClient();
        var entityRef = new EntityReference(nameof(MyData), "1");

        // act
        var response = client.Observe(new GetDataRequest(entityRef), o => o.WithTarget(CreateClientAddress()))
            .Should().Within(10.Seconds()).Emit();

        // assert
        var dataResponse = response.Message.Should().BeOfType<GetDataResponse>().Which;
        dataResponse.Data.Should().NotBeNull();
        dataResponse.Version.Should().BeGreaterThan(0);

    }

    /// <summary>
    /// Tests GetDataRequest for non-existent entity
    /// </summary>
    [Fact]
    public void GetDataRequest_ForNonExistentEntity_ShouldHandleGracefully()
    {
        // arrange
        var client = GetClient();
        var entityRef = new EntityReference(nameof(MyData), "999");

        // act & assert - this might throw or return null/empty, depending on implementation
        // The exact behavior should be consistent with the stream-based approach
        var response = client.Observe(new GetDataRequest(entityRef), o => o.WithTarget(CreateClientAddress()))
            .Should().Within(10.Seconds()).Emit();

        var dataResponse = response.Message.Should().BeOfType<GetDataResponse>().Which;
        dataResponse.Should().NotBeNull();
    }

    /// <summary>
    /// Helper method to get the type(s) of a property from a JSON schema element.
    /// </summary>
    /// <param name="propertyElement">The JSON element representing the property</param>
    /// <returns>A list of types (handles both single type and array of types)</returns>
    private static List<string> GetPropertyType(JsonElement propertyElement)
    {
        var types = new List<string>();

        if (propertyElement.TryGetProperty("type", out var typeElement))
        {
            if (typeElement.ValueKind == JsonValueKind.Array)
            {
                // Handle array of types like ["string", "null"]
                foreach (var type in typeElement.EnumerateArray())
                {
                    types.Add(type.GetString()!);
                }
            }
            else
            {
                // Handle single type like "string"
                types.Add(typeElement.GetString()!);
            }
        }

        return types;
    }
}
