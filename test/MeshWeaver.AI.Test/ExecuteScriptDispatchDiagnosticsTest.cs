#pragma warning disable CS1591

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Pins issue #841 — <c>ExecuteScript</c> dispatch must never die silently.
///
/// <para>The reported symptom was "MCP <c>execute_script</c> answers <c>Dispatched</c>, the
/// activity node at the returned path never appears, and the pod emits nothing at Warning or
/// above." Three independent defects produced it, and each has its own test here:</para>
///
/// <list type="number">
///   <item><b>The caller GUESSED the activity path.</b> <c>MeshOperations.ExecuteScript</c>
///     returned <c>{partition}/_Activity/{submissionId}</c> reconstructed locally, while the
///     owning hub resolves the activity parent through <c>ActivityParentPath</c> / the
///     partition default / the <c>{viewer}</c> sentinel. A completely SUCCESSFUL run then
///     left the caller polling a path that would never exist —
///     <see cref="Dispatch_ViewerRoutedActivity_ReturnsThePathTheActivityActuallyLandsAt"/>.</item>
///   <item><b>The dispatch was fire-and-forget.</b> Every <c>Success = false</c> verdict the
///     owning hub posted — not executable, unreadable node, activity creation refused — went
///     to a caller that never observed it, as did any <c>DeliveryFailure</c> —
///     <see cref="Dispatch_NonExecutableNode_SurfacesRefusalToCaller"/>,
///     <see cref="Dispatch_MissingNode_SurfacesErrorToCaller"/>,
///     <see cref="Dispatch_ActivityCreationRefused_SurfacesFaultToCallerAndLogger"/>.</item>
///   <item><b>The handler logged nothing.</b> None of those branches wrote a log line, so with
///     the caller not listening the failure left no evidence anywhere. Every test below asserts
///     the Warning+ trace as well as the caller-visible verdict.</item>
/// </list>
///
/// <para>The fourth defect — an activity-parent lookup that never emits, ending the chain in
/// total silence — is pinned by <see cref="ResolveActivityParentTest"/>.</para>
/// </summary>
public class ExecuteScriptDispatchDiagnosticsTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string UserHome = "rbuergi";

    /// <summary>
    /// Captures what the mesh's own <see cref="ILoggerFactory"/> emits, so "the failure is
    /// visible at Warning+" is an assertion rather than a claim. Instance-owned (its lifetime
    /// is this test's mesh), never static.
    /// </summary>
    private readonly CapturingLoggerProvider logs = new();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services => services.AddSingleton<ILoggerProvider>(logs));

    /// <summary>Layout client so <c>GetMeshNodeStream</c> can follow the activity.</summary>
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    // ---- the caller must be told the path the run ACTUALLY landed at ---------------

    /// <summary>
    /// A Code node configured with <c>ActivityParentPath = "{viewer}"</c> writes its activity
    /// into the CALLER's home, not the Code node's partition. The old caller-side guess
    /// (<c>{codePartition}/_Activity/{id}</c>) is therefore wrong by construction — the run
    /// succeeds and the caller polls a path that never materialises, which is the reported
    /// "activity node is never created" exactly. The dispatch verdict must carry the owning
    /// hub's own answer.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task Dispatch_ViewerRoutedActivity_ReturnsThePathTheActivityActuallyLandsAt()
    {
        var codePath = await SeedCode(
            "Log.LogInformation(\"ping\"); 1", activityParentPath: "{viewer}");

        var verdict = await DispatchAsync(codePath);

        verdict.GetProperty("status").GetString().Should().Be("Dispatched", verdict.ToString());
        var activityPath = verdict.GetProperty("activityPath").GetString()!;

        activityPath.Should().StartWith($"{TestUsers.Admin.ObjectId}/_Activity/",
            "the reported path must be the one the OWNING hub resolved ({viewer} → the caller's "
            + "home); a caller-side guess from the Code node's partition points at a node that "
            + "will never exist");

        // …and there is really a node there, which really finishes.
        var log = (await GetClient().GetWorkspace()
            .GetMeshNodeStream(activityPath)
            .Select(n => n?.Content as ActivityLog)
            .Should().Within(60.Seconds())
            .Match(l => l is not null && l!.Status != ActivityStatus.Running))!;
        log.Status.Should().Be(ActivityStatus.Succeeded);
    }

    /// <summary>
    /// The plain success contract: a dispatch that answers <c>Dispatched</c> always names a real
    /// Activity node that reaches a terminal status.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task Dispatch_Succeeds_NamesAnActivityThatReachesATerminalStatus()
    {
        var codePath = await SeedCode("Log.LogInformation(\"working\"); 41 + 1");

        var verdict = await DispatchAsync(codePath);

        verdict.GetProperty("status").GetString().Should().Be("Dispatched", verdict.ToString());
        var activityPath = verdict.GetProperty("activityPath").GetString()!;
        activityPath.Should().StartWith($"{UserHome}/_Activity/");

        var log = (await GetClient().GetWorkspace()
            .GetMeshNodeStream(activityPath)
            .Select(n => n?.Content as ActivityLog)
            .Should().Within(60.Seconds())
            .Match(l => l is not null && l!.Status != ActivityStatus.Running))!;
        log.Status.Should().Be(ActivityStatus.Succeeded);
        log.Messages.Select(m => m.Message).Should().Contain(m => m.Contains("working"));
    }

    // ---- every failure branch reaches the caller AND the logger --------------------

    /// <summary>
    /// <c>IsExecutable = false</c>: the owning hub refuses before creating anything. The caller
    /// used to get <c>Dispatched</c> plus a path that would never exist; it must get the refusal.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task Dispatch_NonExecutableNode_SurfacesRefusalToCaller()
    {
        var codePath = await SeedCode("\"never runs\"", isExecutable: false);

        var verdict = await DispatchAsync(codePath);

        verdict.GetProperty("status").GetString().Should().Be("Error", verdict.ToString());
        verdict.GetProperty("message").GetString().Should().Contain("IsExecutable",
            "the caller must be told WHY, in terms of the thing they can fix");
        verdict.TryGetProperty("activityPath", out _).Should().BeFalse(
            "an error verdict must never hand the caller a path to poll — there is no activity");

        AssertLogged(LogLevel.Warning, "ExecuteScript refused");
    }

    /// <summary>
    /// A path with no node behind it. Whether routing answers with a DeliveryFailure or the
    /// per-node hub activates and finds nothing, the caller must end up with an error — never
    /// "Dispatched", never a hang.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task Dispatch_MissingNode_SurfacesErrorToCaller()
    {
        var ghost = $"{UserHome}/ghost-{Guid.NewGuid():N}";

        var verdict = await DispatchAsync(ghost, timeoutSeconds: 20, budget: 60);

        verdict.GetProperty("status").GetString().Should().Be("Error", verdict.ToString());
        verdict.TryGetProperty("activityPath", out _).Should().BeFalse();
    }

    /// <summary>
    /// The induced-failure pin for the deepest swallow point: activity CREATION fails.
    /// <c>Auth</c> is a system-managed mirror partition, so <c>PartitionWriteGuardValidator</c>
    /// refuses any interactive create there — routing a run's activity into it makes
    /// <c>IMeshService.CreateNode</c> throw inside the dispatch chain.
    ///
    /// <para>Before the fix that sink posted a <c>.Message</c>-only response to a caller who was
    /// not listening and logged NOTHING: no activity node, no response, nothing at Warning+ —
    /// the exact picture reported in #841. Now the caller gets the reason and the logger gets
    /// the exception object (type + stack), per the standard set in #892.</para>
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task Dispatch_ActivityCreationRefused_SurfacesFaultToCallerAndLogger()
    {
        var codePath = await SeedCode("1", activityParentPath: "Auth");

        var verdict = await DispatchAsync(codePath);

        verdict.GetProperty("status").GetString().Should().Be("Error", verdict.ToString());
        var message = verdict.GetProperty("message").GetString()!;
        message.Should().Contain("Could not start the script run");
        message.Should().Contain(nameof(UnauthorizedAccessException),
            "the wire error keeps the exception TYPE — '.Message' alone is what made this "
            + "class of failure undiagnosable");
        verdict.TryGetProperty("activityPath", out _).Should().BeFalse();

        var faulted = AssertLogged(LogLevel.Error, "ExecuteScript faulted");
        faulted.Exception.Should().NotBeNull(
            "the logger must receive the exception OBJECT so the stack reaches the operator");
        faulted.Exception!.Should().BeOfType<UnauthorizedAccessException>();
    }

    // ---- helpers -------------------------------------------------------------------

    private async Task<string> SeedCode(
        string code, bool isExecutable = true, string? activityParentPath = null)
    {
        var id = $"exec841-{Guid.NewGuid():N}";
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        await meshService.CreateNode(new MeshNode(id, UserHome)
        {
            Name = "Dispatch diagnostics",
            NodeType = "Code",
            Content = new CodeConfiguration
            {
                Code = code,
                IsExecutable = isExecutable,
                ActivityParentPath = activityParentPath
            }
        }).Should().Within(30.Seconds()).Emit();
        return $"{UserHome}/{id}";
    }

    private async Task<JsonElement> DispatchAsync(
        string path, int timeoutSeconds = 45, int budget = 90)
    {
        var json = await new MeshOperations(Mesh)
            .ExecuteScript(path, timeoutSeconds)
            .Should().Within(TimeSpan.FromSeconds(budget)).Emit();
        Output.WriteLine($"execute_script({path}) → {json}");
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private CapturedLog AssertLogged(LogLevel level, string fragment)
    {
        var match = logs.Records.FirstOrDefault(
            r => r.Level >= level && r.Message.Contains(fragment, StringComparison.Ordinal));
        match.Should().NotBeNull(
            $"a dispatch failure must leave a {level}+ trace containing '{fragment}' — the whole "
            + "point of #841 is that the pod emitted NOTHING at Warning or above. Captured: ["
            + string.Join(" | ", logs.Records.Select(r => $"{r.Level} {r.Category}: {r.Message}"))
            + "]");
        return match!;
    }

    // ---- log capture ---------------------------------------------------------------

    private sealed record CapturedLog(
        LogLevel Level, string Category, string Message, Exception? Exception);

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        // Instance field on an instance owned by the test's mesh — never static state.
        private readonly ConcurrentQueue<CapturedLog> records = new();

        public IReadOnlyList<CapturedLog> Records => records.ToArray();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, records);

        public void Dispose() { }

        private sealed class CapturingLogger(string category, ConcurrentQueue<CapturedLog> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
                => sink.Enqueue(new CapturedLog(
                    logLevel, category, formatter(state, exception), exception));
        }
    }
}

