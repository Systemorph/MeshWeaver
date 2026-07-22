using System;
using System.Linq;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Kernel;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Pins the kernel's REPL contract across submissions now that the executor owns emit + load
/// through <c>ScriptSession</c>'s collectible AssemblyLoadContext (instead of
/// <c>ScriptState.ContinueWithAsync</c>): block #2 must see block #1's <b>variables</b>,
/// <b>functions</b>, and — the riskiest binding path — <b>types</b> defined in an earlier
/// submission's assembly, resolved across submission assemblies inside the session's load
/// context. A regression here means the manual submission-state protocol (slot 0 = globals,
/// slot N+1 = submission #N) or the context's by-name assembly resolution broke.
/// </summary>
public class KernelReplChainingTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    [Fact(Timeout = 120_000)]
    public async Task SecondSubmission_SeesFirstSubmissionsVariablesFunctionsAndTypes()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var kernelId = Guid.NewGuid().ToString("N");
        const string ownerPath = "rbuergi";
        var activityNamespace = $"{ownerPath}/_Activity";
        var activityNode = new MeshNode($"replchain-{kernelId}", activityNamespace)
        {
            Name = "REPL chaining probe",
            NodeType = "Activity",
            MainNode = ownerPath,
            State = MeshNodeState.Active,
            Content = new ActivityLog("KernelExecution") { Status = ActivityStatus.Running }
        };
        await meshService.CreateNode(activityNode).Should().Within(60.Seconds()).Emit();
        var kernelAddress = new Address($"{activityNamespace}/replchain-{kernelId}");

        var client = GetClient();

        IObservable<ActivityLog> LogWith(string marker) => client.GetWorkspace()
            .GetMeshNodeStream(kernelAddress.Path)
            .Select(change => change?.Content as ActivityLog)
            .Where(log => log is not null && log!.Messages.Any(m => m.Message.Contains(marker)))!;

        // Block #1: a variable, a function, and a TYPE — each lives in submission #0's assembly.
        client.Post(
            new SubmitCodeRequest(
                """
                var theAnswer = 40;
                int Bump(int v) => v + 1;
                class Chained { public int One => 1; }
                Console.WriteLine("repl-block1-done");
                """),
            o => o.WithTarget(kernelAddress));
        await LogWith("repl-block1-done").Should().Within(60.Seconds()).Emit();

        // Block #2 (submission #1) reads all three across the assembly boundary.
        client.Post(
            new SubmitCodeRequest(
                """Console.WriteLine($"repl-chain-{Bump(theAnswer) + new Chained().One}");"""),
            o => o.WithTarget(kernelAddress));
        await LogWith("repl-chain-42").Should().Within(60.Seconds()).Emit();
    }
}
