using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
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
/// A cell whose last expression is a CONTROL renders that control; it must not ALSO dump the
/// control's <c>ToString()</c> into the cell's output.
///
/// <para><c>KernelExecutor</c> hands a non-null return value to <c>UpdateView</c>, which renders
/// it. Logging the same object as well printed its record shape beside the rendered thing —
/// "StackControl { Id = , Style = , Skins = System.Collections.Immutable.ImmutableList`1[…] }" —
/// observed under the loss-book grid on <c>RiskTransfer/01-GrossToNet</c>.</para>
///
/// <para>The log line is still the RIGHT behaviour for every other result: for <c>1 + 1</c> it is
/// the only output the learner sees. Both directions are asserted, because suppressing the control
/// dump by silencing the logger outright would be the easy wrong fix.</para>
///
/// <para>🚨 Each cell gets its OWN activity node, and the assertion waits for that activity to
/// reach a TERMINAL status before reading. The activity log is REPLACED per submission, not
/// accumulated: running two cells against one activity and reading afterwards inspects only the
/// second cell's log, so the assertion about the first passes while never seeing its output.</para>
/// </summary>
public class KernelControlResultNotLoggedTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    [Fact(Timeout = 180_000)]
    public async Task ControlResult_IsRendered_ButNotDumpedIntoTheOutput()
    {
        var controlLog = await RunCellAsync(
            "ctrl",
            """
            MeshWeaver.Layout.Controls.Stack
                .WithView(MeshWeaver.Layout.Controls.Markdown("rendered, not dumped"))
            """);

        var scalarLog = await RunCellAsync(
            "scalar",
            """
            $"scalar-result-{40 + 2}-marker"
            """);

        Output.WriteLine($"control cell log ({controlLog.Length}):");
        foreach (var m in controlLog) Output.WriteLine($"   {(m.Length > 160 ? m[..160] + "…" : m)}");
        Output.WriteLine($"scalar cell log ({scalarLog.Length}):");
        foreach (var m in scalarLog) Output.WriteLine($"   {(m.Length > 160 ? m[..160] + "…" : m)}");

        // The scalar direction FIRST: it proves result-logging reached this log at all. Without it,
        // an empty log would satisfy the control assertion for entirely the wrong reason.
        scalarLog.Should().Contain(m => m.Contains("scalar-result-42-marker"),
            "a non-control result is still logged — for a scalar cell that line IS the output");

        controlLog.Should().NotContain(m => m.Contains("StackControl {"),
            "a control is rendered by UpdateView — logging its ToString() as well prints the "
            + "record's field list into the output pane beside the rendered control");
    }

    /// <summary>Runs one cell on a fresh activity and returns that activity's settled messages.</summary>
    private async Task<string[]> RunCellAsync(string label, string code)
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var id = $"{label}-{Guid.NewGuid():N}";
        const string ownerPath = "rbuergi";
        var activityNamespace = $"{ownerPath}/_Activity";
        var node = new MeshNode(id, activityNamespace)
        {
            Name = $"Control-result logging probe ({label})",
            NodeType = "Activity",
            MainNode = ownerPath,
            State = MeshNodeState.Active,
            Content = new ActivityLog("KernelExecution") { Status = ActivityStatus.Running }
        };
        await meshService.CreateNode(node).Should().Within(60.Seconds()).Emit();

        var client = GetClient();
        var address = new Address($"{activityNamespace}/{id}");

        // Settle on the TERMINAL status — the result line is written after the script returns, so
        // waiting on any earlier marker reads the log before the value under test is in it.
        var settled = client.GetWorkspace()
            .GetMeshNodeStream(address.Path)
            .Select(n => n?.Content as ActivityLog)
            .Where(l => l is not null && l!.Status != ActivityStatus.Running)
            .Select(l => l!.Messages.Select(m => m.Message).ToArray())
            .FirstAsync()
            .ToTask();

        client.Post(new SubmitCodeRequest(code), o => o.WithTarget(address));
        return await settled.WaitAsync(TimeSpan.FromSeconds(120));
    }
}