/// <summary>
/// Unit pins for <see cref="CodeNodeType.ResolveActivityParent"/> — the stage that decides where a
/// run's Activity node is written.
///
/// <para>It reads a LIVE workspace query (<c>PartitionRegistry.GetPartition</c>), and a live query
/// that never converges simply never emits. The dispatch chain used to be
/// <c>partitionStream.Take(1).SelectMany(CreateNode).Subscribe(…)</c> with no bound at all, so a
/// non-emitting lookup ended the whole run in perfect silence: no Activity node, no response, no
/// log line — indistinguishable from "still pending" forever. That is the wedges-to-zero
/// violation behind #841's distributed-only reproduction, and it is what
/// <see cref="PartitionLookupNeverEmits_ErrorsInsteadOfHangingForever"/> pins.</para>
/// </summary>
public class ResolveActivityParentTest
{
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(250);

    [Fact(Timeout = 30_000)]
    public async Task PartitionLookupNeverEmits_ErrorsInsteadOfHangingForever()
    {
        Func<Task> act = async () => await CodeNodeType.ResolveActivityParent(
                Observable.Never<PartitionDefinition?>(),
                codeActivityParentPath: null,
                viewerHome: "Roland",
                partitionRoot: "rbuergi",
                Budget)
            .FirstAsync()
            .ToTask();

        (await act.Should().ThrowAsync<TimeoutException>(
                "a partition lookup that never answers must surface — silence here means the "
                + "script run stops with no activity, no response and no log line"))
            .Which.Message.Should()
                .Contain("rbuergi", "the operator must be told WHICH partition stalled").And
                .Contain("no Activity node was created",
                    "…and what the consequence for the run was");
    }

