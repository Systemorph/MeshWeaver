#pragma warning disable CS1591

using System;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Pins the notebook-cell layout of a Code node's Content area:
/// <list type="number">
///   <item>The cell frame contains a toolbar with the Run button (and Edit) when
///   the node is executable — no run yet means no Cancel and no activity embed,
///   just the subtle "not yet run" hint in the output segment.</item>
///   <item>After an <see cref="ExecuteScriptRequest"/>, the cell's output segment
///   embeds the new activity's Progress area (a <see cref="LayoutAreaControl"/>
///   pointing at the activity path) directly beneath the code, inside the frame.</item>
///   <item>While the activity is Running the toolbar shows Cancel; once the run
///   leaves Running (here: via the canonical <c>hub.CancelActivity</c>) the
///   Cancel button reactively disappears.</item>
/// </list>
/// All waits are reactive (<c>Should().Within(...).Match(...)</c> on the layout
/// control stream / mesh-node stream) — no sleeps, no polling.
/// </summary>
public class CodeCellLayoutTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string UserHome = "rbuergi";

    /// <summary>
    /// Client hub subscribes to layout areas + remote MeshNode streams — both
    /// require AddData on the client; AddLayoutClient wires it.
    /// </summary>
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    private async Task<string> SeedExecutableCode(string code)
    {
        var id = $"cell-{Guid.NewGuid():N}";
        var path = $"{UserHome}/{id}";
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        await mesh.CreateNode(new MeshNode(id, UserHome)
        {
            Name = "Notebook cell demo",
            NodeType = "Code",
            Content = new CodeConfiguration
            {
                Code = code,
                IsExecutable = true,
            }
        }).Should().Within(30.Seconds()).Emit();
        return path;
    }

    /// <summary>
    /// Resolves the absolute area path of the child area named <paramref name="id"/>
    /// inside a rendered container (rendered areas carry absolute paths like
    /// <c>Content/CodeCell/CellToolbar</c>).
    /// </summary>
    private static string FindArea(IContainerControl container, string id)
    {
        var match = container.Areas
            .Select(a => a.Area?.ToString())
            .FirstOrDefault(a => a is not null && (a == id || a.EndsWith("/" + id, StringComparison.Ordinal)));
        match.Should().NotBeNull(
            $"container should contain an area '{id}' — found: " +
            $"[{string.Join(", ", container.Areas.Select(a => a.Area))}]");
        return match!;
    }

    private static bool HasArea(IContainerControl container, string id) =>
        container.Areas.Any(a => a.Area?.ToString() is { } s
            && (s == id || s.EndsWith("/" + id, StringComparison.Ordinal)));

    [Fact(Timeout = 60000)]
    public async Task Toolbar_ContainsRunAndEdit_NoCancel_NoOutputEmbed_BeforeFirstRun()
    {
        var codePath = await SeedExecutableCode("\"hi\"");
        var workspace = GetClient().GetWorkspace();
        var reference = new LayoutAreaReference(CodeLayoutAreas.ContentArea);
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new Address(codePath), reference);

        // Root stack contains the cell frame.
        var root = (StackControl)(await stream.GetControlStream(reference.Area!)
            .Should().Within(30.Seconds()).Match(c => c is StackControl s
                && HasArea(s, CodeLayoutAreas.CellArea)))!;

        // Cell frame: toolbar + code + output segments.
        var cell = (StackControl)(await stream
            .GetControlStream(FindArea(root, CodeLayoutAreas.CellArea))
            .Should().Within(10.Seconds()).Match(c => c is StackControl))!;
        HasArea(cell, CodeLayoutAreas.CellToolbarArea).Should().BeTrue("the cell carries its own toolbar");
        HasArea(cell, CodeLayoutAreas.CellCodeArea).Should().BeTrue("the code sits inside the cell frame");
        HasArea(cell, CodeLayoutAreas.CellOutputArea).Should().BeTrue(
            "an executable cell always has an output segment attached beneath the code");

        // (a) Toolbar contains Run (and Edit); no Cancel while nothing runs.
        var toolbarArea = FindArea(cell, CodeLayoutAreas.CellToolbarArea);
        var toolbar = (StackControl)(await stream.GetControlStream(toolbarArea)
            .Should().Within(10.Seconds()).Match(c => c is StackControl))!;
        HasArea(toolbar, CodeLayoutAreas.RunButtonArea).Should().BeTrue(
            "executable Code node must surface Run in the cell toolbar");
        HasArea(toolbar, CodeLayoutAreas.EditButtonArea).Should().BeTrue(
            "Edit moved into the cell toolbar as well");
        HasArea(toolbar, CodeLayoutAreas.CancelButtonArea).Should().BeFalse(
            "no activity is running — Cancel must not render");

        var runControl = await stream
            .GetControlStream(FindArea(toolbar, CodeLayoutAreas.RunButtonArea))
            .Should().Within(10.Seconds()).Match(c => c is not null);
        runControl.Should().BeOfType<ButtonControl>("Run renders as a button control");

        // Before the first run the output segment is the subtle hint,
        // NOT an activity embed (and not a large empty pane).
        var outputControl = await stream
            .GetControlStream(FindArea(cell, CodeLayoutAreas.CellOutputArea))
            .Should().Within(10.Seconds()).Match(c => c is not null);
        outputControl.Should().NotBeOfType<LayoutAreaControl>(
            "no run happened yet, so there is no activity Progress area to embed");
    }

    [Fact(Timeout = 120000)]
    public async Task Run_EmbedsProgressBeneathCode_AndTogglesCancelWithActivityStatus()
    {
        // Cancellable long-runner: Task.Delay(…, Ct) is interrupted the moment
        // the control-plane cancel lands, so the generous delay costs nothing
        // when cancellation works and surfaces a real failure when it doesn't.
        var codePath = await SeedExecutableCode("""
            Log.LogInformation("cell starting");
            await System.Threading.Tasks.Task.Delay(15000, Ct);
            "done"
            """);

        var client = GetClient();
        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(CodeLayoutAreas.ContentArea);
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new Address(codePath), reference);

        // Area alive before the run (also resolves the stable cell area path).
        var root = (StackControl)(await stream.GetControlStream(reference.Area!)
            .Should().Within(30.Seconds()).Match(c => c is StackControl s
                && HasArea(s, CodeLayoutAreas.CellArea)))!;
        var cellArea = FindArea(root, CodeLayoutAreas.CellArea);

        var exec = (await Mesh.Observe(
                new ExecuteScriptRequest(),
                o => o.WithTarget(new Address(codePath)))
            .Should().Within(60.Seconds()).Emit()).Message;
        exec.Success.Should().BeTrue(exec.Error ?? "exec failed");
        var activityPath = exec.ActivityLog!;

        // (b) The output segment embeds THIS activity's Progress area inside the
        // cell frame — the LastActivityPath stamp re-renders the area reactively.
        var cell = (StackControl)(await stream.GetControlStream(cellArea)
            .Should().Within(30.Seconds()).Match(c => c is StackControl s
                && HasArea(s, CodeLayoutAreas.CellOutputArea)))!;
        var outputArea = FindArea(cell, CodeLayoutAreas.CellOutputArea);
        var outputControl = (LayoutAreaControl)(await stream.GetControlStream(outputArea)
            .Should().Within(30.Seconds()).Match(c => c is LayoutAreaControl l
                && l.Address.ToString() == activityPath
                && l.Reference.Area == ActivityLayoutAreas.ProgressArea))!;
        Output.WriteLine($"Output segment embeds Progress of {outputControl.Address}");

        // (c) Cancel is present in the cell toolbar while the activity is Running.
        var toolbarArea = FindArea(cell, CodeLayoutAreas.CellToolbarArea);
        var runningToolbar = (StackControl)(await stream.GetControlStream(toolbarArea)
            .Should().Within(30.Seconds()).Match(c => c is StackControl s
                && HasArea(s, CodeLayoutAreas.CancelButtonArea)))!;
        HasArea(runningToolbar, CodeLayoutAreas.RunButtonArea).Should().BeTrue(
            "Run stays available in the toolbar during a run");
        var cancelControl = await stream
            .GetControlStream(FindArea(runningToolbar, CodeLayoutAreas.CancelButtonArea))
            .Should().Within(10.Seconds()).Match(c => c is not null);
        cancelControl.Should().BeOfType<ButtonControl>("Cancel renders as a button control");

        // Cancel via the canonical extension (same call the toolbar button makes),
        // then the activity leaves Running …
        client.CancelActivity(activityPath);
        await workspace.GetMeshNodeStream(activityPath)
            .Select(n => n?.Content as ActivityLog)
            .Should().Within(30.Seconds())
            .Match(l => l is not null && l.Status != ActivityStatus.Running);

        // … and the toolbar reactively drops the Cancel button.
        await stream.GetControlStream(toolbarArea)
            .Should().Within(30.Seconds()).Match(c => c is StackControl s
                && !HasArea(s, CodeLayoutAreas.CancelButtonArea)
                && HasArea(s, CodeLayoutAreas.RunButtonArea));
    }
}
