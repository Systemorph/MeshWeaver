#pragma warning disable CS1591

using System;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Graph.Security;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Root-cause coverage for the "Worked Example shows an eternal spinner" class
/// of defects — the full chain from a script's <c>Console.Write*</c> to the
/// Progress area a VIEWER sees:
/// <list type="number">
///   <item><b>Capture</b>: a script's <c>Console.WriteLine</c> AND
///   <c>Console.Error.WriteLine</c> land as ActivityLog messages (Information /
///   Error respectively). Error capture was missing — only Console.Out was
///   hooked; stderr prints vanished into the host process.</item>
///   <item><b>Stamp + embed</b>: after <see cref="ExecuteScriptRequest"/> the
///   Code node's <c>LastActivityPath</c> is stamped and the Content area's
///   output segment embeds the Progress area of exactly THAT path.</item>
///   <item><b>Cross-user render</b>: the Progress area of a Succeeded activity
///   emits the output lines (not a spinner) for a DIFFERENT user than the
///   runner — a role-less public-read viewer AND the anonymous VUser. Satellite
///   access delegates to the activity's MainNode (the partition root), whose
///   PublicRead policy must admit both.</item>
///   <item><b>The rule defect</b>: <see cref="SatelliteAccessRule"/> used to
///   hard-deny a NULL identity instead of evaluating it as Anonymous — making
///   every satellite stricter than its MainNode (a contextless viewer could
///   read the Code node but never its `_Activity`, so the output pane spun
///   forever). Pinned directly against the real evaluator.</item>
/// </list>
/// Uses <see cref="MonolithMeshTestBase.ConfigureMeshBase"/> (no blanket
/// public-admin) + a static PublicRead policy on the partition, so the
/// cross-user cases exercise the REAL public-read path.
/// </summary>
public class CodeCellOutputCaptureTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Partition = "rbuergi";
    private const string PublicViewer = "Bob";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            // Static PublicRead policy on the partition: EVERY user — including
            // Anonymous — may Read the partition and (via SatelliteAccessRule's
            // MainNode delegation) its activity satellites. No write grants.
            .AddMeshNodes(new MeshNode("_Policy", Partition)
            {
                NodeType = "PartitionAccessPolicy",
                Name = "Public read",
                Content = new PartitionAccessPolicy { PublicRead = true }
            });

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    private async Task<string> SeedExecutableCode(string code)
    {
        var id = $"capture-{Guid.NewGuid():N}";
        var path = $"{Partition}/{id}";
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        await mesh.CreateNode(new MeshNode(id, Partition)
        {
            Name = "Console capture cell",
            NodeType = "Code",
            Content = new CodeConfiguration { Code = code, IsExecutable = true }
        }).Should().Within(30.Seconds()).Emit();
        return path;
    }

    private async Task<string> RunScript(string codePath)
    {
        var exec = (await Mesh.Observe(
                new ExecuteScriptRequest(),
                o => o.WithTarget(new Address(codePath)))
            .Should().Within(60.Seconds()).Emit()).Message;
        exec.Success.Should().BeTrue(exec.Error ?? "exec failed");
        return exec.ActivityLog!;
    }

    // ── (i) console capture: Out AND Error ─────────────────────────────────

    [Fact(Timeout = 120000)]
    public async Task Script_ConsoleOut_And_ConsoleError_Land_On_ActivityLog()
    {
        var codePath = await SeedExecutableCode("""
            Console.WriteLine("out-line-capture");
            Console.Error.WriteLine("err-line-capture");
            "done"
            """);
        var activityPath = await RunScript(codePath);

        var workspace = GetClient().GetWorkspace();
        var log = (await workspace.GetMeshNodeStream(activityPath)
            .Select(n => n?.Content as ActivityLog)
            .Should().Within(60.Seconds()).Match(l => l is not null
                && l.Status == ActivityStatus.Succeeded
                && l.Messages.Any(m => m.Message.Contains("out-line-capture"))
                && l.Messages.Any(m => m.Message.Contains("err-line-capture"))))!;

        log.Messages.First(m => m.Message.Contains("out-line-capture"))
            .LogLevel.Should().Be(LogLevel.Information,
                "stdout lines flow as Information messages");
        log.Messages.First(m => m.Message.Contains("err-line-capture"))
            .LogLevel.Should().Be(LogLevel.Error,
                "stderr lines flow as Error messages — Console.Error was previously not captured at all");
    }

    // ── (ii) LastActivityPath stamp + output-segment embed ─────────────────

    [Fact(Timeout = 120000)]
    public async Task Run_Stamps_LastActivityPath_And_OutputSegment_Embeds_That_Path()
    {
        var codePath = await SeedExecutableCode("""
            Console.WriteLine("stamp-check");
            "done"
            """);

        var client = GetClient();
        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(CodeLayoutAreas.ContentArea);
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new Address(codePath), reference);

        // Area alive before the run.
        var root = (StackControl)(await stream.GetControlStream(reference.Area!)
            .Should().Within(30.Seconds()).Match(c => c is StackControl))!;

        var activityPath = await RunScript(codePath);

        // The stamp lands on the Code node's content …
        await workspace.GetMeshNodeStream(codePath)
            .Select(n => n.ContentAs<CodeConfiguration>(client.JsonSerializerOptions))
            .Should().Within(30.Seconds()).Match(c => c != null
                && c.LastActivityPath == activityPath);

        // … and the Content area's output segment embeds the Progress area of
        // exactly THAT path (a dangling / missing stamp would leave the pane
        // pointing nowhere — one of the eternal-spinner shapes).
        var cellArea = root.Areas
            .Select(a => a.Area?.ToString())
            .First(a => a is not null
                && (a == CodeLayoutAreas.CellArea
                    || a.EndsWith("/" + CodeLayoutAreas.CellArea, StringComparison.Ordinal)))!;
        var cell = (StackControl)(await stream.GetControlStream(cellArea)
            .Should().Within(30.Seconds()).Match(c => c is StackControl))!;
        var outputArea = cell.Areas
            .Select(a => a.Area?.ToString())
            .First(a => a is not null
                && (a == CodeLayoutAreas.CellOutputArea
                    || a.EndsWith("/" + CodeLayoutAreas.CellOutputArea, StringComparison.Ordinal)))!;
        await stream.GetControlStream(outputArea)
            .Should().Within(30.Seconds()).Match(c => c is LayoutAreaControl l
                && l.Address.ToString() == activityPath
                && l.Reference.Area == ActivityLayoutAreas.ProgressArea);
    }

    // ── (iii) Progress renders OUTPUT (not a spinner) for other viewers ────

    private async Task AssertProgressShowsOutput(string activityPath, string marker)
    {
        var workspace = GetClient().GetWorkspace();
        var reference = new LayoutAreaReference(ActivityLayoutAreas.ProgressArea);
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new Address(activityPath), reference);

        // The Progress root is a Stack: [0] the status indicator, [1] the message
        // log — the control-based shape ActivityLayoutAreas.Progress renders since
        // the hand-rolled messages HTML was replaced by controls (BuildLog /
        // BuildProgressIndicator). The old assertion demanded an HtmlControl and
        // burned its full 30 s wait on every run — one of the hidden AI.Test
        // failures masked by the CI 6-minute kill.
        var rootControl = (StackControl)(await stream.GetControlStream(reference.Area!)
            .Should().Within(30.Seconds()).Match(c => c is StackControl s && s.Areas.Count >= 2))!;

        // Terminal indicator: a Succeeded activity renders a "✓ Done" status line,
        // never a spinner (pinned control-shape: ActivityProgressViewTest).
        var indicatorArea = rootControl.Areas[0].Area!.ToString()!;
        await stream.GetControlStream(indicatorArea)
            .Should().Within(30.Seconds()).Match(c => c is LabelControl l
                && l.Data is not null
                && l.Data.ToString()!.Contains("Done"));

        // The captured output line renders as one of the log rows' message labels
        // (BuildLog: one horizontal [level-tag, message] row per LogMessage).
        var logArea = rootControl.Areas[1].Area!.ToString()!;
        var logStack = (StackControl)(await stream.GetControlStream(logArea)
            .Should().Within(30.Seconds()).Match(c => c is StackControl s && s.Areas.Count >= 1))!;
        await logStack.Areas
            .Select(row => stream.GetControlStream(row.Area!.ToString()!))
            .Merge()
            .Where(c => c is StackControl)
            .SelectMany(row => ((StackControl)row!).Areas
                .Select(cell => stream.GetControlStream(cell.Area!.ToString()!))
                .Merge())
            .Should().Within(30.Seconds()).Match(c => c is LabelControl l
                && l.Data is not null
                && l.Data.ToString()!.Contains(marker));
    }

    [Fact(Timeout = 120000)]
    public async Task Progress_Of_Succeeded_Activity_Renders_Output_For_PublicRead_Viewer()
    {
        var codePath = await SeedExecutableCode("""
            Console.WriteLine("out-line-public");
            "done"
            """);
        var activityPath = await RunScript(codePath);

        // Runner (admin) waits until the activity is terminal WITH the line.
        await GetClient().GetWorkspace().GetMeshNodeStream(activityPath)
            .Select(n => n?.Content as ActivityLog)
            .Should().Within(60.Seconds()).Match(l => l is not null
                && l.Status == ActivityStatus.Succeeded
                && l.Messages.Any(m => m.Message.Contains("out-line-public")));

        // Baseline: the RUNNER can render the Progress area (bisects
        // viewer-independent render defects from access-shaped ones).
        await AssertProgressShowsOutput(activityPath, "out-line-public");

        // A DIFFERENT, role-less user (public-read only via the partition
        // policy) renders the Progress area: output lines, not a spinner.
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        accessService.SetCircuitContext(new AccessContext { ObjectId = PublicViewer, Name = PublicViewer });
        try
        {
            await AssertProgressShowsOutput(activityPath, "out-line-public");
        }
        finally
        {
            accessService.SetCircuitContext(null);
        }
    }

    [Fact(Timeout = 120000)]
    public async Task Progress_Of_Succeeded_Activity_Renders_Output_For_Anonymous_VUser()
    {
        var codePath = await SeedExecutableCode("""
            Console.WriteLine("out-line-anon");
            "done"
            """);
        var activityPath = await RunScript(codePath);

        await GetClient().GetWorkspace().GetMeshNodeStream(activityPath)
            .Select(n => n?.Content as ActivityLog)
            .Should().Within(60.Seconds()).Match(l => l is not null
                && l.Status == ActivityStatus.Succeeded
                && l.Messages.Any(m => m.Message.Contains("out-line-anon")));

        // The logged-out circuit shape (CircuitAccessHandler): the anonymous
        // VUser — IsVirtual, ObjectId = Anonymous. PublicRead must admit it on
        // the activity satellite exactly as it does on the Code node itself.
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        accessService.SetCircuitContext(new AccessContext
        {
            ObjectId = WellKnownUsers.Anonymous,
            Name = "Guest",
            IsVirtual = true
        });
        try
        {
            await AssertProgressShowsOutput(activityPath, "out-line-anon");
        }
        finally
        {
            accessService.SetCircuitContext(null);
        }
    }

    // ── (iv) the SatelliteAccessRule defect, pinned at the rule ────────────

    [Fact(Timeout = 60000)]
    public async Task SatelliteRule_Evaluates_Null_Identity_As_Anonymous_Not_Deny()
    {
        // The exact pre-fix defect: a NULL identity (no AccessContext at all —
        // the shape a contextless delivery produces) was hard-denied, making
        // the `_Activity` satellite stricter than its public-read MainNode.
        // The rule must evaluate the missing identity as Anonymous: PublicRead
        // on the MainNode grants Read; write-class operations stay denied.
        var rule = new SatelliteAccessRule(ActivityNodeType.NodeType, Mesh);
        var activityNode = new MeshNode($"act-{Guid.NewGuid():N}", $"{Partition}/_Activity")
        {
            NodeType = ActivityNodeType.NodeType,
            MainNode = Partition,
            Content = new ActivityLog("ScriptExecution")
        };

        await rule.HasAccess(new NodeValidationContext
            {
                Operation = NodeOperation.Read,
                Node = activityNode
            }, null)
            .Should().Within(30.Seconds()).Match(granted => granted,
                "a public-read partition's activity satellite must be readable without an identity");

        await rule.HasAccess(new NodeValidationContext
            {
                Operation = NodeOperation.Update,
                Node = activityNode
            }, null)
            .Should().Within(30.Seconds()).Match(granted => !granted,
                "PublicRead grants Read only — anonymous writes stay denied");
    }
}