    [Fact(Timeout = 30_000)]
    public async Task PartitionLookupCompletesEmpty_ResolvesToPartitionRoot()
    {
        var parent = await CodeNodeType.ResolveActivityParent(
                Observable.Empty<PartitionDefinition?>(),
                codeActivityParentPath: null,
                viewerHome: "Roland",
                partitionRoot: "rbuergi",
                Budget)
            .FirstAsync()
            .ToTask();

        parent.Should().Be("rbuergi",
            "a source that COMPLETES without emitting means 'no partition definition' — the "
            + "default case, not another way for the chain to end without an answer");
    }

    [Fact(Timeout = 30_000)]
    public async Task CodeNodeOverride_WinsOverPartitionDefault()
    {
        var parent = await CodeNodeType.ResolveActivityParent(
                Observable.Return<PartitionDefinition?>(
                    new PartitionDefinition { Namespace = "Doc", DefaultActivityParentPath = "Doc/Runs" }),
                codeActivityParentPath: "Somewhere/Else",
                viewerHome: "Roland",
                partitionRoot: "Doc",
                Budget)
            .FirstAsync()
            .ToTask();

        parent.Should().Be("Somewhere/Else");
    }

    [Fact(Timeout = 30_000)]
    public async Task ViewerSentinel_OnThePartitionDefault_ExpandsToTheCallersHome()
    {
        var parent = await CodeNodeType.ResolveActivityParent(
                Observable.Return<PartitionDefinition?>(
                    new PartitionDefinition { Namespace = "Doc", DefaultActivityParentPath = "{viewer}" }),
                codeActivityParentPath: null,
                viewerHome: "Roland",
                partitionRoot: "Doc",
                Budget)
            .FirstAsync()
            .ToTask();

        parent.Should().Be("Roland");
    }

    [Fact(Timeout = 30_000)]
    public async Task ViewerSentinel_WithNoViewerIdentity_FallsBackToPartitionRoot()
    {
        var parent = await CodeNodeType.ResolveActivityParent(
                Observable.Return<PartitionDefinition?>(null),
                codeActivityParentPath: "{viewer}",
                viewerHome: null,
                partitionRoot: "Doc",
                Budget)
            .FirstAsync()
            .ToTask();

        parent.Should().Be("Doc");
    }
}
