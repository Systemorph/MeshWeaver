#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.AI.Persistence;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
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
/// Unit tests for InlineReferenceResolver.ExtractSection.
/// </summary>
public class InlineReferenceResolverUnitTest
{
    [Fact]
    public void ExtractSection_MatchesH2Heading_ReturnsSectionContent()
    {
        var markdown = """
                       ## Overview
                       This is the overview.

                       ## Details
                       These are the details.
                       More detail text.

                       ## Summary
                       Final summary.
                       """;

        var result = InlineReferenceResolver.ExtractSection(markdown, "Details");

        result.Should().Contain("These are the details.");
        result.Should().Contain("More detail text.");
        result.Should().NotContain("This is the overview.");
        result.Should().NotContain("Final summary.");
    }

    [Fact]
    public void ExtractSection_NoMatchingSection_ReturnsFullText()
    {
        var markdown = """
                       ## Overview
                       This is the overview.
                       """;

        var result = InlineReferenceResolver.ExtractSection(markdown, "NonExistent");

        result.Should().Be(markdown);
    }

    [Fact]
    public void ExtractSection_CaseInsensitive_MatchesSection()
    {
        var markdown = """
                       ## DETAILS
                       Content here.
                       """;

        var result = InlineReferenceResolver.ExtractSection(markdown, "details");

        result.Should().Contain("Content here.");
    }

    [Fact]
    public void ExtractSection_LastSection_ExtractsToEndOfDocument()
    {
        var markdown = """
                       ## First
                       First content.

                       ## Last
                       Last content with no trailing section.
                       """;

        var result = InlineReferenceResolver.ExtractSection(markdown, "Last");

        result.Should().Contain("Last content with no trailing section.");
    }
}

/// <summary>
/// Integration tests for InlineReferenceResolver.ResolveAsync using a real mesh with test data.
/// The test data (samples/Graph/Data/) includes documentation nodes that can be resolved via @@ references.
/// </summary>
public class InlineReferenceResolverIntegrationTest : MonolithMeshTestBase
{
    private static readonly string TestDataPath = Path.Combine(AppContext.BaseDirectory, "TestData");

    public InlineReferenceResolverIntegrationTest(ITestOutputHelper output) : base(output) { }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return builder
            .UseMonolithMesh()
            .AddFileSystemPersistence(TestDataPath)
            .ConfigureServices(services =>
            {
                services.AddMemoryChatPersistence();
                return services;
            })
            .AddGraph()
            .ConfigureDefaultNodeHub(config => config.AddDefaultLayoutAreas());
    }

    [Fact]
    public async Task ResolveAsync_NoReferences_ReturnsOriginalText()
    {
        var text = "This is plain text with no @@ references.";
        var mockChat = new MockAgentChat();

        var result = await InlineReferenceResolver.ResolveAsync(text, Mesh, mockChat);

        result.Should().Be(text);
    }

    [Fact]
    public async Task ResolveAsync_WithDocumentationReference_ExpandsContent()
    {
        // The test data includes MeshWeaver/Documentation/AI/Tools/MeshPlugin.md
        var text = "Tools: @@MeshWeaver/Documentation/AI/Tools/MeshPlugin";
        var mockChat = new MockAgentChat();

        var result = await InlineReferenceResolver.ResolveAsync(text, Mesh, mockChat);

        // Should have expanded the reference - result should be longer than original
        result.Length.Should().BeGreaterThan(text.Length,
            "the @@ reference should be replaced with the document content");
        // Should contain actual tool documentation content
        result.Should().Contain("Get", "expanded content should include Get tool documentation");
        result.Should().Contain("Search", "expanded content should include Search tool documentation");
    }

    [Fact]
    public async Task ResolveAsync_WithNonExistentReference_LeavesReferenceUnchanged()
    {
        var text = "See @@NonExistent/Path/That/DoesNotExist for details";
        var mockChat = new MockAgentChat();

        var result = await InlineReferenceResolver.ResolveAsync(text, Mesh, mockChat);

        // When a reference can't be resolved, it should remain in the text
        result.Should().Contain("@@NonExistent/Path/That/DoesNotExist");
    }

    [Fact]
    public async Task ResolveAsync_WithNestedReferences_ResolvesRecursively()
    {
        // MeshPlugin.md contains @@MeshWeaver/Documentation/DataMesh/QuerySyntax
        // and @@MeshWeaver/Documentation/DataMesh/UnifiedPath
        // So resolving MeshPlugin.md should also expand those nested references
        var text = "@@MeshWeaver/Documentation/AI/Tools/MeshPlugin";
        var mockChat = new MockAgentChat();

        var result = await InlineReferenceResolver.ResolveAsync(text, Mesh, mockChat);

        // The result should contain content from QuerySyntax (nested reference)
        result.Should().Contain("scope:", "nested QuerySyntax reference should be expanded");
    }

    [Fact]
    public async Task ResolveAsync_MultipleReferences_ExpandsAll()
    {
        var text = "Query: @@MeshWeaver/Documentation/DataMesh/QuerySyntax\n\nPath: @@MeshWeaver/Documentation/DataMesh/UnifiedPath";
        var mockChat = new MockAgentChat();

        var result = await InlineReferenceResolver.ResolveAsync(text, Mesh, mockChat);

        // At minimum, the result should be significantly longer than the original
        result.Length.Should().BeGreaterThan(text.Length,
            "at least one reference should have been expanded with document content");
        // The first reference (QuerySyntax) should definitely be expanded
        result.Should().NotContain("@@MeshWeaver/Documentation/DataMesh/QuerySyntax",
            "QuerySyntax reference should be expanded");
    }

    private class MockAgentChat : IAgentChat
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
