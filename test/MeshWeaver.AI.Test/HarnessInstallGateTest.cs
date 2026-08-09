#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using System.Runtime.CompilerServices;
using System.Reactive.Linq;
using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.AI.Test;

/// <summary>
/// The per-user install gate for <see cref="Harness.RequiresInstall"/> harnesses (the CLI ones):
/// <see cref="HarnessNodeType.ResolveInstalledHarness"/> runs such a harness only while the picked
/// node path resolves to an Active node — the node a Store plugin localizes into
/// <c>{user}/Harness</c> on install and deletes on uninstall. No node (never installed,
/// uninstalled, or a stale pre-gate global <c>Harness/{id}</c> path) resolves to <c>null</c>, which
/// execution treats as "fall back to the default agent path" — a graceful degrade, never a wedge.
/// </summary>
public class HarnessInstallGateTest : AITestBase
{
    public HarnessInstallGateTest(ITestOutputHelper output) : base(output) { }

    protected override bool ShareMeshAcrossTests => true;

    private const string GatedId = "GatedCli";
    private const string HarnessAck = "GATED HARNESS RAN";

    private sealed class GatedCliHarness : IHarness
    {
        public string Id => GatedId;
        public Harness Definition => new()
        {
            Id = GatedId, DisplayName = "Gated CLI", Order = 9,
            SupportsAgentSelection = false, RequiresInstall = true
        };
        // A distinctive client so a round-level test can tell WHICH path answered:
        // the harness ack vs the FakeChatClientFactory's agent-path ack.
        public IChatClient? CreateChatClient(HarnessExecutionContext context) => new AckChatClient(HarnessAck);
    }

