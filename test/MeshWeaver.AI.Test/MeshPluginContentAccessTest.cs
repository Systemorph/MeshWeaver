#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.AI;
using MeshWeaver.AI.Persistence;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.Data.Completion;
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
/// Tests for content: reference resolution via MeshPlugin.Get.
/// Replicates the distributed deployment pattern where each node hub
/// gets its own "content" collection (matching ConfigureMemexMesh).
/// </summary>
public class MeshPluginContentAccessTest : MonolithMeshTestBase
{
    private static readonly string TestDataPath = Path.Combine(AppContext.BaseDirectory, "TestData");
    private static readonly string ContentBasePath = Path.Combine(Path.GetTempPath(), "MeshPluginContentAccessTest_" + Guid.NewGuid().ToString("N"));
    private readonly string _testId = Guid.NewGuid().ToString("N")[..8];

    public MeshPluginContentAccessTest(ITestOutputHelper output) : base(output)
    {
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return builder
            .UseMonolithMesh()
            .AddFileSystemPersistence(TestDataPath)
            .AddGraph()
            .AddAI()
            // Replicate the production ConfigureMemexMesh pattern:
            // Each node hub gets its own "content" collection backed by a per-node subdirectory.
            // This matches MemexConfiguration.ConfigureMemexMesh lines 310-334.
            .ConfigureDefaultNodeHub(config =>
            {
                var nodePath = config.Address.ToString();
                var contentDir = Path.Combine(ContentBasePath, nodePath);
                var nodeContentConfig = new ContentCollectionConfig
                {
                    Name = "content",
                    SourceType = "FileSystem",
                    IsEditable = true,
                    BasePath = contentDir,
                    Settings = new Dictionary<string, string>
                    {
                        ["BasePath"] = contentDir
                    }
                };
                config = config.AddContentCollection(_ => nodeContentConfig);
                return config.AddDefaultLayoutAreas();
            });
    }

    /// <summary>
    /// Tests content:filename.txt (no slash) - should resolve using default "content" collection.
    /// This is the exact pattern used in production: content:Input_Markus.txt
    /// </summary>
    [Fact]
    public async Task Get_ContentReference_NoSlash_ReturnsFileFromDefaultCollection()
    {
        var nodePath = $"ContentTest_{_testId}_A";
        var contentDir = Path.Combine(ContentBasePath, nodePath);
        Directory.CreateDirectory(contentDir);
        await File.WriteAllTextAsync(Path.Combine(contentDir, "Input_Markus.txt"), "Hello from Markus", TestContext.Current.CancellationToken);

        await NodeFactory.CreateNode(
            new MeshNode(nodePath) { Name = "Test Org", NodeType = "Markdown" });

        var plugin = new MeshPlugin(Mesh, new MockAgentChat());
        var result = await plugin.Get($"{nodePath}/content:Input_Markus.txt");

        Output.WriteLine($"Result: {result}");

        result.Should().NotStartWith("Not found");
        result.Should().NotStartWith("Error");
        result.Should().Contain("Hello from Markus");
    }

    /// <summary>
    /// Tests the exact production scenario: nested path with content: reference.
    /// Replicates: PartnerRe/AIConsulting/Interviews/content:Input_Markus.txt
    /// </summary>
    [Fact]
    public async Task Get_ContentReference_NestedPath_ReturnsFileContent()
    {
        var nodePath = $"ContentTest_{_testId}_B/SubUnit/Interviews";

        var contentDir = Path.Combine(ContentBasePath, nodePath);
        Directory.CreateDirectory(contentDir);
        await File.WriteAllTextAsync(Path.Combine(contentDir, "Input_Markus.txt"),
            "Interview notes for Markus about AI Consulting", TestContext.Current.CancellationToken);

        await NodeFactory.CreateNode(
            new MeshNode(nodePath) { Name = "Interviews", NodeType = "Markdown" });

        var plugin = new MeshPlugin(Mesh, new MockAgentChat());
        var result = await plugin.Get($"{nodePath}/content:Input_Markus.txt");

        Output.WriteLine($"Result: {result}");

        result.Should().NotStartWith("Not found");
        result.Should().NotStartWith("Error");
        result.Should().Contain("Interview notes for Markus");
    }

