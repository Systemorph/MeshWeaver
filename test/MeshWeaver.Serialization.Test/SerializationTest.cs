using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Serialization.Test;

public class SerializationTest(ITestOutputHelper output) : HubTestBase(output)
{
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration c)
    {
        return base.ConfigureHost(c)
            .WithTypes(typeof(BoomerangResponse), typeof(GetDataRequest), typeof(GetDataResponse))
            .WithRoutes(f => f.RouteAddress<ClientAddress>(
                    (routedAddress, d) =>
                    {
                        var hostedHub = c.ParentHub!.GetHostedHub(routedAddress, ConfigureClient);
                        var packagedDelivery = d.Package();
                        hostedHub.DeliverMessage(packagedDelivery);
                        return d.Forwarded();
                    }
                ))
            .WithHandler<Boomerang>(
            (hub, request) =>
            {
                hub.Post(
                    new BoomerangResponse(
                        request.Message.Object,
                        request.Message.Object.GetType().Name
                    ),
                    o => o.ResponseFor(request)
                );
                return request.Processed();
            });
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        return base.ConfigureClient(configuration)
            .WithTypes(typeof(BoomerangResponse), typeof(MyEvent), typeof(GetDataRequest), typeof(GetDataResponse))
            .WithRoutes(f =>
            f.RouteAddress<HostAddress>(
                (routedAddress, d) =>
                {
                    var hostedHub = configuration.ParentHub!.GetHostedHub(routedAddress, ConfigureHost);
                    var packagedDelivery = d.Package();
                    hostedHub.DeliverMessage(packagedDelivery);
                    return d.Forwarded();
                }
            )

        );
    }
    [Fact]
    public void BoomerangResponseSerialization()
    {
        var client = Router.GetHostedHub(new ClientAddress(), ConfigureClient);
        var orig = new BoomerangResponse(new MyEvent("Hello"), Type: nameof(MyEvent));
        var serialized = JsonSerializer.Serialize(orig, client.JsonSerializerOptions);

        var response = JsonNode.Parse(serialized).Deserialize<object>(client.JsonSerializerOptions);
        var message = response.Should().BeOfType<BoomerangResponse>().Which;
        message.Object.Should().BeOfType<MyEvent>().Which.Text.Should().Be("Hello");
        message.Type.Should().Be(nameof(MyEvent));
    }
    [Fact]
    public async Task BoomerangTest()
    {
        var client = Router.GetHostedHub(new ClientAddress(), ConfigureClient);

        var response = await client.AwaitResponse(
            new Boomerang(new MyEvent("Hello")),
            o => o.WithTarget(new HostAddress())
            , new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token
        );

        response.Message.Object.Should().BeOfType<MyEvent>().Which.Text.Should().Be("Hello");
        response.Message.Type.Should().Be(nameof(JsonElement));
    }

    [Fact]
    public void TestHostedHubSerializationOptions()
    {
        var client = Router.GetHostedHub(new ClientAddress(), ConfigureClient);
        var hosted = client.GetHostedHub(new SynchronizationAddress());

        Output.WriteLine("=== CLIENT HUB CONVERTERS ===");
        foreach (var converter in client.JsonSerializerOptions.Converters)
        {
            Output.WriteLine($"  {converter.GetType().FullName}");
        }

        Output.WriteLine("\n=== HOSTED HUB CONVERTERS ===");
        foreach (var converter in hosted.JsonSerializerOptions.Converters)
        {
            Output.WriteLine($"  {converter.GetType().FullName}");
        }

        // Hosted hub should inherit all converters from client hub
        var clientConverterTypes = client.JsonSerializerOptions.Converters.Select(c => c.GetType()).ToHashSet();
        var hostedConverterTypes = hosted.JsonSerializerOptions.Converters.Select(c => c.GetType()).ToHashSet();

        var missingInHosted = clientConverterTypes.Except(hostedConverterTypes).ToList();

        Output.WriteLine($"\nClient converters: {clientConverterTypes.Count}");
        Output.WriteLine($"Hosted converters: {hostedConverterTypes.Count}");
        Output.WriteLine($"Missing in hosted: {missingInHosted.Count}");

        // This test verifies our infrastructure fix - hosted hubs should inherit parent converters
        missingInHosted.Should().BeEmpty("hosted hub should inherit all converters from parent client hub");
    }

    [Fact]
    public void TestSimplePolymorphicSerialization()
    {
        var client = Router.GetHostedHub(new ClientAddress(), ConfigureClient);
        var hosted = client.GetHostedHub(new SynchronizationAddress());

        // Test simple polymorphic object
        var testMessage = new PolymorphicTestMessage
        {
            Data = "test data",
            Type = "test"
        };

        // Serialize with client
        var clientSerialized = JsonSerializer.Serialize(testMessage, client.JsonSerializerOptions);
        Output.WriteLine($"Client serialized: {clientSerialized}");

        // Serialize with hosted - should be identical
        var hostedSerialized = JsonSerializer.Serialize(testMessage, hosted.JsonSerializerOptions);
        Output.WriteLine($"Hosted serialized: {hostedSerialized}");

        clientSerialized.Should().Be(hostedSerialized, "both should serialize identically");

        // Deserialize with both
        var clientDeserialized = JsonSerializer.Deserialize<PolymorphicTestMessage>(clientSerialized, client.JsonSerializerOptions);
        var hostedDeserialized = JsonSerializer.Deserialize<PolymorphicTestMessage>(hostedSerialized, hosted.JsonSerializerOptions);

        clientDeserialized.Should().BeEquivalentTo(testMessage);
        hostedDeserialized.Should().BeEquivalentTo(testMessage);
        hostedDeserialized.Should().BeEquivalentTo(clientDeserialized, "both should deserialize identically");
    }

    [Fact]
    public void TestPolymorphicCollectionSerialization()
    {
        var client = Router.GetHostedHub(new ClientAddress(), ConfigureClient);
        var hosted = client.GetHostedHub(new SynchronizationAddress());

        // Test collection of polymorphic objects
        var testMessages = new List<object>
        {
            new PolymorphicTestMessage { Data = "test1", Type = "type1" },
            new PolymorphicTestMessage { Data = "test2", Type = "type2" }
        };

        // Serialize with client
        var clientSerialized = JsonSerializer.Serialize(testMessages, client.JsonSerializerOptions);
        Output.WriteLine($"Client serialized: {clientSerialized}");

        // Serialize with hosted - should be identical
        var hostedSerialized = JsonSerializer.Serialize(testMessages, hosted.JsonSerializerOptions);
        Output.WriteLine($"Hosted serialized: {hostedSerialized}");

        clientSerialized.Should().Be(hostedSerialized, "both should serialize identically");

        // Deserialize with both
        var clientDeserialized = JsonSerializer.Deserialize<List<object>>(clientSerialized, client.JsonSerializerOptions);
        var hostedDeserialized = JsonSerializer.Deserialize<List<object>>(hostedSerialized, hosted.JsonSerializerOptions);

        Output.WriteLine($"Client deserialized count: {clientDeserialized?.Count}");
        Output.WriteLine($"Hosted deserialized count: {hostedDeserialized?.Count}");

        if (clientDeserialized != null)
        {
            for (int i = 0; i < clientDeserialized.Count; i++)
            {
                Output.WriteLine($"Client item {i} type: {clientDeserialized[i].GetType().FullName}");
            }
        }

        if (hostedDeserialized != null)
        {
            for (int i = 0; i < hostedDeserialized.Count; i++)
            {
                Output.WriteLine($"Hosted item {i} type: {hostedDeserialized[i].GetType().FullName}");
            }
        }

        // Both should deserialize collections the same way
        clientDeserialized.Should().NotBeNull();
        hostedDeserialized.Should().NotBeNull();
        clientDeserialized.Count.Should().Be(hostedDeserialized.Count);

        // Each item should be properly deserialized as PolymorphicTestMessage
        clientDeserialized.Should().AllBeOfType<PolymorphicTestMessage>();
        hostedDeserialized.Should().AllBeOfType<PolymorphicTestMessage>();
    }

    [Fact]
    public void TestGenericPolymorphicTypeSerialization()
    {
        var client = Router.GetHostedHub(new ClientAddress(), ConfigureClient);
        var hosted = client.GetHostedHub(new SynchronizationAddress());

        // Create a generic polymorphic type similar to PropertyColumnControl<string>
        var genericObject = new GenericTestClass<string>
        {
            Property = "testProperty",
            Title = "Test Title"
        };

        // Test single object serialization/deserialization
        var singleSerialized = JsonSerializer.Serialize(genericObject, client.JsonSerializerOptions);
        Output.WriteLine($"Single generic object serialized: {singleSerialized}");

        var singleClientDeserialized = JsonSerializer.Deserialize<GenericTestClass<string>>(singleSerialized, client.JsonSerializerOptions);
        var singleHostedDeserialized = JsonSerializer.Deserialize<GenericTestClass<string>>(singleSerialized, hosted.JsonSerializerOptions);

        singleClientDeserialized.Should().BeEquivalentTo(genericObject);
        singleHostedDeserialized.Should().BeEquivalentTo(genericObject);

        // Now test collection of generic polymorphic objects - this mimics the DataGrid columns issue
        var genericCollection = new List<object>
        {
            new GenericTestClass<string> { Property = "prop1", Title = "Title 1" },
            new GenericTestClass<string> { Property = "prop2", Title = "Title 2" }
        };

        var collectionSerialized = JsonSerializer.Serialize(genericCollection, client.JsonSerializerOptions);
        Output.WriteLine($"Generic collection serialized: {collectionSerialized}");

        // Let's debug the type registration issue
        Output.WriteLine("\n=== TYPE REGISTRY DEBUG ===");
        var typeRegistry = hosted.ServiceProvider.GetService(typeof(ITypeRegistry)) as ITypeRegistry;
        if (typeRegistry != null)
        {
            var genericType = typeof(GenericTestClass<string>);
            Output.WriteLine($"Generic type full name: {genericType.FullName}");
            Output.WriteLine($"Generic type AssemblyQualifiedName: {genericType.AssemblyQualifiedName}");

            // Check if type is registered
            var hasTypeName = typeRegistry.TryGetCollectionName(genericType, out var registeredName);
            Output.WriteLine($"Type registered in registry: {hasTypeName}");
            if (hasTypeName)
            {
                Output.WriteLine($"Registered type name: {registeredName}");
            }

            // Try to manually register and see what name it gets
            var manualRegisteredName = typeRegistry.GetOrAddType(genericType);
            Output.WriteLine($"Manual registration name: {manualRegisteredName}");

            // Try to find it back
            var canFind = typeRegistry.TryGetType(manualRegisteredName, out var foundTypeInfo);
            Output.WriteLine($"Can find by registered name: {canFind}");
            if (canFind && foundTypeInfo != null)
            {
                Output.WriteLine($"Found type: {foundTypeInfo.Type.FullName}");
            }
        }
        else
        {
            Output.WriteLine("TypeRegistry not found in ServiceProvider");
        }

        // Try to deserialize with hosted hub options
        var hostedCollectionDeserialized = JsonSerializer.Deserialize<List<object>>(collectionSerialized, hosted.JsonSerializerOptions);

        Output.WriteLine($"Hosted collection deserialized count: {hostedCollectionDeserialized?.Count}");
        if (hostedCollectionDeserialized != null)
        {
            for (int i = 0; i < hostedCollectionDeserialized.Count; i++)
            {
                var item = hostedCollectionDeserialized[i];
                Output.WriteLine($"Hosted item {i} type: {item.GetType().FullName}");
                if (item is JsonElement je)
                {
                    Output.WriteLine($"  JsonElement content: {je.GetRawText()}");
                }
            }
        }

        // This test should pass if the polymorphic converter properly handles generic types
        hostedCollectionDeserialized.Should().NotBeNull();
        hostedCollectionDeserialized.Should().AllBeOfType<GenericTestClass<string>>("generic types should be properly deserialized, not as JsonElement");
    }

    [Fact]
    public async Task TestSerializationFailureHandling()
    {
        Output.WriteLine("Testing serialization failure handling...");
        
        // This test verifies that when no handler exists for a request message type,
        // AwaitResponse should throw DeliveryFailureException instead of hanging
        
        var client = Router.GetHostedHub(new ClientAddress(), ConfigureClient);
        
        // Send an UnknownRequest to the host 
        // The host has no handler for this type at all
        // This should result in a DeliveryFailure being sent back to the client
        var unknownRequest = new GetDataRequest(new EntityReference("collection", "id"));

        Output.WriteLine("Sending UnknownRequest to host (no handler exists for this type)...");
        
        // AwaitResponse should now throw DeliveryFailureException due to no handler being found
        var exception = await Assert.ThrowsAsync<DeliveryFailureException>(() =>
            client.AwaitResponse(
                unknownRequest,
                o => o.WithTarget(new HostAddress()),
                new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token
            )
        );

        // Verify the exception contains useful information about the failure
        exception.Should().NotBeNull();
        exception.Message.Should().NotBeEmpty();
        Output.WriteLine($"Exception message: {exception.Message}");
        
        // The message should indicate no handler was found
        var message = exception.Message.ToLowerInvariant();
        message.Should().Contain("could not deserialize");
    }
}

public record Boomerang(object Object) : IRequest<BoomerangResponse>;
public record BoomerangResponse(object Object, string Type);
public record MyEvent(string Text);

public record PolymorphicTestMessage
{
    public string Data { get; init; } = null!;
    public string Type { get; init; } = null!;
}

public class GenericTestClass<T>
{
    public string Property { get; set; } = null!;
    public string Title { get; set; } = null!;
}




