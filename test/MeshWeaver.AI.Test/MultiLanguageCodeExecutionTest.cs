#pragma warning disable CS1591
using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Kernel;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Hosting.Monolith.TestBase;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// A Code node executes ON THE MESH according to its <c>Language</c>. C# runs in-process on the
/// Activity hub's Roslyn kernel; a foreign language routes to the stable address where that language's
/// WORKER participant registers (<c>py/python-kernel</c> for python, <c>node/node-kernel</c> for
/// javascript/typescript — the workers live in <c>clients/python</c> / <c>clients/typescript</c>).
///
/// <para>These pin BOTH halves: the language→kernel dispatch (<see cref="CodeNodeType.ResolveKernelAddress"/>,
/// exercised for every language including the js/ts additions) and a real end-to-end C# run driving the
/// ActivityLog to <c>Succeeded</c> with its output — the same surface every language's output lands on.
/// (Real python/node <i>interpreter</i> execution is exercised by the worker SDK tests in
/// <c>clients/python</c> / <c>clients/typescript</c> and, once a gate is deployed, an integration run.)</para>
/// </summary>
public class MultiLanguageCodeExecutionTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Partition = "rbuergi";

    // ── the multi-language dispatch: each language → its runtime address ─────

    [Theory]
    [InlineData("python", "py/python-kernel")]        // → the python worker (clients/python)
    [InlineData("Python", "py/python-kernel")]        // language is case-insensitive
    [InlineData("javascript", "node/node-kernel")]    // → the node worker (clients/typescript)
    [InlineData("typescript", "node/node-kernel")]    // ts transpiles in the node worker
    [InlineData("TypeScript", "node/node-kernel")]
    public void ForeignLanguage_RoutesToItsWorkerKernel(string language, string expectedAddress)
        => CodeNodeType.ResolveKernelAddress(language, "acme/_Activity/run-1").ToString()
            .Should().Be(expectedAddress,
                $"a '{language}' Code node has no in-process runtime, so it dispatches to its language worker");

    [Theory]
    [InlineData("csharp")]     // the in-process Roslyn kernel
    [InlineData("CSharp")]
    [InlineData(null)]         // unset defaults to C#
    [InlineData("")]
    [InlineData("json")]       // non-executing languages also stay put
    [InlineData("sql")]
    [InlineData("markdown")]
    public void CSharpAndNonExecuting_RunInProcess_OnTheActivityHub(string? language)
    {
        const string activityPath = "acme/_Activity/run-1";
        CodeNodeType.ResolveKernelAddress(language, activityPath).ToString()
            .Should().Be(activityPath,
                "C# (and languages without a foreign runtime) run in-process on the Activity hub — never a worker");
    }

    // ── a real end-to-end run: C# Code node → executed → output on the ActivityLog ──

    [Fact(Timeout = 120000)]
    public async Task CSharpCodeNode_Executes_And_Surfaces_Output_On_The_ActivityLog()
    {
        var id = $"csharp-{Guid.NewGuid():N}";
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        await mesh.CreateNode(new MeshNode(id, Partition)
        {
            Name = "csharp cell", NodeType = "Code",
            Content = new CodeConfiguration
            {
                Language = "csharp", IsExecutable = true,
                Code = """
                    Console.WriteLine("multi-lang-csharp-ran");
                    6 * 7
                    """
            }
        }).Should().Within(30.Seconds()).Emit();

        var exec = (await Mesh.Observe(new ExecuteScriptRequest(), o => o.WithTarget(new Address($"{Partition}/{id}")))
            .Should().Within(60.Seconds()).Emit()).Message;
        exec.Success.Should().BeTrue(exec.Error ?? "exec failed");

        await Mesh.GetWorkspace().GetMeshNodeStream(exec.ActivityLog!)
            .Select(n => n?.Content as ActivityLog)
            .Should().Within(60.Seconds()).Match(l => l is not null
                && l.Status == ActivityStatus.Succeeded
                && l.Messages.Any(m => m.Message.Contains("multi-lang-csharp-ran")));
    }

    // ── no worker connected: a foreign-language run must FAIL fast, never hang on "Running…" ──

    [Theory(Timeout = 120000)]
    [InlineData("python")]      // → py/python-kernel   (the reported bug)
    [InlineData("javascript")]  // → node/node-kernel   (same defect class — js/ts route to a worker too)
    public async Task ForeignLanguageCodeNode_WithNoWorkerConnected_FailsTheActivity_NeverHangs(string language)
    {
        // No worker is registered at this language's kernel address in the test mesh. Running the node
        // must drive its ActivityLog to a TERMINAL Failed status (with a reason) rather than parking on
        // Running forever — the "nothing hangs" contract of Doc/Architecture/PythonCodeNodes. Before the
        // fix the dispatch fire-and-forgot the SubmitCodeRequest and the run stayed Running indefinitely.
        var id = $"{language}-noworker-{Guid.NewGuid():N}";
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        await mesh.CreateNode(new MeshNode(id, Partition)
        {
            Name = $"{language} cell (no worker)", NodeType = "Code",
            Content = new CodeConfiguration
            {
                Language = language, IsExecutable = true,
                Code = "should-not-run-without-a-worker"
            }
        }).Should().Within(30.Seconds()).Emit();

        var exec = (await Mesh.Observe(new ExecuteScriptRequest(), o => o.WithTarget(new Address($"{Partition}/{id}")))
            .Should().Within(60.Seconds()).Emit()).Message;
        // The run STARTS (the ActivityLog is created); the no-worker failure surfaces on that log,
        // exactly like an in-process C# script error does.
        exec.Success.Should().BeTrue(exec.Error ?? "the run should start");

        await Mesh.GetWorkspace().GetMeshNodeStream(exec.ActivityLog!)
            .Select(n => n?.Content as ActivityLog)
            .Should().Within(60.Seconds()).Match(l => l is not null
                && l.Status == ActivityStatus.Failed
                && l.HasErrors());
    }

    // ── a connected worker: the run reaches Succeeded (dispatch → worker → write-back → terminal) ──

    [Theory(Timeout = 120000)]
    [InlineData("python")]      // → py/python-kernel
    [InlineData("javascript")]  // → node/node-kernel
    public async Task ForeignLanguageCodeNode_WithConnectedWorker_ReachesSucceeded(string language)
    {
        // Stand up an in-process FAKE WORKER at the language's kernel address — the same stable address
        // a real gate registers at (CodeNodeType.ResolveKernelAddress). It patches the run's ActivityLog
        // to Succeeded, exactly as clients/python and clients/typescript do over the wire. This pins the
        // full .NET dispatch path end-to-end (only the interpreter is faked): the round-trip that had NO
        // coverage — which is how "foreign-language runs don't actually complete" went unnoticed.
        var kernelAddress = CodeNodeType.ResolveKernelAddress(language, "unused");
        const string marker = "connected-worker-ran";

        var routing = Mesh.ServiceProvider.GetRequiredService<IRoutingService>();
        var workerHub = Mesh.GetHostedHub(kernelAddress, config =>
        {
            config.TypeRegistry
                .WithType(typeof(SubmitCodeRequest), nameof(SubmitCodeRequest))
                .WithType(typeof(SubmitCodeResponse), nameof(SubmitCodeResponse));
            return config.WithHandler<SubmitCodeRequest>((worker, req) =>
            {
                // Patch the SAME Activity node every subscriber watches (the mesh-native write-back),
                // then reply for the request/response caller — mirrors the real worker's _write_back.
                var activityPath = req.Message.ActivityLogPath!;
                Mesh.GetWorkspace().GetMeshNodeStream(activityPath).Update(curr =>
                        curr.Content is ActivityLog log
                            ? curr with
                            {
                                Content = log with
                                {
                                    Status = ActivityStatus.Succeeded,
                                    End = DateTime.UtcNow,
                                    Messages = log.Messages.Add(new LogMessage(marker, LogLevel.Information))
                                }
                            }
                            : curr)
                    .Subscribe(_ => { }, _ => { });
                worker.Post(new SubmitCodeResponse(req.Message.Id, true), o => o.ResponseFor(req));
                return req.Processed();
            });
        }, HostedHubCreation.Always);
        // Make the kernel address routable to the fake worker — register it in the routing streams
        // (LOCAL_STREAM_HIT, resolved before node lookup), exactly as a real gate/participant does.
        // Without this the monolith router resolves 'py/…' via CreateHub → null → NotFound.
        routing.RegisterStream(workerHub!);

        var id = $"{language}-worker-{Guid.NewGuid():N}";
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        await mesh.CreateNode(new MeshNode(id, Partition)
        {
            Name = $"{language} cell (worker)", NodeType = "Code",
            Content = new CodeConfiguration { Language = language, IsExecutable = true, Code = "run" }
        }).Should().Within(30.Seconds()).Emit();

        var exec = (await Mesh.Observe(new ExecuteScriptRequest(), o => o.WithTarget(new Address($"{Partition}/{id}")))
            .Should().Within(60.Seconds()).Emit()).Message;
        exec.Success.Should().BeTrue(exec.Error ?? "exec failed");

        await Mesh.GetWorkspace().GetMeshNodeStream(exec.ActivityLog!)
            .Select(n => n?.Content as ActivityLog)
            .Should().Within(60.Seconds()).Match(l => l is not null
                && l.Status == ActivityStatus.Succeeded
                && l.Messages.Any(m => m.Message.Contains(marker)));
    }
}