    private sealed class AckChatClient(string ack) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, ack)));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, ack);
            await Task.Yield();
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceType == typeof(IChatClient) ? this : null;

        public void Dispose() { }
    }

    private sealed class FakeChatClientFactory : IChatClientFactory
    {
        public const string AgentAck = "agent path ack";
        public string Name => "FakeFactory";
        public IReadOnlyList<string> Models => ["fake-model"];
        public int Order => 0;

        public Microsoft.Agents.AI.ChatClientAgent CreateAgent(
            AgentConfiguration config,
            IAgentChat chat,
            IReadOnlyDictionary<string, Microsoft.Agents.AI.ChatClientAgent> existingAgents,
            IReadOnlyList<AgentConfiguration> hierarchyAgents,
            string? modelName = null)
            => new(
                chatClient: new AckChatClient(AgentAck),
                instructions: config.Instructions ?? "You are a fake test assistant.",
                name: config.Id,
                description: config.Description ?? config.Id,
                tools: [],
                loggerFactory: null,
                services: null);

        public Task<Microsoft.Agents.AI.ChatClientAgent> CreateAgentAsync(
            AgentConfiguration config,
            IAgentChat chat,
            IReadOnlyDictionary<string, Microsoft.Agents.AI.ChatClientAgent> existingAgents,
            IReadOnlyList<AgentConfiguration> hierarchyAgents,
            string? modelName = null)
            => Task.FromResult(CreateAgent(config, chat, existingAgents, hierarchyAgents, modelName));
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services =>
            {
                services.AddSingleton<IHarness, GatedCliHarness>();
                services.AddSingleton<IChatClientFactory>(new FakeChatClientFactory());
                return services;
            });

    // The client needs the data/layout wiring for GetWorkspace().GetMeshNodeStream(path) —
    // the same surface ThreadExecution's parentHub has in prod (see ThreadComposerFlowTest).
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        configuration.TypeRegistry.AddAITypes();
        return base.ConfigureClient(configuration).AddLayoutClient();
    }

    /// <summary>Not installed: the picked path has no node → null → default agent path.</summary>
    [Fact(Timeout = 30000)]
    public async Task RequiresInstall_WithoutInstalledNode_ResolvesNull()
    {
        var hub = GetClient();
        // The absent-node case emits only after the InstallProbeTimeout elapses — give the
        // assertion strictly more than that so it never races the probe.
        var resolved = await HarnessNodeType
            .ResolveInstalledHarness(hub, $"{TestPartition}/Harness/{GatedId}")
            .Should().Within(HarnessNodeType.InstallProbeTimeout + TimeSpan.FromSeconds(10)).Emit();
        resolved.Should().BeNull(
            "a RequiresInstall harness without its installed node must fall back — this is what " +
            "makes uninstall (and the pre-gate stale global path) actually revoke the harness");
    }

    /// <summary>Installed: the localized node exists in the user/space partition → the harness runs.</summary>
    [Fact(Timeout = 30000)]
    public async Task RequiresInstall_WithInstalledNode_ResolvesHarness()
    {
        var hub = GetClient();
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        // The node the Store plugin's install localizes into the viewer's partition.
        var installed = new MeshNode(GatedId, $"{TestPartition}/Harness")
        {
            NodeType = HarnessNodeType.NodeType,
            Name = "Gated CLI",
            State = MeshNodeState.Active,
            Content = new Harness
            {
                Id = GatedId, DisplayName = "Gated CLI", Order = 9, RequiresInstall = true
            }
        };
        await meshService.CreateNode(installed).Should().Emit();

        var resolved = await HarnessNodeType
            .ResolveInstalledHarness(hub, installed.Path)
            .Should().Emit();
        resolved.Should().NotBeNull("the installed node is what licenses the harness for this user");
        resolved!.Id.Should().Be(GatedId);
    }

    /// <summary>A non-gated harness (MeshWeaver) resolves with no node probe at all.</summary>
    [Fact(Timeout = 30000)]
    public async Task NonGatedHarness_ResolvesWithoutProbe()
    {
        var hub = GetClient();
        var resolved = await HarnessNodeType
            .ResolveInstalledHarness(hub, $"{HarnessNodeType.RootNamespace}/{Harnesses.MeshWeaver}")
            .Should().Emit();
        resolved.Should().BeOfType<MeshWeaverHarness>(
            "the default harness needs no install and must resolve immediately");
    }

    /// <summary>
    /// THE round-level pin — through the REAL execution pipeline (StartThread →
    /// ExecuteMessageAsync), not the resolver in isolation: ExecuteMessageAsync normalizes
    /// request.Harness to the bare id early, so the install gate MUST probe the ORIGINAL picked
    /// node path. Probing the normalized value would license off the wrong node (the bare id is
    /// a partition ROOT path) — the exact regression a resolver-only test cannot catch. With the
    /// installed node present, the round answers from the HARNESS client.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task Round_WithInstalledNode_RunsTheGatedHarness()
    {
        var client = GetClient();
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var harnessPath = $"{TestPartition}/Harness/{GatedId}";

        await meshService.CreateNode(new MeshNode(GatedId, $"{TestPartition}/Harness")
        {
            NodeType = HarnessNodeType.NodeType,
            Name = "Gated CLI",
            State = MeshNodeState.Active,
            Content = new Harness { Id = GatedId, DisplayName = "Gated CLI", Order = 9, RequiresInstall = true }
        }).Should().Emit();

        var threadCreated = new System.Reactive.Subjects.AsyncSubject<MeshNode>();
        client.StartThread(
            namespacePath: TestPartition,
            userText: "run on the gated harness",
            agentName: "Agent/Assistant",
            modelName: "_Provider/Fake/fake-model",
            harness: harnessPath,
            contextPath: TestPartition,
            createdBy: "rbuergi@systemorph.com",
            onCreated: node => { threadCreated.OnNext(node); threadCreated.OnCompleted(); });

        var created = await threadCreated.Should().Emit();
        var thread = await WaitForThread(created!.Path!, t => t.Messages.Count >= 2);

        var responseText = await LastAssistantText(created.Path!, thread);
        responseText.Should().Contain(HarnessAck,
            "the installed picked-path node licenses the gated harness — the round must run its " +
            "client, not fall back (falling back here means the gate probed the WRONG path, e.g. " +
            "the id-normalized value instead of the picked node path)");
    }

    /// <summary>
    /// The same round WITHOUT the installed node: the gate refuses and the round answers from the
    /// default agent path — graceful fallback, no error, no wedge.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task Round_WithoutInstalledNode_FallsBackToAgentPath()
    {
        var client = GetClient();
        // A picked path whose node deliberately does not exist (uninstalled / never installed).
        var harnessPath = $"{TestPartition}/Harness/never-installed-{GatedId}";

        var threadCreated = new System.Reactive.Subjects.AsyncSubject<MeshNode>();
        client.StartThread(
            namespacePath: TestPartition,
            userText: "run without an install",
            agentName: "Agent/Assistant",
            modelName: "_Provider/Fake/fake-model",
            harness: harnessPath,
            contextPath: TestPartition,
            createdBy: "rbuergi@systemorph.com",
            onCreated: node => { threadCreated.OnNext(node); threadCreated.OnCompleted(); });

        var created = await threadCreated.Should().Emit();
        var thread = await WaitForThread(created!.Path!, t => t.Messages.Count >= 2);

        var responseText = await LastAssistantText(created.Path!, thread);
        responseText.Should().NotContain(HarnessAck,
            "an uninstalled harness must never run — the gate falls back to the agent path");
        responseText.Should().Contain(FakeChatClientFactory.AgentAck,
            "the fallback is the default agent path, a graceful degrade rather than an error");
    }

    private async Task<MeshThread> WaitForThread(string threadPath, Func<MeshThread, bool> predicate)
        => (await Mesh.GetWorkspace().GetMeshNodeStream(threadPath)
            .Select(n => n?.Content as MeshThread)
            .Where(t => t is not null)
            .Should().Within(TimeSpan.FromSeconds(45))
            .Match(t => predicate(t!)))!;

    // The text of the thread's last assistant message cell (messages are satellite nodes).
    // The cell is created with placeholder text ("Allocating agent...") and streamed into —
    // wait for the TERMINAL status, not for a first/non-empty emission.
    private async Task<string> LastAssistantText(string threadPath, MeshThread thread)
    {
        var lastId = thread.Messages[^1];
        var node = await Mesh.GetWorkspace().GetMeshNodeStream($"{threadPath}/{lastId}")
            .Where(n => n?.ContentAs<ThreadMessage>(Mesh.JsonSerializerOptions) is
                { Status: ThreadMessageStatus.Completed })
            .Take(1)
            .Should().Within(TimeSpan.FromSeconds(30)).Emit();
        return node!.ContentAs<ThreadMessage>(Mesh.JsonSerializerOptions)!.Text ?? "";
    }
}