    /// <summary>
    /// Tests content:collectionName/filename.txt (with slash) — explicit collection name.
    /// </summary>
    [Fact]
    public async Task Get_ContentReference_WithExplicitCollection_ReturnsFileContent()
    {
        var nodePath = $"ContentTest_{_testId}_C";
        var contentDir = Path.Combine(ContentBasePath, nodePath);
        Directory.CreateDirectory(contentDir);
        await File.WriteAllTextAsync(Path.Combine(contentDir, "report.md"), "# Report Content", TestContext.Current.CancellationToken);

        await NodeFactory.CreateNode(
            new MeshNode(nodePath) { Name = "Test Org 2", NodeType = "Markdown" });

        var plugin = new MeshPlugin(Mesh, new MockAgentChat());
        var result = await plugin.Get($"{nodePath}/content:content/report.md");

        Output.WriteLine($"Result: {result}");

        result.Should().NotStartWith("Error");
        result.Should().Contain("# Report Content");
    }

    /// <summary>
    /// Tests direct GetDataRequest to node hub with UnifiedReference.
    /// This bypasses MeshPlugin.Get to test the handler directly.
    /// </summary>
    [Fact]
    public async Task GetDataRequest_ContentReference_DirectToNodeHub_ReturnsFileContent()
    {
        var nodePath = $"ContentTest_{_testId}_D";
        var contentDir = Path.Combine(ContentBasePath, nodePath);
        Directory.CreateDirectory(contentDir);
        await File.WriteAllTextAsync(Path.Combine(contentDir, "data.txt"), "Direct access content", TestContext.Current.CancellationToken);

        await NodeFactory.CreateNode(
            new MeshNode(nodePath) { Name = "Direct Test", NodeType = "Markdown" });

        var client = GetClient();
        var response = await client.AwaitResponse(
            new GetDataRequest(new UnifiedReference("content:data.txt")),
            o => o.WithTarget(new Address(nodePath)),
            new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);

        Output.WriteLine($"Response error: {response.Message.Error}");
        Output.WriteLine($"Response data: {response.Message.Data}");

        response.Message.Error.Should().BeNull();
        response.Message.Data.Should().NotBeNull();
        var content = response.Message.Data as string;
        content.Should().Contain("Direct access content");
    }

    /// <summary>
    /// Tests that content collection is properly registered on the node hub.
    /// </summary>
    [Fact]
    public async Task NodeHub_ContentCollection_IsRegistered()
    {
        var nodePath = $"ContentTest_{_testId}_E";
        var contentDir = Path.Combine(ContentBasePath, nodePath);
        Directory.CreateDirectory(contentDir);

        await NodeFactory.CreateNode(
            new MeshNode(nodePath) { Name = "Collection Test", NodeType = "Markdown" });

        var client = GetClient();
        var response = await client.AwaitResponse(
            new GetDataRequest(new UnifiedReference("collection:")),
            o => o.WithTarget(new Address(nodePath)),
            new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);

        Output.WriteLine($"Response error: {response.Message.Error}");
        Output.WriteLine($"Response data type: {response.Message.Data?.GetType().Name}");

        response.Message.Error.Should().BeNull();
    }

    /// <summary>
    /// Repro for the prod symptom: agent emits an absolute path where the SPACED filename
    /// portion is wrapped in double quotes (e.g. content/"My File.docx"). The full path
    /// looks like @/Org/Sub/content/"Diskussion Thomas Final Report.docx".
    /// MeshOperations.ResolvePath only strips SURROUNDING quotes, so embedded quotes
    /// survive, the address part is parsed correctly but the file lookup goes after a
    /// quoted-literal filename that doesn't exist.
    /// </summary>
    [Fact]
    public async Task Get_AbsolutePath_QuotedSpacedFilename_ReturnsFileContent()
    {
        var nodePath = $"ContentTest_{_testId}_Q";
        var contentDir = Path.Combine(ContentBasePath, nodePath);
        Directory.CreateDirectory(contentDir);
        const string SpacedFile = "Diskussion Thomas Final Report.txt";
        await File.WriteAllTextAsync(Path.Combine(contentDir, SpacedFile),
            "the actual report content", TestContext.Current.CancellationToken);

        await NodeFactory.CreateNode(
            new MeshNode(nodePath) { Name = "Quoted Test", NodeType = "Markdown" });

        var plugin = new MeshPlugin(Mesh, new MockAgentChat());

        // Exact prod-shape path: absolute (@/) + spaced filename wrapped in quotes.
        var path = $"@/{nodePath}/content/\"{SpacedFile}\"";
        var result = await plugin.Get(path);

        Output.WriteLine($"Path: {path}");
        Output.WriteLine($"Result: {result}");

        result.Should().NotStartWith("Error");
        result.Should().NotStartWith("Not found");
        result.Should().Contain("the actual report content");
    }

