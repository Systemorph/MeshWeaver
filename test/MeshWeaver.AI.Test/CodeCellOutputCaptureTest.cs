#pragma warning disable CS1591

using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
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
        try
        {
            await stream.GetControlStream(indicatorArea)
                .Should().Within(30.Seconds()).Match(c => c is LabelControl l
                    && l.Data is not null
                    && l.Data.ToString()!.Contains("Done"));
        }
        catch (MeshWeaver.Reactive.Assertions.ObservableAssertionException ex)
        {
            // 🚨 CI-only flake diagnosis (runs 31401910253 / 31407254207-2 / 31414605989): the
            // indicator emitted exactly ONE control in 30s — the stale Running-state spinner —
            // although this test had ALREADY observed Status=Succeeded on the node stream before
            // subscribing. That single message cannot separate the two failure worlds, which
            // need opposite fixes:
            //   • node REGRESSED (a late write rolled Status back to Running) → write-order bug
            //     upstream of the render;
            //   • node still Succeeded but the render/sync pipeline LOST the re-render frame →
            //     layout-sync bug.
            // Re-read the authoritative node AT FAILURE TIME and put its state into the thrown
            // message, so the next CI sighting names its world instead of burning another triage.
            ActivityLog? logNow = null;
            try
            {
                logNow = (await workspace.GetMeshNodeStream(activityPath)
                        .Where(n => n is not null)
                        .Select(n => n!.Content as ActivityLog)
                        .Take(1)
                        .Timeout(TimeSpan.FromSeconds(5))
                        .FirstAsync().ToTask());
            }
            catch (Exception readEx)
            {
                throw new Xunit.Sdk.XunitException(
                    $"{ex.Message}\nFAILURE-TIME NODE STATE: unreadable ({readEx.GetType().Name}: "
                    + $"{readEx.Message}) — the owner did not answer, pointing at the owner hub, "
                    + "not the render pipeline.", ex);
            }
            throw new Xunit.Sdk.XunitException(
                $"{ex.Message}\nFAILURE-TIME NODE STATE: Status={logNow?.Status}, "
                + $"Messages={logNow?.Messages.Count}, End={logNow?.End:o}. "
                + (logNow?.Status == ActivityStatus.Succeeded
                    ? "Node is (still) Succeeded ⇒ the render/sync pipeline lost the terminal "
                      + "re-render frame (layout-sync bug)."
                    : "Node is NOT Succeeded although this test already observed Succeeded before "
                      + "subscribing ⇒ a later write regressed the node (write-order bug)."), ex);
        }

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
        accessService.SetHostIdentity(new AccessContext { ObjectId = PublicViewer, Name = PublicViewer });
        try
        {
            await AssertProgressShowsOutput(activityPath, "out-line-public");
        }
        finally
        {
            accessService.SetHostIdentity(null);
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
        accessService.SetHostIdentity(new AccessContext
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
            accessService.SetHostIdentity(null);
        }
    }

    // ── (iii-b) Progress renders the CONTROL a script returned ─────────────

    [Fact(Timeout = 120000)]
    public async Task Progress_Renders_The_Control_A_Script_Returned()
    {
        // #915's other half. A script whose result is a control logs NOTHING, so the output pane
        // showed "✓ Done — This run produced no output." over an invisible result: the cell
        // embeds the Progress area, and Progress rendered the log only. The control cannot be
        // recovered from ActivityLog.ReturnValue either — a container serializes as bare
        // NamedAreaControl references (children live in the non-serialized Views/Renderers), which
        // is why the pane has to render the LIVE control off the kernel's area dictionary.
        const string marker = "rendered-result-marker";
        var codePath = await SeedExecutableCode($$"""
            using MeshWeaver.Layout;
            Controls.Stack.WithView(Controls.Markdown("{{marker}}"))
            """);
        var activityPath = await RunScript(codePath);

        await GetClient().GetWorkspace().GetMeshNodeStream(activityPath)
            .Select(n => n?.Content as ActivityLog)
            .Should().Within(60.Seconds()).Match(l => l is not null
                && l.Status == ActivityStatus.Succeeded);

        var workspace = GetClient().GetWorkspace();
        var reference = new LayoutAreaReference(ActivityLayoutAreas.ProgressArea);
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new Address(activityPath), reference);

        // The result hangs off the Progress root under its OWN named area, after the log —
        // and WITHOUT a "✓ Done" status heading: a succeeded run whose result renders shows
        // the result alone (ActivityLayoutAreas.ShowsStatusLine — the cell toolbar carries the
        // idle state instead, 2026-08-12 UX feedback). Exactly two areas: the (empty) log and
        // the result.
        var root = (StackControl)(await stream.GetControlStream(reference.Area!)
            .Should().Within(60.Seconds()).Match(c => c is StackControl s
                && s.Areas.Count == 2
                && s.Areas.Any(a => IsArea(a, ActivityLayoutAreas.ResultArea))))!;
        var resultArea = root.Areas.First(a => IsArea(a, ActivityLayoutAreas.ResultArea)).Area!.ToString()!;

        // …and it is the LIVE control tree: the returned Stack's child renders as the markdown
        // the script wrote, not as an empty NamedAreaControl reference.
        var resultStack = (StackControl)(await stream.GetControlStream(resultArea)
            .Should().Within(30.Seconds()).Match(c => c is StackControl s && s.Areas.Count >= 1))!;
        await resultStack.Areas
            .Select(a => stream.GetControlStream(a.Area!.ToString()!))
            .Merge()
            .Should().Within(30.Seconds()).Match(c => c is MarkdownControl m
                && m.Markdown is not null
                && m.Markdown.ToString()!.Contains(marker));

        // The log says nothing: "This run produced no output." beside a rendered result would
        // contradict the very thing under it. With the status heading gone the log is the
        // root's FIRST area (the result is the second).
        var logArea = root.Areas[0].Area!.ToString()!;
        await stream.GetControlStream(logArea)
            .Should().Within(30.Seconds()).Match(c => c is StackControl s && s.Areas.Count == 0);
    }

    /// <summary>Area ids are rendered PREFIXED with their parent's path ("Progress/Result").</summary>
    private static bool IsArea(NamedAreaControl area, string name) =>
        area.Area?.ToString() is { } a
        && (a == name || a.EndsWith("/" + name, StringComparison.Ordinal));

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
