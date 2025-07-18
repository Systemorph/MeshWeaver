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
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Activities;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

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
public class DataPluginTest(ITestOutputHelper output) : HubTestBase(output)
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
                data.AddHubSource(new HostAddress(), dataSource => dataSource.WithType<MyData>())
            );

    /// <summary>
    /// Tests basic data plugin initialization and data loading
    /// </summary>
    [Fact]
    public async Task InitializeTest()
    {
        var workspace = GetHost().GetWorkspace();
        var response = await workspace
            .GetObservable<MyData>()
            .Timeout(10.Seconds())
            .FirstOrDefaultAsync();
        response.Should().BeEquivalentTo(MyData.InitialData);
    }


    /// <summary>
    /// Tests data update operations through the data plugin
    /// </summary>
    [Fact]
    public async Task Update()
    {
        // arrange
        var client = GetClient();
        var updateItems = new object[] { new MyData("1", "AAA"), new MyData("3", "CCC"), };

        var clientWorkspace = client.GetWorkspace();
        var data = (await clientWorkspace
                .GetObservable<MyData>()
                //.Timeout(10.Seconds())
                .FirstOrDefaultAsync())!
            .OrderBy(a => a.Id)
            .ToArray();

        data.Should().HaveCount(2);

        // act
        var updateResponse = await client.AwaitResponse(
            DataChangeRequest.Update(updateItems),
            o => o.WithTarget(new ClientAddress())//,
            //new CancellationTokenSource(TimeSpan.FromSeconds(3)).Token
        );

        // asserts
        updateResponse.Message.Should().BeOfType<DataChangeResponse>();
        var expectedItems = new MyData[] { new("1", "AAA"), new("2", "B"), new("3", "CCC") };

        data = (
            await clientWorkspace
                .GetObservable<MyData>()
                .Timeout(10.Seconds())
                .FirstOrDefaultAsync(x => x.Count == 3)
        )!
            .OrderBy(a => a.Id)
            .ToArray();

        data.ToArray().Should().BeEquivalentTo(expectedItems);
        data = (
            await GetHost()
                .GetWorkspace()
                .GetObservable<MyData>()
                .Timeout(10.Seconds())
                .FirstOrDefaultAsync(x => x.Count == 3)
        )!
            .OrderBy(a => a.Id)
            .ToArray();

        data.ToArray().Should().BeEquivalentTo(expectedItems);
        await Task.Delay(200);
        storage.Values.Cast<MyData>().OrderBy(x => x.Id).Should().BeEquivalentTo(expectedItems);
    }

    /// <summary>
    /// Tests data deletion operations through the data plugin
    /// </summary>
    [Fact]
    public async Task Delete()
    {
        // arrange
        var client = GetClient();

        var data = await GetHost()
            .GetWorkspace()
            .GetObservable<MyData>()
            .Timeout(10.Seconds())
            .FirstOrDefaultAsync();
        data.Should().BeEquivalentTo(MyData.InitialData);

        var toBeDeleted = data.Take(1).ToArray();
        var expectedItems = data.Skip(1).ToArray();
        // act
        var deleteResponse = await client.AwaitResponse(
            DataChangeRequest.Delete(toBeDeleted, "TestUser"),
            o => o.WithTarget(new ClientAddress())
            //, new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token
        );


        // asserts
        data = await GetClient()
            .GetWorkspace()
            .GetObservable<MyData>()
            .Timeout(10.Seconds())
            .FirstOrDefaultAsync(i => i.Count == 1);
        data.Should().BeEquivalentTo(expectedItems);
        data = await GetHost()
            .GetWorkspace()
            .GetObservable<MyData>()
            .Timeout(10.Seconds())
            .FirstOrDefaultAsync(i => i.Count == 1);
        data.Should().BeEquivalentTo(expectedItems);

        storage.Values.Should().BeEquivalentTo(expectedItems);
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
    public async Task CheckUsagesFromWorkspaceVariable()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var myInstance = await workspace
            .GetObservable<MyData>("1")
            .Timeout(10.Seconds())
            .FirstAsync();
        myInstance!.Text.Should().NotBe(TextChange);

        // act
        myInstance = myInstance with
        {
            Text = TextChange
        };
        await client.AwaitResponse(
            DataChangeRequest.Update([myInstance]),
            o => o.WithTarget(new ClientAddress()),
            new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token
        );

        var hostWorkspace = GetHost().GetWorkspace();

        var instance = await hostWorkspace
            .GetObservable<MyData>("1")
            .Timeout(10.Seconds())
            .FirstAsync(i => i?.Text == TextChange)
            ;
        instance.Should().NotBeNull();
        await Task.Delay(100);
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
    public async Task ValidationFailure()
    {
        // arrange
        var client = GetClient();
        var updateItems = new object[] { new MyData("5", null!) };

        // act
        var updateResponse = await client.AwaitResponse(
            DataChangeRequest.Update(updateItems),
            o => o.WithTarget(new ClientAddress())
            //, new CancellationTokenSource(TimeSpan.FromSeconds(3)).Token
        );

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
    public async Task ReduceCollectionReference()
    {
        var host = GetHost();
        var collection = await host.GetWorkspace().GetStream(new CollectionReference(nameof(MyData)))
            .Select(c => c.Value!.Instances.Values)
            .FirstAsync();

        collection.Should().BeEquivalentTo(MyData.InitialData);
    }

    /// <summary>
    /// Tests that GetSchemaRequest returns a valid JSON schema for MyData type
    /// </summary>
    [Fact]
    public async Task GetSchemaRequest_ShouldReturnValidJsonSchema()
    {
        // arrange
        var client = GetClient();
        var typeName = typeof(MyData).FullName!;

        // act
        var response = await client.AwaitResponse(
            new GetSchemaRequest(typeName),
            o => o.WithTarget(new ClientAddress()),
            new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token
        );

        // assert
        var schemaResponse = response.Message.Should().BeOfType<SchemaResponse>().Which;
        schemaResponse.Type.Should().Be(typeName); schemaResponse.Schema.Should().NotBeNullOrEmpty();
        schemaResponse.Schema.Should().NotBe("{}");

        // Verify it's valid JSON
        var schemaJson = JsonDocument.Parse(schemaResponse.Schema);
        schemaJson.Should().NotBeNull();

        // Verify it represents an object type (most likely)
        GetPropertyType(schemaJson.RootElement).Should().Contain("object");

        // Verify that it has some meaningful content - not just an empty object
        schemaJson.RootElement.EnumerateObject().Should().NotBeEmpty();
    }

    /// <summary>
    /// Tests that GetSchemaRequest returns empty schema for unknown types
    /// </summary>
    [Fact]
    public async Task GetSchemaRequest_ForUnknownType_ShouldReturnEmptySchema()
    {
        // arrange
        var client = GetClient();
        var unknownTypeName = "UnknownType";

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
    /// Tests that GetDomainTypesRequest returns all available registered types
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
        var myDataType = typesResponse.Types.FirstOrDefault(t => t.Name.Contains("MyData"));
        myDataType.Should().NotBeNull();
        myDataType.DisplayName.Should().NotBeNullOrEmpty();
        myDataType.Description.Should().NotBeNullOrEmpty();
    }

    /// <summary>
    /// Tests that GetDomainTypesRequest returns types in sorted order
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
        );        // assert
        var typesResponse = response.Message.Should().BeOfType<DomainTypesResponse>().Which;
        var types = typesResponse.Types.ToArray();

        // Verify types are sorted by display name
        var sortedTypes = types.OrderBy(t => t.DisplayName).ToArray();
        types.Should().Equal(sortedTypes, (t1, t2) => (t1.DisplayName).Equals(t2.DisplayName ?? t2.Name));
    }

    /// <summary>
    /// Tests update operations with invalid data and validation error handling
    /// </summary>
    [Fact]
    public async Task UpdateWithInvalidData_ShouldReturnValidationErrors()
    {
        // arrange
        var client = GetClient();
        var invalidItems = new object[]
        {
            new MyData("1", null!), // Required field is null
            new MyData("", "Valid text") // Empty ID
        };

        // act
        var updateResponse = await client.AwaitResponse(
            DataChangeRequest.Update(invalidItems),
            o => o.WithTarget(new ClientAddress()),
            new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token
        );

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
    public async Task UpdateNonExistentRecord_ShouldCreateNewRecord()
    {
        // arrange
        var client = GetClient();
        var newItem = new MyData("999", "New Item");

        // Verify item doesn't exist initially
        var initialData = await GetHost()
            .GetWorkspace()
            .GetObservable<MyData>()
            .Timeout(10.Seconds())
            .FirstOrDefaultAsync();
        initialData.Should().NotContain(x => x.Id == "999");

        // act
        var updateResponse = await client.AwaitResponse(
            DataChangeRequest.Update(new object[] { newItem }),
            o => o.WithTarget(new ClientAddress()),
            new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token
        );

        // assert
        updateResponse.Message.Should().BeOfType<DataChangeResponse>();
        var updatedData = await GetHost()
            .GetWorkspace()
            .GetObservable<MyData>()
            .Timeout(10.Seconds())
            .FirstOrDefaultAsync(x => x.Any(item => item.Id == "999"));
        updatedData.Should().Contain(x => x.Id == "999" && x.Text == "New Item");
    }

    /// <summary>
    /// Tests handling of multiple simultaneous update operations
    /// </summary>
    [Fact]
    public async Task MultipleSimultaneousUpdates_ShouldHandleCorrectly()
    {
        // arrange
        var client = GetClient();
        var updates1 = new object[] { new MyData("10", "Update 1") };
        var updates2 = new object[] { new MyData("11", "Update 2") };
        var updates3 = new object[] { new MyData("12", "Update 3") };

        // act - send multiple updates simultaneously
        var tasks = new[]
        {
            client.AwaitResponse(
                DataChangeRequest.Update(updates1),
                o => o.WithTarget(new ClientAddress()),
                new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token
            ),
            client.AwaitResponse(
                DataChangeRequest.Update(updates2),
                o => o.WithTarget(new ClientAddress()),
                new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token
            ),
            client.AwaitResponse(
                DataChangeRequest.Update(updates3),
                o => o.WithTarget(new ClientAddress()),
                new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token
            )
        };

        var responses = await Task.WhenAll(tasks);

        // assert
        responses.Should().AllSatisfy(response =>
            response.Message.Should().BeOfType<DataChangeResponse>());

        var finalData = await GetHost()
            .GetWorkspace()
            .GetObservable<MyData>()
            .Timeout(10.Seconds())
            .FirstOrDefaultAsync(x => x.Count >= 5); // Initial 2 + 3 new items

        finalData.Should().Contain(x => x.Id == "10" && x.Text == "Update 1");
        finalData.Should().Contain(x => x.Id == "11" && x.Text == "Update 2");
        finalData.Should().Contain(x => x.Id == "12" && x.Text == "Update 3");
    }

    /// <summary>
    /// Tests schema request behavior with null or empty type parameters
    /// </summary>
    [Fact]
    public async Task SchemaRequest_WithNullOrEmptyType_ShouldReturnEmptySchema()
    {
        // arrange
        var client = GetClient();

        // act & assert for null
        var responseNull = await client.AwaitResponse(
            new GetSchemaRequest(null!),
            o => o.WithTarget(new ClientAddress()),
            new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token
        );

        var schemaResponseNull = responseNull.Message.Should().BeOfType<SchemaResponse>().Which;
        schemaResponseNull.Schema.Should().Be("{}");

        // act & assert for empty string
        var responseEmpty = await client.AwaitResponse(
            new GetSchemaRequest(""),
            o => o.WithTarget(new ClientAddress()),
            new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token
        );

        var schemaResponseEmpty = responseEmpty.Message.Should().BeOfType<SchemaResponse>().Which;
        schemaResponseEmpty.Schema.Should().Be("{}");
    }

    /// <summary>
    /// Tests data synchronization consistency between client and host
    /// </summary>
    [Fact]
    public async Task DataSynchronization_BetweenClientAndHost_ShouldStayConsistent()
    {
        // arrange
        var client = GetClient();
        var host = GetHost();
        var updateItem = new MyData("1", "Updated Text");

        // Get initial state
        var initialClientData = await client
            .GetWorkspace()
            .GetObservable<MyData>()
            .Timeout(10.Seconds())
            .FirstOrDefaultAsync();

        var initialHostData = await host
            .GetWorkspace()
            .GetObservable<MyData>()
            .Timeout(10.Seconds())
            .FirstOrDefaultAsync();

        initialClientData.Should().BeEquivalentTo(initialHostData);

        // act - update from client
        await client.AwaitResponse(
            DataChangeRequest.Update(new object[] { updateItem }),
            o => o.WithTarget(new ClientAddress()),
            new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token
        );

        // assert - both client and host should have the same updated data
        var updatedClientData = await client
            .GetWorkspace()
            .GetObservable<MyData>()
            .Timeout(10.Seconds())
            .FirstOrDefaultAsync(x => x.Any(item => item.Text == "Updated Text"));

        var updatedHostData = await host
            .GetWorkspace()
            .GetObservable<MyData>()
            .Timeout(10.Seconds())
            .FirstOrDefaultAsync(x => x.Any(item => item.Text == "Updated Text"));

        updatedClientData.Should().BeEquivalentTo(updatedHostData);
        updatedClientData.Should().Contain(x => x.Id == "1" && x.Text == "Updated Text");
    }

    /// <summary>
    /// Tests collection reference operations with specific data types
    /// </summary>
    [Fact]
    public async Task CollectionReference_WithSpecificType_ShouldReturnCorrectData()
    {
        // arrange
        var host = GetHost();
        var collectionRef = new CollectionReference(nameof(MyData));

        // act
        var stream = host.GetWorkspace().GetStream(collectionRef);
        var collection = await stream
            .Select(c => c.Value!.Instances.Values.Cast<MyData>())
            .Timeout(10.Seconds())
            .FirstAsync();

        // assert
        collection.Should().BeEquivalentTo(MyData.InitialData); 
        collection.Should().AllBeOfType<MyData>();
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