    /// <summary>
    /// Same as above but quotes wrap the WHOLE relative portion after content/ —
    /// i.e. content/"name" vs "content/name" — both shapes appear in agent output.
    /// </summary>
    [Fact]
    public async Task Get_AbsolutePath_QuotesAroundContentSegment_ReturnsFileContent()
    {
        var nodePath = $"ContentTest_{_testId}_Q2";
        var contentDir = Path.Combine(ContentBasePath, nodePath);
        Directory.CreateDirectory(contentDir);
        const string SpacedFile = "Input Markus Apr 15.txt";
        await File.WriteAllTextAsync(Path.Combine(contentDir, SpacedFile),
            "markus input notes", TestContext.Current.CancellationToken);

        await NodeFactory.CreateNode(
            new MeshNode(nodePath) { Name = "Markus Test", NodeType = "Markdown" });

        var plugin = new MeshPlugin(Mesh, new MockAgentChat());

        // Quote wraps "content/<file>" together rather than just <file>.
        var path = $"@/{nodePath}/\"content/{SpacedFile}\"";
        var result = await plugin.Get(path);

        Output.WriteLine($"Path: {path}");
        Output.WriteLine($"Result: {result}");

        result.Should().NotStartWith("Error");
        result.Should().NotStartWith("Not found");
        result.Should().Contain("markus input notes");
    }

    /// <summary>
    /// Tolerance matrix — every shape we've actually observed an agent or autocomplete
    /// emit for the SAME spaced file. They must all return the file content. Keep this
    /// table extended with new shapes the agents come up with in the wild.
    /// {NODE} is replaced with the per-test node path so the parameterization stays
    /// readable; {FILE} is the spaced filename. Both sit under a per-test temp dir.
    /// </summary>
    [Theory]
    [InlineData("@/{NODE}/content/{FILE}",                  "no quotes, absolute")]
    [InlineData("\"@/{NODE}/content/{FILE}\"",              "wrapping quotes around the whole reference")]
    [InlineData("@/{NODE}/content/\"{FILE}\"",              "quotes around filename only")]
    [InlineData("@/{NODE}/\"content/{FILE}\"",              "quotes around content/filename together")]
    [InlineData("@\"/{NODE}/content/{FILE}\"",              "quote right after @")]
    [InlineData("@\"{NODE}/content/{FILE}\"",               "quote after @, no leading slash")]
    [InlineData("@/{NODE}/content/{FILE}   ",               "trailing whitespace")]
    [InlineData("   @/{NODE}/content/{FILE}",               "leading whitespace")]
    [InlineData("@/{NODE}/content/'{FILE}'",                "single quotes around filename")]
    public async Task Get_AgentEmittedShapes_AllReturnFileContent(string template, string description)
    {
        var nodePath = $"ContentTest_{_testId}_{Math.Abs(template.GetHashCode()):X}";
        var contentDir = Path.Combine(ContentBasePath, nodePath);
        Directory.CreateDirectory(contentDir);
        const string SpacedFile = "Diskussion Thomas Final Report.txt";
        const string Body = "agent-shape tolerance body";
        await File.WriteAllTextAsync(Path.Combine(contentDir, SpacedFile), Body);

        await NodeFactory.CreateNode(
            new MeshNode(nodePath) { Name = "Tolerance", NodeType = "Markdown" });

        var path = template.Replace("{NODE}", nodePath).Replace("{FILE}", SpacedFile);
        Output.WriteLine($"[{description}] path: {path}");

        var plugin = new MeshPlugin(Mesh, new MockAgentChat());
        var result = await plugin.Get(path);
        Output.WriteLine($"  result: {result}");

        result.Should().Contain(Body, $"shape '{description}' should resolve to the file");
    }

    /// <summary>
    /// Yet another shape an agent (or model) sometimes emits: @"content/some path" — the
    /// quote sits right after @ and wraps everything that follows. Same fix applies
    /// (strip every quote), this just nails the regression.
    /// </summary>
    [Fact]
    public async Task Get_AbsolutePath_QuoteAfterAtSign_ReturnsFileContent()
    {
        var nodePath = $"ContentTest_{_testId}_Q3";
        var contentDir = Path.Combine(ContentBasePath, nodePath);
        Directory.CreateDirectory(contentDir);
        const string SpacedFile = "some fucking thing.txt";
        await File.WriteAllTextAsync(Path.Combine(contentDir, SpacedFile),
            "the contents", TestContext.Current.CancellationToken);

        await NodeFactory.CreateNode(
            new MeshNode(nodePath) { Name = "QAfterAt", NodeType = "Markdown" });

        var plugin = new MeshPlugin(Mesh, new MockAgentChat());
        var path = $"@\"/{nodePath}/content/{SpacedFile}\"";
        var result = await plugin.Get(path);

        Output.WriteLine($"Path: {path}");
        Output.WriteLine($"Result: {result}");

        result.Should().NotStartWith("Error");
        result.Should().NotStartWith("Not found");
        result.Should().Contain("the contents");
    }

