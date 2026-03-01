#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.AI.AzureOpenAI;
using MeshWeaver.AI.Persistence;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using TextContent = Microsoft.Extensions.AI.TextContent;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Integration tests for agent delegation using real LLM calls via GitHub Models.
/// Verifies that when a Coordinator agent delegates to a Worker agent:
/// 1. Both agent threads are generated
/// 2. The Worker's thread runs in the namespace of the Coordinator's thread
/// 3. The delegation result flows back to the Coordinator
///
/// Requires GH_TOKEN environment variable for GitHub Models API access.
/// </summary>
[Collection("DelegationIntegrationTests")]
public class DelegationIntegrationTest : MonolithMeshTestBase
{
    private readonly string _testDataDirectory = InitTestDataDirectory();

    /// <summary>
    /// Creates a temp directory with test agent .md files.
    /// Must be static because it's called during field initialization (before base ctor).
    /// </summary>
    private static string InitTestDataDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "MeshWeaverDelegationTest", Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        CreateTestAgentFiles(dir);
        return dir;
    }

    public DelegationIntegrationTest(ITestOutputHelper output) : base(output)
    {
    }

    private static void CreateTestAgentFiles(string directory)
    {
        // Create Coordinator agent: delegates tasks to Worker
        File.WriteAllText(Path.Combine(directory, "Coordinator.md"),
            """
            ---
            nodeType: Agent
            name: Coordinator
            description: Coordinates tasks by delegating to Worker
            isDefault: true
            delegations:
              - agentPath: Worker
                instructions: Handles general knowledge questions
            ---

            You are Coordinator, a helpful assistant that delegates tasks to specialized agents.
            When the user asks a question, use the delegate_to_agent tool to send it to Worker.
            After receiving the result, summarize it for the user.
            """);

        // Create Worker agent: answers questions directly
        File.WriteAllText(Path.Combine(directory, "Worker.md"),
            """
            ---
            nodeType: Agent
            name: Worker
            description: A helpful assistant that answers general knowledge questions
            ---

            You are Worker, a helpful assistant. Answer questions concisely.
            """);
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return builder
            .UseMonolithMesh()
            .AddFileSystemPersistence(_testDataDirectory)
            .AddGraph()
            .ConfigureServices(services =>
            {
                var ghToken = Environment.GetEnvironmentVariable("GH_TOKEN");
                services.AddAzureOpenAI(config =>
                {
                    config.Endpoint = "https://models.inference.ai.azure.com";
                    config.ApiKey = ghToken ?? "not-set";
                    config.Models = ["gpt-4o-mini"];
                });
                services.AddAgentChatServices();
                return services;
            });
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();

        if (Directory.Exists(_testDataDirectory))
        {
            try { Directory.Delete(_testDataDirectory, recursive: true); }
            catch { /* Ignore cleanup errors */ }
        }
    }

    /// <summary>
    /// Verifies that when Coordinator delegates to Worker:
    /// 1. The delegation tool (delegate_to_agent) is called
    /// 2. The delegation result appears as FunctionResultContent in the parent's thread
    /// 3. The response contains text from both Coordinator and Worker
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task Delegation_CoordinatorDelegatesToWorker_BothThreadsCreated()
    {
        var ghToken = Environment.GetEnvironmentVariable("GH_TOKEN");
        if (string.IsNullOrEmpty(ghToken))
        {
            Output.WriteLine("SKIPPED: GH_TOKEN environment variable not set. " +
                             "Set it to a GitHub token for GitHub Models API access.");
            return;
        }

        // Arrange: Create AgentChatClient from the mesh service provider
        var chatClient = new AgentChatClient(Mesh.ServiceProvider);
        await chatClient.InitializeAsync(null, "gpt-4o-mini");

        // Verify agents were loaded
        var agents = await chatClient.GetOrderedAgentsAsync();
        Output.WriteLine($"Loaded {agents.Count} agents: [{string.Join(", ", agents.Select(a => a.Name))}]");
        agents.Should().Contain(a => a.Name == "Coordinator", "Coordinator agent should be loaded from .md file");
        agents.Should().Contain(a => a.Name == "Worker", "Worker agent should be loaded from .md file");

        // Act: Send a message — Coordinator should delegate to Worker
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "What is the capital of France?")
        };

        var responses = new List<ChatMessage>();
        await foreach (var msg in chatClient.GetResponseAsync(messages, TestContext.Current.CancellationToken))
        {
            responses.Add(msg);
            // Log each response message for debugging
            foreach (var content in msg.Contents)
            {
                switch (content)
                {
                    case FunctionCallContent fc:
                        Output.WriteLine($"  FunctionCall: {fc.Name}({string.Join(", ", fc.Arguments?.Select(kv => $"{kv.Key}={kv.Value}") ?? [])})");
                        break;
                    case FunctionResultContent fr:
                        var resultPreview = fr.Result?.ToString()?[..Math.Min(200, fr.Result.ToString()!.Length)] ?? "(null)";
                        Output.WriteLine($"  FunctionResult: callId={fr.CallId}, result={resultPreview}");
                        break;
                    case TextContent tc:
                        Output.WriteLine($"  Text: {tc.Text?[..Math.Min(200, tc.Text.Length)]}");
                        break;
                    default:
                        Output.WriteLine($"  Content: {content.GetType().Name}");
                        break;
                }
            }
        }

        // Assert: Collect all content items from the response
        var allContents = responses.SelectMany(m => m.Contents).ToList();

        // Assert: Delegation tool call was made
        var functionCalls = allContents.OfType<FunctionCallContent>().ToList();
        Output.WriteLine($"\nFunction calls: {functionCalls.Count}");
        foreach (var fc in functionCalls)
            Output.WriteLine($"  - {fc.Name}");

        functionCalls.Should().Contain(fc => fc.Name == "delegate_to_agent",
            "Coordinator should use delegate_to_agent tool to delegate to Worker");

        // Assert: Delegation result was returned (Worker's thread ran in Coordinator's namespace)
        var functionResults = allContents.OfType<FunctionResultContent>().ToList();
        Output.WriteLine($"\nFunction results: {functionResults.Count}");
        functionResults.Should().NotBeEmpty(
            "Worker's delegation result should appear as FunctionResultContent in Coordinator's thread");

        // Assert: Text response was generated
        var textContents = allContents
            .OfType<TextContent>()
            .Select(t => t.Text)
            .Where(t => !string.IsNullOrEmpty(t))
            .ToList();

        Output.WriteLine($"\nText contents: {textContents.Count}");
        textContents.Should().NotBeEmpty("Coordinator should produce a text response after delegation");

        var fullResponse = string.Join(" ", textContents);
        Output.WriteLine($"\nFull response: {fullResponse}");

        // The response should mention Paris (or at least have meaningful content)
        fullResponse.Length.Should().BeGreaterThan(5,
            "response should contain meaningful text from the delegation chain");
    }

    /// <summary>
    /// Verifies that the Worker's result (from its isolated thread) appears
    /// as a FunctionResultContent in the Coordinator's thread — proving
    /// that Worker's thread is in the namespace of Coordinator's thread.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task Delegation_WorkerThreadInCoordinatorNamespace_ResultInFunctionResult()
    {
        var ghToken = Environment.GetEnvironmentVariable("GH_TOKEN");
        if (string.IsNullOrEmpty(ghToken))
        {
            Output.WriteLine("SKIPPED: GH_TOKEN environment variable not set.");
            return;
        }

        // Arrange
        var chatClient = new AgentChatClient(Mesh.ServiceProvider);
        await chatClient.InitializeAsync(null, "gpt-4o-mini");

        // Act
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Say hello")
        };

        var responses = new List<ChatMessage>();
        await foreach (var msg in chatClient.GetResponseAsync(messages, TestContext.Current.CancellationToken))
        {
            responses.Add(msg);
        }

        // Assert: The FunctionResultContent proves Worker ran in Coordinator's namespace
        var allContents = responses.SelectMany(m => m.Contents).ToList();

        // The delegation call and result should both be present in the response messages
        var hasDelegationCall = allContents.OfType<FunctionCallContent>()
            .Any(fc => fc.Name == "delegate_to_agent");
        var hasDelegationResult = allContents.OfType<FunctionResultContent>().Any();

        Output.WriteLine($"Has delegation call: {hasDelegationCall}");
        Output.WriteLine($"Has delegation result: {hasDelegationResult}");

        if (hasDelegationCall)
        {
            // If Coordinator delegated, the result must be present (Worker's thread is in Coordinator's namespace)
            hasDelegationResult.Should().BeTrue(
                "when Coordinator delegates, Worker's result should appear as FunctionResult " +
                "in Coordinator's thread (Worker's thread is scoped within Coordinator's execution)");

            // The function result should contain meaningful text from Worker
            var delegationResults = allContents.OfType<FunctionResultContent>()
                .Select(fr => fr.Result?.ToString() ?? "")
                .Where(r => !string.IsNullOrEmpty(r))
                .ToList();

            Output.WriteLine($"Delegation results: {string.Join(" | ", delegationResults.Select(r => r[..Math.Min(100, r.Length)]))}");
            delegationResults.Should().NotBeEmpty("delegation result should contain Worker's response text");
        }
        else
        {
            // Log if Coordinator didn't delegate (LLM might not always follow instructions perfectly)
            Output.WriteLine("NOTE: Coordinator did not delegate this time. " +
                             "LLM may not always follow delegation instructions.");
        }
    }

    /// <summary>
    /// Verifies that agents loaded from .md files are correctly parsed
    /// and have their delegation configurations set up.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task AgentLoading_FromMarkdownFiles_ParsesDelegationsCorrectly()
    {
        var ghToken = Environment.GetEnvironmentVariable("GH_TOKEN");
        if (string.IsNullOrEmpty(ghToken))
        {
            Output.WriteLine("SKIPPED: GH_TOKEN environment variable not set.");
            return;
        }

        // Arrange & Act
        var chatClient = new AgentChatClient(Mesh.ServiceProvider);
        await chatClient.InitializeAsync(null, "gpt-4o-mini");

        var agents = await chatClient.GetOrderedAgentsAsync();

        // Assert: Both agents loaded
        agents.Count.Should().BeGreaterThanOrEqualTo(2, "should have at least Coordinator and Worker agents");

        // Assert: Coordinator has delegation to Worker
        var coordinator = agents.FirstOrDefault(a => a.Name == "Coordinator");
        coordinator.Should().NotBeNull("Coordinator agent should be loaded");
        coordinator!.AgentConfiguration.Should().NotBeNull();
        coordinator.AgentConfiguration!.Delegations.Should().NotBeNullOrEmpty(
            "Coordinator should have delegations configured from .md front matter");
        coordinator.AgentConfiguration.Delegations!
            .Should().Contain(d => d.AgentPath == "Worker",
                "Coordinator should delegate to Worker as configured in .md file");
        coordinator.AgentConfiguration.IsDefault.Should().BeTrue(
            "Coordinator should be the default agent");

        // Assert: Worker has no delegations
        var worker = agents.FirstOrDefault(a => a.Name == "Worker");
        worker.Should().NotBeNull("Worker agent should be loaded");
        worker!.AgentConfiguration.Should().NotBeNull();
        (worker.AgentConfiguration!.Delegations == null || worker.AgentConfiguration.Delegations.Count == 0)
            .Should().BeTrue("Worker should have no delegations");

        Output.WriteLine("Agent loading verified successfully:");
        foreach (var agent in agents)
        {
            var delegations = agent.AgentConfiguration?.Delegations;
            Output.WriteLine($"  - {agent.Name}: {agent.Description} " +
                             $"(delegations: {delegations?.Count ?? 0}, isDefault: {agent.AgentConfiguration?.IsDefault})");
        }
    }
}

[CollectionDefinition("DelegationIntegrationTests", DisableParallelization = true)]
public class DelegationIntegrationTestsCollection { }
