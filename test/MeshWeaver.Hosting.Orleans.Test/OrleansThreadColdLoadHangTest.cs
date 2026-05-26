using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.AI;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;
using Orleans.TestingHost;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Cold-load repro for the prod symptom from 2026-05-20 — a parent Thread with
/// a delegation Sub-Thread is left mid-stream in persistent storage when the
/// portal restarts. The user's reported URL had the exact shape:
/// <c>{partition}/_Thread/{parent-id}/{response-msg-id}/{sub-thread-id}</c>.
/// Opening either the parent or the sub-thread URL deadlocks on load.
///
/// <para>The expected behaviour: when the per-Thread grain activates cold and
/// reads a stale-executing node from storage, the activation MUST complete
/// without deadlock. Whether that completion looks like "auto-resume from
/// where the previous round was interrupted" or "recover to Idle and let the
/// user resubmit" is a separate design choice — the load-bearing invariant
/// the test pins is <b>no deadlock on activation</b>.</para>
///
/// <para>Why a SUB-THREAD is in the picture: the prod hang was specifically on
/// a sub-thread URL. Sub-threads have an extra hub-activation hop (parent's
/// response cell hub → sub-thread hub) and inherit the parent's
/// in-flight tool-call state via <c>DelegationPath</c> on
/// <see cref="ToolCallEntry"/>. The single-thread variant covered by the
/// in-class smaller test misses this hop.</para>
///
/// <para>In-memory variant (this class) ALWAYS runs in CI as a regression
/// guard. The Postgres variant
/// (<see cref="OrleansThreadColdLoadHangPostgresTest"/>) reproduces the prod
/// shape exactly and is skipped when <c>MESHWEAVER_LOCAL_PG_CS</c> isn't set.</para>
/// </summary>
public class OrleansThreadColdLoadHangTest(ITestOutputHelper output) : TestBase(output)
{
    private TestCluster Cluster { get; set; } = null!;
    private IMessageHub ClientMesh => Cluster.Client.ServiceProvider.GetRequiredService<IMessageHub>();

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        var builder = new TestClusterBuilder();
        builder.Options.InitialSilosCount = 1;
        builder.AddSiloBuilderConfigurator<ColdLoadInMemorySiloConfigurator>();
        builder.AddClientBuilderConfigurator<TestClientConfigurator>();
        Cluster = builder.Build();
        await Cluster.DeployAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        if (Cluster is not null)
            await Cluster.DisposeAsync();
        await base.DisposeAsync();
    }

    private async Task<IMessageHub> GetClientAsync([CallerMemberName] string? name = null)
    {
        var client = ClientMesh.ServiceProvider.CreateMessageHub(
            new Address("client", $"coldload-{name}-{Guid.NewGuid():N}"),
            config =>
            {
                config.TypeRegistry.AddAITypes();
                return config.AddLayoutClient();
            });
        var accessService = client.ServiceProvider.GetRequiredService<AccessService>();
        accessService.SetCircuitContext(new AccessContext
        {
            ObjectId = "TestUser", Name = "TestUser", Email = "testuser@meshweaver.io"
        });
        Cluster.Client.ServiceProvider.GetRequiredService<IRoutingService>()
            .RegisterStream(client.Address, client.DeliverMessage);
        return client;
    }

    /// <summary>
    /// Seeds the storage with the exact prod shape: parent Thread mid-stream,
    /// parent's response cell holds an unfinished <c>delegate_to_agent</c>
    /// pointing at a sub-thread, and the sub-thread itself is mid-stream with
    /// its own response cell stuck on "Allocating agent...". Bypasses
    /// <c>CreateNodeRequest</c> so no hub has yet activated — the seed is the
    /// post-crash state of a previous portal instance.
    /// </summary>
    private async Task<SeededPaths> SeedParentAndSubThreadAsync(CancellationToken ct)
    {
        var siloSp = ((InProcessSiloHandle)Cluster.Primary).SiloHost.Services;
        var adapter = siloSp.GetRequiredService<IStorageAdapter>();
        var options = JsonSerializerOptions.Default;

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var parentId = $"parent-{suffix}";
        var parentRespId = $"presp{suffix[..4]}";
        var parentUserId = $"puser{suffix[..4]}";
        var subId = $"sub-{suffix}";
        var subRespId = $"sresp{suffix[..4]}";
        var subUserId = $"suser{suffix[..4]}";

        var parentThreadPath = $"TestUser/_Thread/{parentId}";
        var parentRespPath = $"{parentThreadPath}/{parentRespId}";
        var parentUserPath = $"{parentThreadPath}/{parentUserId}";
        var subThreadPath = $"{parentRespPath}/{subId}";
        var subRespPath = $"{subThreadPath}/{subRespId}";
        var subUserPath = $"{subThreadPath}/{subUserId}";

        // --- PARENT THREAD ---------------------------------------------------
        // Mid-stream: Status=Executing, ActiveMessageId set, the streaming
        // tool calls hold a delegate_to_agent referring to the sub-thread via
        // DelegationPath. No PendingUserMessage → WatchForExecution skips,
        // recovery should fire.
        await adapter.Write(new MeshNode(parentId, "TestUser/_Thread")
        {
            Name = "Parent thread (frozen mid-delegation)",
            NodeType = ThreadNodeType.NodeType,
            MainNode = "TestUser",
            CreatedBy = "TestUser",
            Content = new MeshThread
            {
                CreatedBy = "TestUser",
                Messages = ImmutableList.Create(parentUserId, parentRespId),
                Status = ThreadExecutionStatus.Executing,
                ActiveMessageId = parentRespId,
                ExecutionStartedAt = DateTime.UtcNow.AddMinutes(-5),
                StreamingToolCalls = ImmutableList.Create(new ToolCallEntry
                {
                    Name = "delegate_to_agent",
                    DisplayName = "Delegating to Worker",
                    Result = null,
                    IsSuccess = false,
                    DelegationPath = subThreadPath
                })
            }
        }, options).FirstAsync().ToTask(ct);

        await adapter.Write(new MeshNode(parentUserId, parentThreadPath)
        {
            Name = "Parent user message",
            NodeType = ThreadMessageNodeType.NodeType,
            MainNode = "TestUser",
            CreatedBy = "TestUser",
            Content = new ThreadMessage
            {
                Role = "user",
                Text = "Please delegate this to the Worker.",
                Timestamp = DateTime.UtcNow.AddMinutes(-5),
                Type = ThreadMessageType.ExecutedInput,
                Status = ThreadMessageStatus.Submitted,
                CreatedBy = "TestUser"
            }
        }, options).FirstAsync().ToTask(ct);

        await adapter.Write(new MeshNode(parentRespId, parentThreadPath)
        {
            Name = "Parent response (frozen mid-stream)",
            NodeType = ThreadMessageNodeType.NodeType,
            MainNode = "TestUser",
            Content = new ThreadMessage
            {
                Role = "assistant",
                Text = "Delegating to Worker...",
                Timestamp = DateTime.UtcNow.AddMinutes(-5),
                Type = ThreadMessageType.AgentResponse,
                Status = ThreadMessageStatus.Streaming,
                ToolCalls = ImmutableList.Create(new ToolCallEntry
                {
                    Name = "delegate_to_agent",
                    DisplayName = "Delegating to Worker",
                    Result = null,
                    IsSuccess = false,
                    DelegationPath = subThreadPath
                })
            }
        }, options).FirstAsync().ToTask(ct);

        // --- SUB-THREAD ------------------------------------------------------
        // Lives directly under the parent's response message path. Mid-stream
        // shape: its own response cell stuck on "Allocating agent..." — the
        // exact placeholder a user sees frozen on the screen.
        await adapter.Write(new MeshNode(subId, parentRespPath)
        {
            Name = "Sub-thread (frozen)",
            NodeType = ThreadNodeType.NodeType,
            MainNode = "TestUser",
            CreatedBy = "TestUser",
            Content = new MeshThread
            {
                CreatedBy = "TestUser",
                Messages = ImmutableList.Create(subUserId, subRespId),
                Status = ThreadExecutionStatus.Executing,
                ActiveMessageId = subRespId,
                ExecutionStartedAt = DateTime.UtcNow.AddMinutes(-5)
            }
        }, options).FirstAsync().ToTask(ct);

        await adapter.Write(new MeshNode(subUserId, subThreadPath)
        {
            Name = "Sub-thread user prompt",
            NodeType = ThreadMessageNodeType.NodeType,
            MainNode = "TestUser",
            CreatedBy = "TestUser",
            Content = new ThreadMessage
            {
                Role = "user",
                Text = "do some work that hangs",
                Timestamp = DateTime.UtcNow.AddMinutes(-5),
                Type = ThreadMessageType.ExecutedInput,
                Status = ThreadMessageStatus.Submitted,
                CreatedBy = "TestUser"
            }
        }, options).FirstAsync().ToTask(ct);

        await adapter.Write(new MeshNode(subRespId, subThreadPath)
        {
            Name = "Sub-thread response (frozen mid-stream)",
            NodeType = ThreadMessageNodeType.NodeType,
            MainNode = "TestUser",
            Content = new ThreadMessage
            {
                Role = "assistant",
                Text = "Allocating agent...",
                Timestamp = DateTime.UtcNow.AddMinutes(-5),
                Type = ThreadMessageType.AgentResponse,
                Status = ThreadMessageStatus.Streaming
            }
        }, options).FirstAsync().ToTask(ct);

        Output.WriteLine($"Seeded parent {parentThreadPath} and sub-thread {subThreadPath}.");
        return new SeededPaths(parentThreadPath, parentRespPath, subThreadPath, subRespPath);
    }

    private sealed record SeededPaths(
        string ParentThreadPath,
        string ParentResponsePath,
        string SubThreadPath,
        string SubResponsePath);

    /// <summary>
    /// Repro of the prod symptom: activate the SUB-THREAD URL cold (the exact
    /// URL pattern the user reported as stuck). The sub-thread's grain must
    /// activate without deadlock and the per-node MeshNodeReference reducer
    /// must surface a stable, non-executing state within the recovery budget.
    ///
    /// <para>If the grain activation hangs: the stream never emits — the test
    /// times out and the recovery-on-cold-load handler is broken for the
    /// sub-thread shape. If the grain activates but never settles
    /// (IsExecuting stays true forever): same symptom, different root cause.</para>
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task SubThreadUrl_LoadedColdFromStorage_ActivatesAndSettlesWithoutDeadlock()
    {
        var ct = new CancellationTokenSource(80.Seconds()).Token;
        var paths = await SeedParentAndSubThreadAsync(ct);

        var client = await GetClientAsync();
        var workspace = client.GetWorkspace();

        // 1) Activate the SUB-THREAD by subscribing to its per-node reducer.
        //    This is the codepath a user hits by opening the sub-thread URL
        //    in the browser. The grain must come up; the first non-null
        //    emission is the proof that activation finished.
        Output.WriteLine($"Activating sub-thread {paths.SubThreadPath} cold...");
        var subStream = workspace
            .GetRemoteStream<MeshNode, MeshNodeReference>(
                new Address(paths.SubThreadPath), new MeshNodeReference());

        var firstSubEmission = await subStream
            .Where(change => change.Value is not null)
            .Take(1)
            .Timeout(ActivationBudget)
            .ToTask(ct);
        firstSubEmission.Should().NotBeNull(
            "sub-thread grain must activate cold from the persisted node — " +
            "if this times out, the grain wedged in OnActivateAsync.");
        Output.WriteLine("Sub-thread grain activated.");

        // 2) The sub-thread must SETTLE — either by recovery clearing it to
        //    Idle, or by auto-resuming and reaching completion. The
        //    load-bearing assertion is "must not stay stuck". We give the
        //    sub-thread the full recovery budget to reach IsExecuting=false.
        Output.WriteLine($"Waiting for sub-thread to settle (budget {RecoveryBudget.TotalSeconds:F0}s)...");
        var subSettled = await subStream
            .Select(change => change.Value?.Content as MeshThread)
            .Where(t => t is { IsExecuting: false })
            .Take(1)
            .Timeout(RecoveryBudget)
            .ToTask(ct);
        subSettled.Should().NotBeNull();
        subSettled!.IsExecuting.Should().BeFalse(
            "BUG REPRO: sub-thread URL hangs on cold load. Either recovery " +
            "didn't fire on activation, or recovery's OWN write deadlocked the " +
            "grain, or auto-resume entered an infinite wait. Recovery shape " +
            "in ThreadExecution.RecoverStaleExecutingThread (ThreadExecution.cs:202) " +
            "must clear sub-thread state on activation; if a separate auto-resume " +
            "path is added, it must not gate on cross-grain calls that wedge.");
        Output.WriteLine("Sub-thread settled.");

        // 3) The PARENT must also settle without deadlock. Activated by the
        //    same primitive — the prod repro is "navigate to either URL,
        //    deadlock", so this guards both halves.
        Output.WriteLine($"Activating parent thread {paths.ParentThreadPath}...");
        var parentStream = workspace
            .GetRemoteStream<MeshNode, MeshNodeReference>(
                new Address(paths.ParentThreadPath), new MeshNodeReference());
        var parentSettled = await parentStream
            .Select(change => change.Value?.Content as MeshThread)
            .Where(t => t is { IsExecuting: false })
            .Take(1)
            .Timeout(RecoveryBudget)
            .ToTask(ct);
        parentSettled.Should().NotBeNull();
        parentSettled!.IsExecuting.Should().BeFalse(
            "parent thread must also settle on cold load — it carries the " +
            "delegation toolcall pointing at the sub-thread and recovery has " +
            "to walk that toolcall.");
        Output.WriteLine($"PASSED — both parent and sub-thread cold-loaded and settled without deadlock.");
    }

    /// <summary>
    /// Single-thread (no delegation) variant — the minimal repro for cold-load
    /// recovery. Kept alongside the sub-thread test because a fix that
    /// addresses the sub-thread path while breaking the single-thread one
    /// would regress every "thread crashed mid-stream" recovery in prod, not
    /// just the delegation case.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task StaleExecutingThread_LoadedColdFromStorage_RecoversToIdle()
    {
        var ct = new CancellationTokenSource(80.Seconds()).Token;
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var threadPath = $"TestUser/_Thread/stale-{suffix}";
        var responseMsgId = $"r{suffix}";
        var userMsgId = $"u{suffix}";

        var siloSp = ((InProcessSiloHandle)Cluster.Primary).SiloHost.Services;
        var adapter = siloSp.GetRequiredService<IStorageAdapter>();
        var options = JsonSerializerOptions.Default;

        await adapter.Write(new MeshNode($"stale-{suffix}", "TestUser/_Thread")
        {
            Name = "Stale mid-execution thread",
            NodeType = ThreadNodeType.NodeType,
            MainNode = "TestUser",
            CreatedBy = "TestUser",
            Content = new MeshThread
            {
                CreatedBy = "TestUser",
                Messages = ImmutableList.Create(userMsgId, responseMsgId),
                Status = ThreadExecutionStatus.Executing,
                ActiveMessageId = responseMsgId,
                ExecutionStartedAt = DateTime.UtcNow.AddMinutes(-5),
                StreamingToolCalls = ImmutableList.Create(new ToolCallEntry
                {
                    Name = "search_nodes",
                    Result = null,
                    IsSuccess = false
                })
            }
        }, options).FirstAsync().ToTask(ct);

        await adapter.Write(new MeshNode(userMsgId, threadPath)
        {
            Name = "User msg",
            NodeType = ThreadMessageNodeType.NodeType,
            MainNode = "TestUser",
            CreatedBy = "TestUser",
            Content = new ThreadMessage
            {
                Role = "user", Text = "search for X",
                Timestamp = DateTime.UtcNow.AddMinutes(-5),
                Type = ThreadMessageType.ExecutedInput,
                Status = ThreadMessageStatus.Submitted, CreatedBy = "TestUser"
            }
        }, options).FirstAsync().ToTask(ct);

        await adapter.Write(new MeshNode(responseMsgId, threadPath)
        {
            Name = "Response (frozen)",
            NodeType = ThreadMessageNodeType.NodeType,
            MainNode = "TestUser",
            Content = new ThreadMessage
            {
                Role = "assistant", Text = "Allocating agent...",
                Timestamp = DateTime.UtcNow.AddMinutes(-5),
                Type = ThreadMessageType.AgentResponse,
                Status = ThreadMessageStatus.Streaming
            }
        }, options).FirstAsync().ToTask(ct);

        var client = await GetClientAsync();
        var workspace = client.GetWorkspace();
        var threadSyncStream = workspace
            .GetRemoteStream<MeshNode, MeshNodeReference>(new Address(threadPath), new MeshNodeReference());

        var settled = await threadSyncStream
            .Select(change => change.Value?.Content as MeshThread)
            .Where(t => t is { IsExecuting: false })
            .Take(1)
            .Timeout(RecoveryBudget)
            .ToTask(ct);

        settled.Should().NotBeNull();
        settled!.IsExecuting.Should().BeFalse(
            "single-thread cold-load: RecoverStaleExecutingThread must clear " +
            "IsExecuting on a thread left mid-stream in storage with no " +
            "PendingUserMessage. If this hangs, recovery never ran on cold " +
            "activation or its own write deadlocked the grain.");
    }

    /// <summary>
    /// Grain activation budget — the proof that
    /// <c>MessageHubGrain.OnActivateAsync</c> finished. Generous enough for
    /// a cold Orleans bring-up + assembly-store warm-up; well below the
    /// recovery budget so an activation hang is distinguishable from a
    /// recovery-stuck state.
    /// </summary>
    private static readonly TimeSpan ActivationBudget = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Recovery-must-finish budget. Init handlers run synchronously as part of
    /// activation; recovery's own write is async and goes through one cross-hub
    /// hop. 45 s is well past a healthy recovery's wall-clock and short enough
    /// that a deadlock surfaces as a loud test failure rather than blending
    /// into the test's overall timeout.
    /// </summary>
    private static readonly TimeSpan RecoveryBudget = TimeSpan.FromSeconds(45);
}

