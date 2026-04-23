#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Schema;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.AI;
using MeshWeaver.AI.Persistence;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Test content type with various property types for schema validation testing.
/// </summary>
public record TestProduct
{
    public string Name { get; init; } = "";
    public decimal Price { get; init; }
    public string? Category { get; init; }
    public int Quantity { get; init; }
}

/// <summary>
/// Tests for JSON schema retrieval and content validation against schemas.
/// </summary>
public class SchemaValidationTest : MonolithMeshTestBase
{
    private const string TestNodeType = "TestProduct";
    private static readonly string TestDataPath = Path.Combine(AppContext.BaseDirectory, "TestData");

    public SchemaValidationTest(ITestOutputHelper output) : base(output) { }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return builder
            .UseMonolithMesh()
            .AddFileSystemPersistence(TestDataPath)
            .AddGraph()
            .AddAI()
            .AddMeshNodes(new MeshNode(TestNodeType)
            {
                Name = "Test Product",
                AssemblyLocation = typeof(SchemaValidationTest).Assembly.Location,
                HubConfiguration = config => config
                    .AddMeshDataSource(source => source.WithContentType<TestProduct>())
                    .AddDefaultLayoutAreas()
            });
    }

    private MeshPlugin CreatePlugin() => new(Mesh, new TestAgentChat());

    #region Schema Retrieval

    [Fact]
    public void SchemaGeneration_ForTestProduct_ReturnsValidSchema()
    {
        // Use the NodeType's hub config to create a temporary hub and generate the schema
        var nodeTypeService = Mesh.ServiceProvider.GetRequiredService<INodeTypeService>();
        var hubConfig = nodeTypeService.GetCachedHubConfiguration(TestNodeType);
        hubConfig.Should().NotBeNull("TestProduct should have a cached hub configuration");

        var tempHub = Mesh.GetHostedHub(new Address("_schema_test"), hubConfig!);
        tempHub.Should().NotBeNull();

        try
        {
            var typeRegistry = tempHub!.ServiceProvider.GetRequiredService<ITypeRegistry>();
            typeRegistry.TryGetType(TestNodeType, out var typeDef).Should().BeTrue(
                "TestProduct type should be registered in the hub's type registry");

            var schemaNode = Mesh.JsonSerializerOptions.GetJsonSchemaAsNode(typeDef!.Type);
            var schemaText = schemaNode.ToJsonString();

            schemaText.Should().NotBeNullOrEmpty();
            schemaText.Should().NotBe("{}");
        }
        finally
        {
            tempHub!.Dispose();
        }
    }

    [Fact]
    public void SchemaGeneration_ContainsExpectedProperties()
    {
        var nodeTypeService = Mesh.ServiceProvider.GetRequiredService<INodeTypeService>();
        var hubConfig = nodeTypeService.GetCachedHubConfiguration(TestNodeType);
        var tempHub = Mesh.GetHostedHub(new Address("_schema_test2"), hubConfig!);

        try
        {
            var typeRegistry = tempHub!.ServiceProvider.GetRequiredService<ITypeRegistry>();
            typeRegistry.TryGetType(TestNodeType, out var typeDef);
            var schemaText = Mesh.JsonSerializerOptions.GetJsonSchemaAsNode(typeDef!.Type).ToJsonString();

            var schemaLower = schemaText.ToLowerInvariant();
            schemaLower.Should().Contain("name", "Schema should reference 'name' property");
            schemaLower.Should().Contain("price", "Schema should reference 'price' property");
            schemaLower.Should().Contain("quantity", "Schema should reference 'quantity' property");
        }
        finally
        {
            tempHub!.Dispose();
        }
    }

    #endregion

    #region Content Validation — Valid Content

    [Fact]
    public async Task Create_WithValidContent_Succeeds()
    {
        var plugin = CreatePlugin();
        var uniqueId = $"valid-product-{Guid.NewGuid():N}";

        var nodeJson = JsonSerializer.Serialize(new
        {
            id = uniqueId,
            @namespace = "ACME",
            name = "Valid Product",
            nodeType = TestNodeType,
            content = new { name = "Widget", price = 9.99m, quantity = 10, category = "Tools" }
        });

        var result = await plugin.Create(nodeJson);

        result.Should().StartWith("Created:", because: "valid content should pass schema validation");
    }

    [Fact]
    public async Task Create_WithPartialValidContent_Succeeds()
    {
        var plugin = CreatePlugin();
        var uniqueId = $"partial-product-{Guid.NewGuid():N}";

        var nodeJson = JsonSerializer.Serialize(new
        {
            id = uniqueId,
            @namespace = "ACME",
            name = "Partial Product",
            nodeType = TestNodeType,
            content = new { name = "Gadget", price = 5.0m, quantity = 1 }
        });

        var result = await plugin.Create(nodeJson);

        result.Should().StartWith("Created:", because: "partial content with defaults should pass");
    }

    #endregion

    #region Update — Null Content Rejection

    [Fact]
    public async Task Update_WithNullContent_ReturnsValidationErrorAndSchema()
    {
        var plugin = CreatePlugin();
        var uniqueId = $"null-content-update-{Guid.NewGuid():N}";

        // Seed a node with valid content
        var createJson = JsonSerializer.Serialize(new
        {
            id = uniqueId,
            @namespace = "ACME",
            name = "Original",
            nodeType = TestNodeType,
            content = new { name = "Widget", price = 9.99m, quantity = 5 }
        });
        (await plugin.Create(createJson)).Should().StartWith("Created:");

        // Update with content explicitly set to null — should be rejected
        var updateJson = JsonSerializer.Serialize(new object[]
        {
            new
            {
                id = uniqueId,
                @namespace = "ACME",
                name = "Updated Without Content",
                nodeType = TestNodeType,
                content = (object?)null
            }
        });

        var result = await plugin.Update(updateJson);

        result.Should().Contain("Error", because: "null content must be rejected");
        result.Should().Contain("content", because: "the error must call out the content field");
        // Schema for TestProduct must be embedded so the agent can recover
        result.Should().Contain("Expected content schema", because: "agent needs the schema to retry");
        result.Should().Contain("name", because: "schema should include the TestProduct.name property");
        result.Should().Contain("price", because: "schema should include the TestProduct.price property");
        result.Should().Contain("quantity", because: "schema should include the TestProduct.quantity property");

        // Original content must still be intact (the rejected update did not overwrite anything)
        var afterReject = await plugin.Get($"@ACME/{uniqueId}");
        afterReject.Should().Contain("Widget");
    }

    [Fact]
    public async Task Update_WithMissingContent_ReturnsValidationErrorAndSchema()
    {
        var plugin = CreatePlugin();
        var uniqueId = $"missing-content-update-{Guid.NewGuid():N}";

        // Seed a node
        var createJson = JsonSerializer.Serialize(new
        {
            id = uniqueId,
            @namespace = "ACME",
            name = "Original",
            nodeType = TestNodeType,
            content = new { name = "Widget", price = 9.99m, quantity = 5 }
        });
        (await plugin.Create(createJson)).Should().StartWith("Created:");

        // Update without including the content key at all — also rejected
        var updateJson = JsonSerializer.Serialize(new object[]
        {
            new
            {
                id = uniqueId,
                @namespace = "ACME",
                name = "Name Only",
                nodeType = TestNodeType
            }
        });

        var result = await plugin.Update(updateJson);

        result.Should().Contain("Error");
        result.Should().Contain("content");
        result.Should().Contain("Expected content schema");
    }

    [Fact]
    public async Task Patch_WithExplicitNullContent_ReturnsValidationErrorAndSchema()
    {
        var plugin = CreatePlugin();
        var uniqueId = $"null-content-patch-{Guid.NewGuid():N}";

        var createJson = JsonSerializer.Serialize(new
        {
            id = uniqueId,
            @namespace = "ACME",
            name = "Original",
            nodeType = TestNodeType,
            content = new { name = "Widget", price = 9.99m, quantity = 5 }
        });
        (await plugin.Create(createJson)).Should().StartWith("Created:");

        // Patch with content: null should be rejected and return the schema
        var patchFields = "{\"content\": null}";
        var result = await plugin.Patch($"@ACME/{uniqueId}", patchFields);

        result.Should().Contain("Error");
        result.Should().Contain("content");
        result.Should().Contain("Expected content schema");
        result.Should().Contain("name");
        result.Should().Contain("price");

        // Existing content must still be intact
        var afterReject = await plugin.Get($"@ACME/{uniqueId}");
        afterReject.Should().Contain("Widget");
    }

    [Fact]
    public async Task Patch_WithoutContentKey_PreservesExistingContent()
    {
        var plugin = CreatePlugin();
        var uniqueId = $"patch-no-content-{Guid.NewGuid():N}";

        var createJson = JsonSerializer.Serialize(new
        {
            id = uniqueId,
            @namespace = "ACME",
            name = "Original",
            nodeType = TestNodeType,
            content = new { name = "Widget", price = 9.99m, quantity = 5 }
        });
        (await plugin.Create(createJson)).Should().StartWith("Created:");

        // Patching only the name must not touch content, and must not trip the null-content guard.
        var result = await plugin.Patch($"@ACME/{uniqueId}", "{\"name\": \"Renamed\"}");

        // The null-content guard must NOT fire when 'content' key is omitted.
        result.Should().NotContain("'content' is null",
            because: "omitting the content key is the supported way to leave content alone");
        result.Should().NotContain("Expected content schema",
            because: "no schema should be returned when the patch is valid");

        var after = await plugin.Get($"@ACME/{uniqueId}");
        after.Should().Contain("Widget", because: "existing content must survive a content-less patch");
    }

    #endregion

    #region Schema Helper API

    [Fact]
    public void GetContentSchema_ForRegisteredType_ReturnsSchema()
    {
        var ops = new MeshOperations(Mesh);

        var schema = ops.GetContentSchema(TestNodeType);

        schema.Should().NotBeNullOrEmpty();
        schema!.Should().Contain("name");
        schema.Should().Contain("price");
        schema.Should().Contain("quantity");
    }

    [Fact]
    public void GetContentSchema_ForUnknownType_ReturnsNull()
    {
        var ops = new MeshOperations(Mesh);

        var schema = ops.GetContentSchema("NonExistentType");

        schema.Should().BeNull();
    }

    #endregion

    #region Id Validation

    [Fact]
    public async Task Create_WithSlashInId_SanitizesAndCreates()
    {
        var plugin = CreatePlugin();
        var uniqueSuffix = Guid.NewGuid().ToString("N")[..8];

        var nodeJson = JsonSerializer.Serialize(new
        {
            id = $"ACME/Product/PricingTool{uniqueSuffix}",
            @namespace = "",
            name = "Pricing Tool",
            nodeType = "Markdown"
        });

        var result = await plugin.Create(nodeJson);

        result.Should().StartWith("Created:", because: "slashes in id should be sanitized into namespace + id");
        result.Should().Contain($"PricingTool{uniqueSuffix}");
    }

    #endregion

    #region Schema-Based Validation Helper

    [Fact]
    public void ValidateContent_ValidContent_ReturnsNull()
    {
        var ops = new MeshOperations(Mesh);
        var node = new MeshNode("test", "ACME")
        {
            Name = "Test",
            NodeType = TestNodeType,
            Content = new TestProduct { Name = "Widget", Price = 9.99m, Quantity = 5 }
        };

        var result = ops.ValidateContentAgainstSchema(node);

        result.Should().BeNull(because: "valid content should not produce validation errors");
    }

    [Fact]
    public void ValidateContent_NoNodeType_SkipsValidation()
    {
        var ops = new MeshOperations(Mesh);
        var node = new MeshNode("test", "ACME")
        {
            Name = "Test",
            Content = new { random = "data" }
        };

        var result = ops.ValidateContentAgainstSchema(node);

        result.Should().BeNull(because: "no nodeType means validation is skipped");
    }

    [Fact]
    public void ValidateContent_UnknownNodeType_SkipsValidation()
    {
        var ops = new MeshOperations(Mesh);
        var node = new MeshNode("test", "ACME")
        {
            Name = "Test",
            NodeType = "NonExistentType",
            Content = new { anything = "goes" }
        };

        var result = ops.ValidateContentAgainstSchema(node);

        result.Should().BeNull(because: "unknown node type means no schema to validate against");
    }

    #endregion

    /// <summary>
    /// Minimal IAgentChat implementation for testing MeshPlugin.
    /// </summary>
    private class TestAgentChat : IAgentChat
    {
        public AgentContext? Context { get; set; }
        public void SetContext(AgentContext? applicationContext) => Context = applicationContext;
        public Task ResumeAsync(ChatConversation conversation) => Task.CompletedTask;
        public IAsyncEnumerable<ChatMessage> GetResponseAsync(IReadOnlyCollection<ChatMessage> messages, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IReadOnlyCollection<ChatMessage> messages, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public void SetThreadId(string threadId) { }
        public void DisplayLayoutArea(LayoutAreaControl layoutAreaControl) { }
        public Task<IReadOnlyList<AgentDisplayInfo>> GetOrderedAgentsAsync()
            => Task.FromResult<IReadOnlyList<AgentDisplayInfo>>(new List<AgentDisplayInfo>());
        public void SetSelectedAgent(string? agentName) { }
    }
}
