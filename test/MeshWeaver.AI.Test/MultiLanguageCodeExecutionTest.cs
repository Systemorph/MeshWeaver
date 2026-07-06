#pragma warning disable CS1591
using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Hosting.Monolith.TestBase;
using Microsoft.Extensions.DependencyInjection;
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
}