/// <summary>
/// Silo configurator for the cold-load test. Uses
/// <see cref="PersistenceExtensions.AddInMemoryPersistence{TBuilder}"/> (single
/// shared <see cref="InMemoryStorageAdapter"/>) so the test can seed via
/// <c>siloSp.GetRequiredService&lt;IStorageAdapter&gt;()</c>.
/// </summary>
public class ColdLoadInMemorySiloConfigurator : ISiloConfigurator, IHostConfigurator
{
    public void Configure(ISiloBuilder siloBuilder)
    {
        siloBuilder.ConfigureMeshWeaverServer()
            .AddMemoryGrainStorageAsDefault();
    }

    public void Configure(IHostBuilder hostBuilder)
    {
        hostBuilder.UseOrleansMeshServer()
            .AddInMemoryPersistence()
            .ConfigurePortalMesh()
            .AddGraph()
            .AddAI()
            .AddRowLevelSecurity()
            .AddMeshNodes(new MeshNode("TestUser", "User") { Name = "TestUser", NodeType = "User" })
            .AddMeshNodes(TestUserAdminAccess())
            .ConfigureServices(services =>
                services.AddSingleton<IChatClientFactory, ColdLoadNoOpChatClientFactory>())
            .ConfigureDefaultNodeHub(config => config.AddDefaultLayoutAreas());
    }

