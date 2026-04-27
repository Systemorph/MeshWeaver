#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
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
/// Comprehensive failure-mode tests for the agent write tools (Create, Update, Patch).
///
/// Rationale: when an agent sends malformed input, the tool MUST return a speaking
/// error string — never an empty string, never a silent success, never a stream-
/// breaking write. These tests enumerate the failure shapes we see in the wild:
/// invalid JSON, null content, missing required fields, empty identifiers,
/// schema-violating content, and unknown paths.
/// </summary>
public class AgentWriteFailureTests : MonolithMeshTestBase
{
    private const string TestNodeType = nameof(TestProduct);
    private static readonly string TestDataPath = Path.Combine(AppContext.BaseDirectory, "TestData");

    public AgentWriteFailureTests(ITestOutputHelper output) : base(output) { }

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
                AssemblyLocation = typeof(AgentWriteFailureTests).Assembly.Location,
                HubConfiguration = config => config
                    .AddMeshDataSource(source => source.WithContentType<TestProduct>())
                    .AddDefaultLayoutAreas()
            });
    }

    private MeshPlugin CreatePlugin() => new(Mesh, new MinimalChat());

    private async Task<string> SeedProductAsync(MeshPlugin plugin, string uniqueId)
    {
        var json = JsonSerializer.Serialize(new
        {
            id = uniqueId,
            @namespace = "ACME",
            name = "Seeded Product",
            nodeType = TestNodeType,
            content = new { name = "Widget", price = 9.99m, quantity = 5 }
        });
        var result = await plugin.Create(json);
        result.Should().StartWith("Created:", because: "seed data must succeed so the real assertions are meaningful");
        return $"ACME/{uniqueId}";
    }

    // =========================================================================
    // INVARIANT: every failure path returns a non-empty speaking error
    // =========================================================================

    [Fact]
    public async Task NoTool_EverReturnsEmpty_OnAnyInput()
    {
        var plugin = CreatePlugin();
        var inputs = new[] { "", " ", "null", "[]", "{}", "not json", "[null]" };

        foreach (var input in inputs)
        {
            (await plugin.Create(input)).Should().NotBeNullOrWhiteSpace(
                because: $"Create('{input}') must never return empty — downstream streams depend on non-empty responses");
            (await plugin.Update(input)).Should().NotBeNullOrWhiteSpace(
                because: $"Update('{input}') must never return empty");
            (await plugin.Delete(input)).Should().NotBeNullOrWhiteSpace(
                because: $"Delete('{input}') must never return empty");
        }

        // Patch has a second argument — try each combination too
        foreach (var input in inputs)
        {
            (await plugin.Patch("@ACME/nonexistent", input)).Should().NotBeNullOrWhiteSpace();
            (await plugin.Patch(input, "{}")).Should().NotBeNullOrWhiteSpace();
        }
    }

    // =========================================================================
    // CREATE — failure modes
    // =========================================================================

    [Fact]
    public async Task Create_InvalidJson_ReturnsSpeakingError()
    {
        var plugin = CreatePlugin();

        var result = await plugin.Create("{ unterminated");

        result.Should().StartWith("Invalid JSON:");
        result.Should().Contain("escape", because: "hint helps the agent on the common root cause");
    }

    [Fact]
    public async Task Create_NullLiteral_ReturnsError()
    {
        var plugin = CreatePlugin();
        (await plugin.Create("null")).Should().Contain("null");
    }

    [Fact]
    public async Task Create_MissingName_ReturnsSpeakingError()
    {
        var plugin = CreatePlugin();
        var nodeJson = JsonSerializer.Serialize(new
        {
            id = $"no-name-{Guid.NewGuid():N}",
            @namespace = "ACME",
            nodeType = TestNodeType
        });

        var result = await plugin.Create(nodeJson);

        result.Should().Contain("Error");
        result.Should().Contain("name", because: "error must call out the field that's missing");
    }

    [Fact]
    public async Task Create_SchemaInvalidContent_ReturnsErrorWithSchema()
    {
        var plugin = CreatePlugin();
        var nodeJson = JsonSerializer.Serialize(new
        {
            id = $"bad-content-{Guid.NewGuid():N}",
            @namespace = "ACME",
            name = "Bad Content",
            nodeType = TestNodeType,
            // TestProduct.Price is decimal — passing an object forces a shape mismatch
            content = new { name = "X", price = new { nested = "object" }, quantity = 1 }
        });

        var result = await plugin.Create(nodeJson);

        result.Should().Contain("Error");
        result.Should().Contain("schema", because: "create must include the expected schema on shape mismatch");
        result.Should().Contain("price", because: "schema should expose the offending property");
    }

    // =========================================================================
    // UPDATE — failure modes (each entry failure must NOT halt the batch)
    // =========================================================================

    [Fact]
    public async Task Update_InvalidJson_ReturnsSpeakingError()
    {
        var plugin = CreatePlugin();
        var result = await plugin.Update("not a json array");
        result.Should().StartWith("Invalid JSON:");
    }

    [Fact]
    public async Task Update_EmptyArray_ReturnsNoNodesProvided()
    {
        var plugin = CreatePlugin();
        var result = await plugin.Update("[]");
        result.Should().Be("No nodes provided.");
    }

    [Fact]
    public async Task Update_ArrayWithNullEntry_ReportsErrorForEntry()
    {
        var plugin = CreatePlugin();
        var result = await plugin.Update("[null]");
        result.Should().Contain("Error");
        result.Should().Contain("null", because: "error must explain the null entry");
    }

    [Fact]
    public async Task Update_MissingId_ReturnsSpeakingError()
    {
        var plugin = CreatePlugin();
        var updateJson = JsonSerializer.Serialize(new object[]
        {
            new { @namespace = "ACME", name = "No Id", nodeType = TestNodeType, content = new { name = "X", price = 1m, quantity = 1 } }
        });

        var result = await plugin.Update(updateJson);

        result.Should().Contain("Error");
        result.Should().Contain("id", because: "error must pinpoint the missing id");
    }

    [Fact]
    public async Task Update_MissingName_ReturnsSpeakingError()
    {
        var plugin = CreatePlugin();
        var path = await SeedProductAsync(plugin, $"no-name-{Guid.NewGuid():N}");
        var id = path[("ACME/".Length)..];

        var updateJson = JsonSerializer.Serialize(new object[]
        {
            new { id, @namespace = "ACME", nodeType = TestNodeType, content = new { name = "X", price = 1m, quantity = 1 } }
        });

        var result = await plugin.Update(updateJson);

        result.Should().Contain("Error");
        result.Should().Contain("name", because: "empty name corrupts UI — error must call it out");
    }

    [Fact]
    public async Task Update_MissingNodeType_ReturnsSpeakingError()
    {
        var plugin = CreatePlugin();
        var updateJson = JsonSerializer.Serialize(new object[]
        {
            new { id = "x", @namespace = "ACME", name = "Name Only", content = new { anything = 1 } }
        });

        var result = await plugin.Update(updateJson);

        result.Should().Contain("Error");
        result.Should().Contain("nodeType");
        result.Should().Contain("Patch", because: "error should direct the agent to Patch for partial updates");
    }

    [Fact]
    public async Task Update_NullContent_ReturnsErrorWithSchema()
    {
        var plugin = CreatePlugin();
        var uniqueId = $"null-update-{Guid.NewGuid():N}";
        await SeedProductAsync(plugin, uniqueId);

        var updateJson = JsonSerializer.Serialize(new object[]
        {
            new { id = uniqueId, @namespace = "ACME", name = "Still Named", nodeType = TestNodeType, content = (object?)null }
        });

        var result = await plugin.Update(updateJson);

        result.Should().Contain("content");
        result.Should().Contain("null");
        result.Should().Contain("schema", because: "error must include the recovery schema");
        result.Should().Contain("price");

        // Seed is intact — rejected update must not overwrite persisted data
        (await plugin.Get($"@ACME/{uniqueId}")).Should().Contain("Widget");
    }

    [Fact]
    public async Task Update_SchemaInvalidContent_ReturnsErrorWithSchema()
    {
        var plugin = CreatePlugin();
        var uniqueId = $"bad-update-{Guid.NewGuid():N}";
        await SeedProductAsync(plugin, uniqueId);

        var updateJson = JsonSerializer.Serialize(new object[]
        {
            new
            {
                id = uniqueId,
                @namespace = "ACME",
                name = "Bad Update",
                nodeType = TestNodeType,
                content = new { name = "X", price = new { wrong = "shape" }, quantity = 1 }
            }
        });

        var result = await plugin.Update(updateJson);

        result.Should().Contain("Error");
        result.Should().Contain("schema");
        result.Should().Contain("price");
    }

    [Fact]
    public async Task Update_Batch_MixedValidAndInvalid_ReportsEachOutcome()
    {
        var plugin = CreatePlugin();
        var goodId = $"good-{Guid.NewGuid():N}";
        var badId = $"bad-{Guid.NewGuid():N}";
        await SeedProductAsync(plugin, goodId);
        await SeedProductAsync(plugin, badId);

        var updateJson = JsonSerializer.Serialize(new object[]
        {
            new { id = goodId, @namespace = "ACME", name = "Good Update", nodeType = TestNodeType, content = new { name = "Ok", price = 2m, quantity = 1 } },
            new { id = badId, @namespace = "ACME", name = "Bad Update", nodeType = TestNodeType, content = (object?)null }
        });

        var result = await plugin.Update(updateJson);

        result.Should().Contain($"Updated: ACME/{goodId}", because: "a valid entry must still succeed when another fails");
        result.Should().Contain("'content' is null", because: "the bad entry's error must be reported");
        result.Should().Contain("Expected content schema");
    }

    // =========================================================================
    // PATCH — failure modes
    // =========================================================================

    [Fact]
    public async Task Patch_NonExistentPath_ReturnsSpeakingError()
    {
        var plugin = CreatePlugin();
        var result = await plugin.Patch("@ACME/definitely-not-here", "{\"name\": \"X\"}");
        result.Should().Contain("not found");
    }

    [Fact]
    public async Task Patch_FieldsIsJsonArray_ReturnsError()
    {
        var plugin = CreatePlugin();
        var uniqueId = $"arr-fields-{Guid.NewGuid():N}";
        await SeedProductAsync(plugin, uniqueId);

        var result = await plugin.Patch($"@ACME/{uniqueId}", "[\"not\", \"an\", \"object\"]");

        result.Should().Contain("Error");
        result.Should().Contain("JSON object", because: "error must explain what shape is expected");
    }

    [Fact]
    public async Task Patch_FieldsInvalidJson_ReturnsSpeakingError()
    {
        var plugin = CreatePlugin();
        var uniqueId = $"bad-patch-json-{Guid.NewGuid():N}";
        await SeedProductAsync(plugin, uniqueId);

        var result = await plugin.Patch($"@ACME/{uniqueId}", "{ not json");

        // Either "Invalid JSON:" or a generic "Error:" is acceptable — what matters is
        // that it's a speaking error and not an empty string or silent success.
        result.Should().NotBeNullOrWhiteSpace();
        (result.Contains("Invalid JSON") || result.Contains("Error")).Should().BeTrue(
            because: $"broken JSON must surface a real error, got: '{result}'");
    }

    [Fact]
    public async Task Patch_ExplicitNullContent_RejectedWithSchema()
    {
        var plugin = CreatePlugin();
        var uniqueId = $"patch-null-{Guid.NewGuid():N}";
        await SeedProductAsync(plugin, uniqueId);

        var result = await plugin.Patch($"@ACME/{uniqueId}", "{\"content\": null}");

        result.Should().Contain("Error");
        result.Should().Contain("content");
        result.Should().Contain("null");
        result.Should().Contain("schema");
        result.Should().Contain("price");

        (await plugin.Get($"@ACME/{uniqueId}")).Should().Contain("Widget",
            because: "rejected patch must not overwrite persisted data");
    }

    [Fact]
    public async Task Patch_SchemaInvalidContent_RejectedWithSchema()
    {
        var plugin = CreatePlugin();
        var uniqueId = $"patch-bad-content-{Guid.NewGuid():N}";
        await SeedProductAsync(plugin, uniqueId);

        // Passing a content object whose shape violates TestProduct
        var fields = "{\"content\": {\"name\": \"X\", \"price\": {\"nested\": true}, \"quantity\": 1}}";
        var result = await plugin.Patch($"@ACME/{uniqueId}", fields);

        result.Should().Contain("Error");
        result.Should().Contain("schema");
        result.Should().Contain("price");

        (await plugin.Get($"@ACME/{uniqueId}")).Should().Contain("Widget",
            because: "shape-broken patch must not overwrite content");
    }

    [Fact]
    public async Task Patch_EmptyName_Rejected()
    {
        var plugin = CreatePlugin();
        var uniqueId = $"patch-empty-name-{Guid.NewGuid():N}";
        await SeedProductAsync(plugin, uniqueId);

        var result = await plugin.Patch($"@ACME/{uniqueId}", "{\"name\": \"\"}");

        result.Should().Contain("Error");
        result.Should().Contain("name");
        result.Should().Contain("empty");
    }

    [Fact]
    public async Task Patch_EmptyFieldsObject_DoesNotBreakStream()
    {
        var plugin = CreatePlugin();
        var uniqueId = $"patch-noop-{Guid.NewGuid():N}";
        await SeedProductAsync(plugin, uniqueId);

        // Empty fields = nothing to change; must not produce an empty result
        var result = await plugin.Patch($"@ACME/{uniqueId}", "{}");

        result.Should().NotBeNullOrWhiteSpace(
            because: "even a no-op patch must return a non-empty response so downstream streams stay healthy");

        // Existing content untouched
        (await plugin.Get($"@ACME/{uniqueId}")).Should().Contain("Widget");
    }

    // =========================================================================
    // STREAM HEALTH — rejected writes must leave persisted state unchanged
    // =========================================================================

    [Fact]
    public async Task RejectedWrites_DoNotCorruptPersistedNode()
    {
        var plugin = CreatePlugin();
        var uniqueId = $"stream-health-{Guid.NewGuid():N}";
        await SeedProductAsync(plugin, uniqueId);
        var path = $"@ACME/{uniqueId}";

        // Fire a battery of invalid writes
        var attempts = new[]
        {
            plugin.Update(JsonSerializer.Serialize(new object[] { new { id = uniqueId, @namespace = "ACME", name = "X", nodeType = TestNodeType, content = (object?)null } })),
            plugin.Update(JsonSerializer.Serialize(new object[] { new { id = uniqueId, @namespace = "ACME", name = "X", nodeType = TestNodeType, content = new { name = "X", price = new { bad = true }, quantity = 1 } } })),
            plugin.Patch(path, "{\"content\": null}"),
            plugin.Patch(path, "{\"name\": \"\"}"),
            plugin.Patch(path, "{\"content\": {\"name\": \"X\", \"price\": {\"bad\": true}, \"quantity\": 1}}"),
        };

        foreach (var t in attempts)
        {
            var r = await t;
            r.Should().Contain("Error", because: "every attempt is invalid and must be rejected");
            r.Should().NotBeNullOrWhiteSpace();
        }

        // After all rejected attempts, the seeded content must still be intact
        var after = await plugin.Get(path);
        after.Should().Contain("Widget", because: "the seed must survive all rejected writes — streams stay healthy");
    }

    // -------------------------------------------------------------------------
    private class MinimalChat : IAgentChat
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
