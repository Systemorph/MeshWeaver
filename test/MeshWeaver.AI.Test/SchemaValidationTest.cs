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

    #region Id Validation

    [Fact]
    public async Task Create_WithSlashInId_SanitizesAndCreates()
    {
        var plugin = CreatePlugin();

        var nodeJson = JsonSerializer.Serialize(new
        {
            id = "ACME/Product/PricingTool",
            @namespace = "",
            name = "Pricing Tool",
            nodeType = "Markdown"
        });

        var result = await plugin.Create(nodeJson);

        result.Should().StartWith("Created:", because: "slashes in id should be sanitized into namespace + id");
        result.Should().Contain("PricingTool");
    }

    #endregion

    #region Schema-Based Validation Helper

    [Fact]
    public async Task ValidateContent_ValidContent_ReturnsNull()
    {
        var ops = new MeshOperations(Mesh);
        var node = new MeshNode("test", "ACME")
        {
            Name = "Test",
            NodeType = TestNodeType,
            Content = new TestProduct { Name = "Widget", Price = 9.99m, Quantity = 5 }
        };

        var result = await ops.ValidateContentAgainstSchemaAsync(node);

        result.Should().BeNull(because: "valid content should not produce validation errors");
    }

    [Fact]
    public async Task ValidateContent_NoNodeType_SkipsValidation()
    {
        var ops = new MeshOperations(Mesh);
        var node = new MeshNode("test", "ACME")
        {
            Name = "Test",
            Content = new { random = "data" }
        };

        var result = await ops.ValidateContentAgainstSchemaAsync(node);

        result.Should().BeNull(because: "no nodeType means validation is skipped");
    }

    [Fact]
    public async Task ValidateContent_UnknownNodeType_SkipsValidation()
    {
        var ops = new MeshOperations(Mesh);
        var node = new MeshNode("test", "ACME")
        {
            Name = "Test",
            NodeType = "NonExistentType",
            Content = new { anything = "goes" }
        };

        var result = await ops.ValidateContentAgainstSchemaAsync(node);

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
