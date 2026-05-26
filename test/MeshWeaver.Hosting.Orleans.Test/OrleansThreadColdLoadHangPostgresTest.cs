using System;
using System.Collections.Generic;
using System.Collections.Immutable;
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
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Hosting.PostgreSql;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;
using Orleans.TestingHost;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Production-shape variant of <see cref="OrleansThreadColdLoadHangTest"/>:
/// same parent + sub-thread cold-load repro, but wired against a running local
/// Postgres container via <see cref="AddPartitionedPostgreSqlPersistence"/> —
/// the exact persistence stack the prod portal uses.
///
/// <para>This is the test that actually reproduces the user's prod symptom
/// (2026-05-20) — opening the URL of a sub-thread that was mid-execution at
/// the time of a portal restart hangs the browser. The in-memory variant is
/// a fast regression guard; this one catches PG-specific failure modes that
/// in-memory misses: satellite-table routing, RLS context on init, schema
/// discovery during recovery, pg_notify lag for the recovery write.</para>
///
/// <para>Skipped when <c>MESHWEAVER_LOCAL_PG_CS</c> isn't set. Run locally
/// against the Aspire <c>memex-postgres</c> container:</para>
/// <code>
/// set MESHWEAVER_LOCAL_PG_CS=Host=127.0.0.1;Port=...;Database=memex;Username=postgres;Password=...
/// set MESHWEAVER_LOCAL_USER=rbuergi   # the partition root to seed under
/// dotnet test test/MeshWeaver.Hosting.Orleans.Test --filter "FullyQualifiedName~OrleansThreadColdLoadHangPostgresTest"
/// </code>
/// </summary>
public class OrleansThreadColdLoadHangPostgresTest(ITestOutputHelper output) : TestBase(output)
{
    private const string ConnectionStringEnvVar = "MESHWEAVER_LOCAL_PG_CS";