    private static MeshNode[] TestUserAdminAccess()
    {
        var assignment = new AccessAssignment
        {
            AccessObject = "TestUser",
            DisplayName = "Test User",
            Roles = [new RoleAssignment { Role = "Admin" }]
        };
        return [new("TestUser_Access", "User/_Access")
        {
            NodeType = "AccessAssignment",
            Name = "TestUser Access",
            Content = assignment,
            MainNode = "User",
        }];
    }
}

/// <summary>
/// Returns an <see cref="IChatClient"/> that immediately throws if invoked.
/// The cold-load tests should NEVER reach a streaming round on the seeded
/// stale threads — if they do, recovery's "skip auto-execute on PendingUserMessage
/// guard" misbehaved and the test surfaces the bug as a loud failure.
///
/// <para>NOTE: when an "auto-resume on cold load" path is added, this factory
/// will need to allow at least one round (e.g., return a short final
/// response) so the auto-resumed round can reach IsExecuting=false. Until
/// then the load-bearing invariant is "no streaming on the seeded shape".</para>
/// </summary>
internal class ColdLoadNoOpChatClientFactory : IChatClientFactory
{
    public string Name => "ColdLoadNoOpFactory";
    public IReadOnlyList<string> Models => ["coldload-noop"];
    public int Order => 0;

