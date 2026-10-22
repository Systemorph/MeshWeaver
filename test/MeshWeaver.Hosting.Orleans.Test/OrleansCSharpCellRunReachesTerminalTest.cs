using System;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// A C# Code cell run on ORLEANS — the hosting shape of every deployed portal and of the Education
/// journey suite — reaches a terminal status on its Activity.
/// <para>The Monolith suites (CSharpCellRunKeepsTheKernelsVerdictTest and friends) pin this
/// in-process; nothing ran a C# cell through grains before. The Education journey (run
/// 39663348262) showed every embedded cell still non-terminal 3 minutes after ▶ Run on the Orleans
/// portal while the same cell finishes in about a second in-process.</para>
/// <para>Shape copied from the suite: the cell lives in the user's own partition and routes its
/// run to <c>{viewer}</c>, i.e. <c>{user}/_Activity/{id}</c>.</para>
/// </summary>
public class OrleansCSharpCellRunReachesTerminalTest(ITestOutputHelper output) : OrleansMeshTestBase(output)
{
    private IMessageHub GetClient([CallerMemberName] string? name = null)
        => base.GetClient($"cell-run-{name}-{Guid.NewGuid():N}", "TestUser");

    [Fact(Timeout = 120_000)]
    public Task ACSharpCellRun_OnOrleans_ReachesSucceeded()
        => RunsToSucceeded("Log.LogInformation(\"grading\"); 41 + 1", TestContext.Current.CancellationToken);

    /// <summary>The Education solution cell itself (AdvancedBusinessRules SpotThePlugSolved): records,
    /// LINQ, and a layout control returned as the result.</summary>
    [Fact(Timeout = 120_000)]
    public Task TheEducationSolutionCell_OnOrleans_ReachesSucceeded()
        => RunsToSucceeded(SpotThePlugSolved, TestContext.Current.CancellationToken);

    private const string SpotThePlugSolved = """"
using MeshWeaver.Layout;
using MeshWeaver.Layout.DataGrid;

// THE ANSWER — report B ties because 'Balancing item' absorbs whatever is left over.
string plugReport = "B";
string plugLine   = "Balancing item";

record Report(string Name, double Opening, (string Cause, double Amount)[] Steps, double Closing);

var reports = new[]
{
    new Report("A", 1000, new[]
    {
        ("Claims paid",         -140.0),
        ("Interest accretion",    20.0),
        ("Assumption change",     35.0),
        ("Experience variance",   -3.0),
    }, 912),

    new Report("B", 500, new[]
    {
        ("Claims paid",          -80.0),
        ("Interest accretion",    10.0),
        ("Balancing item",        26.0),
    }, 456),

    new Report("C", 2000, new[]
    {
        ("Premiums received",    240.0),
        ("Claims paid",         -310.0),
        ("Interest accretion",    41.0),
        ("FX",                   -12.0),
        ("Rounding",              -0.2),
    }, 1958.8),

    new Report("D", 750, new[]
    {
        ("Claims paid",         -100.0),
        ("Yield-curve update",   -18.0),
        ("Experience variance",    9.0),
    }, 641),
};

record Row(string Report, string Reconciles, string Smallest, string YourVerdict);

var rows = reports.Select(r =>
{
    var ties = Math.Abs(r.Opening + r.Steps.Sum(s => s.Amount) - r.Closing) < 0.05;
    var smallest = r.Steps.OrderBy(s => Math.Abs(s.Amount)).First();
    return new Row(r.Name, ties ? "yes" : "no",
        $"{smallest.Cause} ({smallest.Amount:N1})",
        r.Name == plugReport ? $"plugged by '{plugLine}'" : "");
}).ToList();

// The two answers, graded.
var checks = new[]
{
    ("The report", plugReport == "B"),
    ("The line",   plugLine.Contains("Balancing", StringComparison.OrdinalIgnoreCase)),
};
var green = checks.Count(c => c.Item2);
var total = checks.Length;

Controls.Stack
    .WithView(Controls.H4($"Answer battery — {green}/{total} green"))
    .WithView(Controls.DataGrid(rows)
        .WithColumn(new PropertyColumnControl<string> { Property = "report"      }.WithTitle("Report"))
        .WithColumn(new PropertyColumnControl<string> { Property = "reconciles"  }.WithTitle("Reconciles"))
        .WithColumn(new PropertyColumnControl<string> { Property = "smallest"    }.WithTitle("Smallest line"))
        .WithColumn(new PropertyColumnControl<string> { Property = "yourVerdict" }.WithTitle("Your verdict"))
        .Resizable(false))
    .WithView(Controls.Markdown(string.Join("\n\n", checks.Select(c =>
        $"{(c.Item2 ? "✓" : "✗")} {c.Item1}"))))
    .WithView(Controls.Markdown(
        "Every report reconciles, so the *Reconciles* column tells you nothing — that is the whole " +
        "lesson. Ask instead of each line: *could I have computed this from something that " +
        "happened, without knowing the closing balance?* The line that fails that test is the plug. " +
        "The smallest line is not the answer either: two of these are small because the effect they " +
        "name is genuinely small."))
"""";

    private async Task RunsToSucceeded(string code, System.Threading.CancellationToken ct, [CallerMemberName] string? name = null)
    {
        var client = GetClient(name);
        var id = $"cell-{Guid.NewGuid():N}";
        var created = await client.Observe(
                new CreateNodeRequest(new MeshNode(id, "TestUser")
                {
                    Name = "Orleans cell",
                    NodeType = "Code",
                    Content = new CodeConfiguration
                    {
                        Code = code,
                        Language = "csharp",
                        IsExecutable = true,
                        ActivityParentPath = "{viewer}",
                    }
                }),
                o => o.WithTarget(new Address("TestUser")))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        created.Message.Success.Should().BeTrue(created.Message.Error ?? "");
        var codePath = created.Message.Node!.Path!;

        var ack = (await client.Observe(new ExecuteScriptRequest(), o => o.WithTarget(new Address(codePath)))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct)).Message;
        ack.Success.Should().BeTrue(ack.Error ?? "dispatch refused");
        var activityPath = ack.ActivityLog!;
        Output.WriteLine($"Run → {activityPath}");
        activityPath.Should().StartWith("TestUser/_Activity/");

        var log = await client.GetWorkspace().GetMeshNodeStream(activityPath)
            .Select(n => n.ContentAs<ActivityLog>(client.JsonSerializerOptions))
            .Do(l => Output.WriteLine($"[activity] {l?.Status} messages={l?.Messages.Count}"))
            .Where(l => l is not null && l.Status.IsTerminal())
            .FirstAsync().Timeout(TimeSpan.FromSeconds(60)).Await(ct);
        log!.Status.Should().Be(ActivityStatus.Succeeded,
            string.Join(" | ", log.Messages.ConvertAll(m => m.Message)));
    }
}