    private TestCluster? Cluster { get; set; }
    private IMessageHub ClientMesh => Cluster!.Client.ServiceProvider.GetRequiredService<IMessageHub>();

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(ConnectionStringEnvVar)))
            return;

        var builder = new TestClusterBuilder();
        builder.Options.InitialSilosCount = 1;
        builder.AddSiloBuilderConfigurator<ColdLoadPostgresSiloConfigurator>();
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

    private async Task<IMessageHub> GetClientAsync(
        string userPartition, [CallerMemberName] string? name = null)
    {
        var client = ClientMesh.ServiceProvider.CreateMessageHub(
            new Address("client", $"coldload-pg-{name}-{Guid.NewGuid():N}"),
            config =>
            {
                config.TypeRegistry.AddAITypes();
                return config.AddLayoutClient();
            });
        var accessService = client.ServiceProvider.GetRequiredService<AccessService>();
        accessService.SetCircuitContext(new AccessContext
        {
            ObjectId = userPartition, Name = userPartition,
            Email = $"{userPartition}@meshweaver.io"
        });
        Cluster!.Client.ServiceProvider.GetRequiredService<IRoutingService>()
            .RegisterStream(client.Address, client.DeliverMessage);
        return client;
    }

    /// <summary>
    /// Seeds the parent thread + sub-thread shape directly via the silo's
    /// <see cref="IStorageAdapter"/>. The adapter routes by path → satellite
    /// table, so Thread/ThreadMessage rows land in the <c>threads</c> table
    /// under the user's partition schema — the same layout the prod portal
    /// reads from.
    /// </summary>
    private async Task<SeededPaths> SeedParentAndSubThreadAsync(
        string userPartition, CancellationToken ct)
    {
        var siloSp = ((InProcessSiloHandle)Cluster!.Primary).SiloHost.Services;
        var adapter = siloSp.GetRequiredService<IStorageAdapter>();
        var options = JsonSerializerOptions.Default;

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var parentId = $"pgcold-parent-{suffix}";
        var parentRespId = $"presp{suffix[..4]}";
        var parentUserId = $"puser{suffix[..4]}";
        var subId = $"pgcold-sub-{suffix}";
        var subRespId = $"sresp{suffix[..4]}";
        var subUserId = $"suser{suffix[..4]}";

        var parentThreadPath = $"{userPartition}/_Thread/{parentId}";
        var parentRespPath = $"{parentThreadPath}/{parentRespId}";
        var subThreadPath = $"{parentRespPath}/{subId}";
        var subRespPath = $"{subThreadPath}/{subRespId}";

        await adapter.Write(new MeshNode(parentId, $"{userPartition}/_Thread")
        {
            Name = "PG cold-load parent (frozen mid-delegation)",
            NodeType = ThreadNodeType.NodeType,
            MainNode = userPartition,
            CreatedBy = userPartition,
            Content = new MeshThread
            {
                CreatedBy = userPartition,
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
            Name = "Parent user msg",
            NodeType = ThreadMessageNodeType.NodeType,
            MainNode = userPartition,
            CreatedBy = userPartition,
            Content = new ThreadMessage
            {
                Role = "user", Text = "Please delegate.",
                Timestamp = DateTime.UtcNow.AddMinutes(-5),
                Type = ThreadMessageType.ExecutedInput,
                Status = ThreadMessageStatus.Submitted, CreatedBy = userPartition
            }
        }, options).FirstAsync().ToTask(ct);

        await adapter.Write(new MeshNode(parentRespId, parentThreadPath)
        {
            Name = "Parent response (frozen)",
            NodeType = ThreadMessageNodeType.NodeType,
            MainNode = userPartition,
            Content = new ThreadMessage
            {
                Role = "assistant", Text = "Delegating to Worker...",
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

        await adapter.Write(new MeshNode(subId, parentRespPath)
        {
            Name = "Sub-thread (frozen)",
            NodeType = ThreadNodeType.NodeType,
            MainNode = userPartition,
            CreatedBy = userPartition,
            Content = new MeshThread
            {
                CreatedBy = userPartition,
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
            MainNode = userPartition,
            CreatedBy = userPartition,
            Content = new ThreadMessage
            {
                Role = "user", Text = "do some work that hangs",
                Timestamp = DateTime.UtcNow.AddMinutes(-5),
                Type = ThreadMessageType.ExecutedInput,
                Status = ThreadMessageStatus.Submitted, CreatedBy = userPartition
            }
        }, options).FirstAsync().ToTask(ct);

        await adapter.Write(new MeshNode(subRespId, subThreadPath)
        {
            Name = "Sub-thread response (frozen)",
            NodeType = ThreadMessageNodeType.NodeType,
            MainNode = userPartition,
            Content = new ThreadMessage
            {
                Role = "assistant", Text = "Allocating agent...",
                Timestamp = DateTime.UtcNow.AddMinutes(-5),
                Type = ThreadMessageType.AgentResponse,
                Status = ThreadMessageStatus.Streaming
            }
        }, options).FirstAsync().ToTask(ct);

        Output.WriteLine($"PG seed complete: parent={parentThreadPath}, sub={subThreadPath}.");
        return new SeededPaths(parentThreadPath, parentRespPath, subThreadPath, subRespPath);
    }

    private sealed record SeededPaths(
        string ParentThreadPath,
        string ParentResponsePath,
        string SubThreadPath,
        string SubResponsePath);

    /// <summary>
    /// PG cold-load repro of the exact prod symptom: open the sub-thread URL
    /// after a portal restart, expect activation + settle, NOT deadlock.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task SubThreadUrl_LoadedColdFromPostgres_ActivatesAndSettlesWithoutDeadlock()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(ConnectionStringEnvVar)))
        {
            Output.WriteLine(
                $"SKIPPED: set ${ConnectionStringEnvVar} to a running Postgres connection " +
                "string (e.g. the local Aspire `memex-postgres` container).");
            return;
        }

        var ct = new CancellationTokenSource(150.Seconds()).Token;
        var userPartition = Environment.GetEnvironmentVariable("MESHWEAVER_LOCAL_USER") ?? "rbuergi";

        var paths = await SeedParentAndSubThreadAsync(userPartition, ct);

        var client = await GetClientAsync(userPartition);
        var workspace = client.GetWorkspace();

        // Activate the sub-thread — the user-stuck URL pattern.
        Output.WriteLine($"Activating sub-thread {paths.SubThreadPath} via PG...");
        var subStream = workspace
            .GetRemoteStream<MeshNode, MeshNodeReference>(
                new Address(paths.SubThreadPath), new MeshNodeReference());

        var firstSubEmission = await subStream
            .Where(change => change.Value is not null)
            .Take(1)
            .Timeout(PgActivationBudget)
            .ToTask(ct);
        firstSubEmission.Should().NotBeNull(
            "sub-thread grain must activate cold from the PG-persisted node. " +
            "If this times out, the prod symptom is reproduced: OnActivateAsync " +
            "wedged before any emission.");

        var subSettled = await subStream
            .Select(change => change.Value?.Content as MeshThread)
            .Where(t => t is { IsExecuting: false })
            .Take(1)
            .Timeout(PgRecoveryBudget)
            .ToTask(ct);
        subSettled!.IsExecuting.Should().BeFalse(
            "PG cold-load BUG REPRO: sub-thread URL hangs on load (user report 2026-05-20). " +
            "Either RecoverStaleExecutingThread didn't fire under PG RLS context, " +
            "or recovery's own UpdateMeshNode write through the partition-routing " +
            "stack deadlocked the grain.");

        // Parent must also settle without deadlock.
        var parentStream = workspace
            .GetRemoteStream<MeshNode, MeshNodeReference>(
                new Address(paths.ParentThreadPath), new MeshNodeReference());
        var parentSettled = await parentStream
            .Select(change => change.Value?.Content as MeshThread)
            .Where(t => t is { IsExecuting: false })
            .Take(1)
            .Timeout(PgRecoveryBudget)
            .ToTask(ct);
        parentSettled!.IsExecuting.Should().BeFalse(
            "PG cold-load: parent thread must also settle on activation.");

        Output.WriteLine("PASSED — PG cold-load: both parent and sub-thread settled.");
    }

    /// <summary>
    /// PG cold activation needs more headroom than in-memory: Orleans bring-up +
    /// partition schema discovery + first PG round-trip on a fresh container
    /// can take 30 s before any emission lands.
    /// </summary>
    private static readonly TimeSpan PgActivationBudget = TimeSpan.FromSeconds(45);

    /// <summary>
    /// PG recovery budget. Recovery is a one-shot non-blocking write but the
    /// PG round-trip + pg_notify + workspace catalog refresh can take a few
    /// seconds on a cold container. 90 s is well past healthy and short
    /// enough to fail loud on deadlock.
    /// </summary>
    private static readonly TimeSpan PgRecoveryBudget = TimeSpan.FromSeconds(90);
}

/// <summary>
/// Silo configurator mirroring <c>OrleansPostgresPathResolutionTest</c>'s
/// production-shape PG wiring plus AI (so Thread/ThreadMessage NodeTypes are
/// registered and the init handler chain runs on activation).
/// </summary>
public class ColdLoadPostgresSiloConfigurator : ISiloConfigurator, IHostConfigurator
{
    private const string ConnectionStringEnvVar = "MESHWEAVER_LOCAL_PG_CS";

    public static readonly string AssemblyStoreRoot =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"mw-orleans-coldload-pg-{Guid.NewGuid():N}");

    public void Configure(ISiloBuilder siloBuilder)
    {
        siloBuilder.ConfigureMeshWeaverServer()
            .AddMemoryGrainStorageAsDefault();
        siloBuilder.ConfigureServices(services =>
            services.AddFileSystemAssemblyStore(AssemblyStoreRoot));
    }

    public void Configure(IHostBuilder hostBuilder)
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringEnvVar)
            ?? "Host=localhost;Database=test;Username=postgres;Password=postgres";

        hostBuilder.UseOrleansMeshServer()
            .ConfigureServices(services => services.AddPartitionedPostgreSqlPersistence(connectionString))
            .ConfigurePortalMesh()
            .AddAI()
            .AddRowLevelSecurity()
            .ConfigureServices(services =>
                services.AddSingleton<IChatClientFactory, ColdLoadNoOpChatClientFactory>());
    }
}