    public ChatClientAgent CreateAgent(
        AgentConfiguration config, IAgentChat chat,
        IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
        IReadOnlyList<AgentConfiguration> hierarchyAgents,
        string? modelName = null)
        => new(chatClient: new ColdLoadNoOpChatClient(),
            instructions: config.Instructions ?? "Test assistant.",
            name: config.Id, description: config.Description ?? config.Id,
            tools: [], loggerFactory: null, services: null);

    public Task<ChatClientAgent> CreateAgentAsync(
        AgentConfiguration config, IAgentChat chat,
        IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
        IReadOnlyList<AgentConfiguration> hierarchyAgents,
        string? modelName = null)
        => Task.FromResult(CreateAgent(config, chat, existingAgents, hierarchyAgents, modelName));
}

internal class ColdLoadNoOpChatClient : IChatClient
{
    public ChatClientMetadata Metadata => new("ColdLoadNoOp");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(
            "Cold-load test invoked a chat client. Recovery on activation should " +
            "have settled the stale thread without firing the streaming path. " +
            "When auto-resume lands, replace this with a fake that completes a " +
            "short round.");

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(
            "Cold-load test invoked the streaming chat client. Same reason as " +
            "GetResponseAsync above.");

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceType == typeof(IChatClient) ? this : null;

    public void Dispose() { }
}