    /// <summary>
    /// End-to-end repro: typing lowercase "markus" against the node hub's autocomplete
    /// must return the spaced file "Input Markus Apr 15.txt", and the InsertText that
    /// the autocomplete suggests must round-trip through MeshPlugin.Get and return the
    /// file content. This exercises the full chat pipeline: case-insensitive fuzzy
    /// match → quoted-reference InsertText → ResolvePath → file lookup.
    /// </summary>
    [Fact]
    public async Task Autocomplete_RoundTrip_LowercaseQuery_QuotedInsertText_GetsContent()
    {
        var nodePath = $"ContentTest_{_testId}_RT";
        var contentDir = Path.Combine(ContentBasePath, nodePath);
        Directory.CreateDirectory(contentDir);
        const string SpacedFile = "Input Markus Apr 15.txt";
        const string FileContent = "the input markus body";
        await File.WriteAllTextAsync(Path.Combine(contentDir, SpacedFile), FileContent);

        await NodeFactory.CreateNode(
            new MeshNode(nodePath) { Name = "Round Trip", NodeType = "Markdown" });

        var client = GetClient();

        // Step 1 — autocomplete, lowercase, treats node as the chat context.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        var acResponse = await client.AwaitResponse(
            new AutocompleteRequest("@markus", nodePath),
            o => o.WithTarget(new Address(nodePath)),
            cts.Token);

        Output.WriteLine($"Autocomplete items: {acResponse.Message.Items.Count}");
        foreach (var item in acResponse.Message.Items)
            Output.WriteLine($"  - Label={item.Label} | InsertText={item.InsertText}");

        var match = acResponse.Message.Items.FirstOrDefault(i =>
            i.Label != null && i.Label.Contains(SpacedFile, StringComparison.Ordinal));
        match.Should().NotBeNull("lowercase 'markus' should fuzzy-match 'Input Markus Apr 15.txt'");
        match!.InsertText.Should().NotBeNullOrEmpty();

        // The InsertText for a spaced filename is wrapped in quotes by FormatInsertText.
        match.InsertText.Should().Contain("\"", "spaced filenames are quoted in the InsertText");

        // Step 2 — feed the InsertText (verbatim, including quotes) into MeshPlugin.Get,
        // pretending the agent received it via attachment. Strip trailing whitespace the
        // way the chat input would when treating it as an @reference.
        var insertedRef = match.InsertText.TrimEnd();
        var plugin = new MeshPlugin(Mesh, new MockAgentChat());
        var contextResolved = MeshOperations.ResolveContextPath(
            new MockAgentChat { Context = new AgentContext { Address = new Address(nodePath), Context = nodePath } },
            insertedRef);
        Output.WriteLine($"Inserted ref: {insertedRef}");
        Output.WriteLine($"Context-resolved: {contextResolved}");

        var result = await plugin.Get(contextResolved);
        Output.WriteLine($"Get result: {result}");

        result.Should().NotStartWith("Error");
        result.Should().NotStartWith("Not found");
        result.Should().Contain(FileContent);
    }

    private class MockAgentChat : IAgentChat
    {
        public AgentContext? Context { get; set; }
        public Action<LayoutAreaControl>? OnDisplayLayoutArea { get; set; }
        public void SetContext(AgentContext? applicationContext) => Context = applicationContext;
        public Task ResumeAsync(ChatConversation conversation) => Task.CompletedTask;
        public IAsyncEnumerable<ChatMessage> GetResponseAsync(IReadOnlyCollection<ChatMessage> messages, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IReadOnlyCollection<ChatMessage> messages, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public void SetThreadId(string threadId) { }
        public void DisplayLayoutArea(LayoutAreaControl layoutAreaControl) => OnDisplayLayoutArea?.Invoke(layoutAreaControl);
        public Task<IReadOnlyList<AgentDisplayInfo>> GetOrderedAgentsAsync()
            => Task.FromResult<IReadOnlyList<AgentDisplayInfo>>(new List<AgentDisplayInfo>());
        public void SetSelectedAgent(string? agentName) { }
        public ThreadExecutionContext? ExecutionContext => null;
        public string? LastDelegationPath { get; set; }
        public Action<string>? UpdateDelegationStatus { get; set; }
    }
}
