using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using Markdig.Syntax;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Kernel;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MdExtensions = MeshWeaver.Markdown.MarkdownExtensions;

namespace MeshWeaver.Persistence.Test;

[Collection("KernelTests")]
public class MonolithKernelTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>Share Mesh/SP across [Fact]s — see MonolithMeshTestBase.ShareMeshAcrossTests.</summary>
    protected override bool ShareMeshAcrossTests => true;

    // 60s per test — generous budget for cold CI runs (kernel grain activation
    // + Roslyn compile + ALC load can total 15-20s alone, and the inner
    // WatchForActivityLogAsync timeout is 25s). Locally tests come in ~5-15s
    // each, so this won't slow the green path; it just stops the cold-CI
    // tests from timing out before the kernel has finished activating.
    private const int DefaultTimeoutMs = 60000;

    // AddKernel() is already included via AddGraph() in base class
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder);

    /// <summary>
    /// Materialises a per-test Activity MeshNode whose hub hosts the kernel
    /// handlers (via <c>ActivityNodeType.HubConfiguration</c> + <c>AddKernelSubHubHandlers</c>),
    /// and returns its address. Replaces the legacy `kernel/*` standalone hub
    /// addressing — every kernel session is now an Activity-hosted sub-hub.
    /// </summary>
    private Address CreateKernelSession()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var kernelId = Guid.NewGuid().ToString("N");
        const string ownerPath = "rbuergi";
        var activityNamespace = $"{ownerPath}/_Activity";
        var activityNode = new MeshNode($"markdown-{kernelId}", activityNamespace)
        {
            Name = "Test kernel session",
            NodeType = "Activity",
            MainNode = ownerPath,
            State = MeshNodeState.Active,
            Content = new ActivityLog("KernelExecution") { Status = ActivityStatus.Running }
        };
        meshService.CreateNode(activityNode).Should().Emit();
        return new Address($"{activityNamespace}/markdown-{kernelId}");
    }

    /// <summary>
    /// Returns a Task that completes when the activity log at <paramref name="activityAddress"/>
    /// emits a snapshot matching <paramref name="predicate"/>. Subscribes IMMEDIATELY
    /// at call time so the caller can post the trigger AFTER awaiting the returned
    /// Task on the next line — without that ordering, the script's activity log
    /// update can fire BEFORE the test subscribes (handler runs faster than the
    /// test thread reaches the subscribe call), the hot stream drops the
    /// emission, and the test times out. See
    /// <c>Doc/Architecture/WritingTests.md</c> → "Stream assertions".
    /// </summary>
    // Returns the (cold, filtered) activity-log stream. The workspace
    // GetRemoteStream replays its current snapshot to late subscribers, so the
    // reactive .Should().Match(...) at the call site may subscribe AFTER the
    // trigger post and still observe the matching emission. The reactive
    // assertion supplies its own blocking wait + timeout (25s budget — kernel
    // session activation on cold CI Linux runners can take ~10-15s alone for
    // Roslyn compile + ALC load).
    private IObservable<ActivityLog> WatchForActivityLog(
        IMessageHub client, Address activityAddress, Func<ActivityLog, bool> predicate)
        => client.GetWorkspace()
            .GetMeshNodeStream(activityAddress.Path)
            .Select(change => change?.Content as ActivityLog)
            .Where(log => log is not null && predicate(log!))
            .Select(log => log!);

    [Fact(Timeout = DefaultTimeoutMs)]
    public void HelloWorld()
    {
        var client = GetClient();
        var kernelAddress = CreateKernelSession();

        var logStream = WatchForActivityLog(client, kernelAddress,
            l => l.Messages.Any(m => m.Message.Contains("Hello World")));

        client.Post(
            new SubmitCodeRequest("Console.WriteLine(\"Hello World\");"),
            o => o.WithTarget(kernelAddress));

        var log = logStream.Should().Within(25.Seconds()).Emit();

        log.Messages.Select(m => m.Message)
            .Should().Contain(m => m.Contains("Hello World"));
    }

    [Fact(Timeout = DefaultTimeoutMs)]
    public void CalculatorDirectlyThroughKernel()
    {
        const string Code = @"using MeshWeaver.Layout;
using static MeshWeaver.Layout.Controls;
using static MeshWeaver.Layout.EditorExtensions;
record Calculator(double Summand1, double Summand2);
static UiControl CalculatorSum(Calculator c) => Markdown($""**Sum**: {c.Summand1 + c.Summand2}"");
Mesh.Edit(new Calculator(1,2), CalculatorSum)
";
        const string Area = nameof(Area);
        var client = GetClient();
        var kernelAddress = CreateKernelSession();

        var stream = client.GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(kernelAddress, new LayoutAreaReference(Area));

        client.Post(
            new SubmitCodeRequest(Code) { Id = Area },
            o => o.WithTarget(kernelAddress));

        var control = stream.GetControlStream(Area)
            .Should().Within(20.Seconds()).Match(x => x is not null);

        var stack = control.Should().BeOfType<StackControl>().Which;
        control = stream.GetControlStream(stack.Areas.First().Area.ToString()!)
            .Should().Within(10.Seconds()).Match(x => x is not null);
        var editor = control.Should().BeOfType<EditorControl>().Which;
        editor.DataContext.Should().NotBeNull();
        var data = stream.GetDataStream<object?>(new(editor.DataContext!))
            .Should().Within(10.Seconds()).Match(x => x is not null);
        stream.UpdatePointer(3, editor.DataContext, new("summand1"));
        var md = stream.GetControlStream(stack.Areas.Last().Area.ToString()!)
            .Should().Within(5.Seconds()).Match(x => !(x as MarkdownControl)?.Markdown?.ToString()?.Contains("3") == true);

        md.Should().BeOfType<MarkdownControl>().Which.Markdown.ToString().Should().Contain("5");
    }

    /// <summary>
    /// Tests that SubmitCodeRequest produces a layout area result
    /// (the same path that Blazor interactive markdown views use).
    /// </summary>
    [Fact(Timeout = DefaultTimeoutMs)]
    public void SubmitCodeRequest_ProducesLayoutAreaResult()
    {
        var client = GetClient();
        var kernelAddress = CreateKernelSession();
        const string viewId = "test-view-1";

        var stream = client.GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            kernelAddress, new LayoutAreaReference(viewId));

        client.Post(
            new SubmitCodeRequest("MeshWeaver.Layout.Controls.Markdown(\"Hello from kernel\")") { Id = viewId },
            o => o.WithTarget(kernelAddress));

        var control = stream.GetControlStream(viewId)
            .Should().Within(15.Seconds()).Match(x => x is not null);

        control.Should().BeOfType<MarkdownControl>();
        (control as MarkdownControl)!.Markdown.ToString().Should().Contain("Hello from kernel");
    }

    /// <summary>
    /// Tests that multiple SubmitCodeRequests to the same kernel
    /// share state (like a notebook — variables persist between cells).
    /// </summary>
    [Fact(Timeout = DefaultTimeoutMs)]
    public void MultipleSubmissions_ShareKernelState()
    {
        var client = GetClient();
        var kernelAddress = CreateKernelSession();

        var stream = client.GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            kernelAddress, new LayoutAreaReference("cell-2"));

        // First submission: define a variable
        client.Post(
            new SubmitCodeRequest("var myValue = 42;") { Id = "cell-1" },
            o => o.WithTarget(kernelAddress));

        // Second submission: use the variable and produce a result
        client.Post(
            new SubmitCodeRequest("MeshWeaver.Layout.Controls.Markdown($\"Value is {myValue}\")") { Id = "cell-2" },
            o => o.WithTarget(kernelAddress));

        var control = stream.GetControlStream("cell-2")
            .Should().Within(15.Seconds()).Match(x => x is not null);

        control.Should().BeOfType<MarkdownControl>();
        (control as MarkdownControl)!.Markdown.ToString().Should().Contain("Value is 42");
    }

    /// <summary>
    /// Tests that each kernel session gets a unique address. After the
    /// kernel-as-Activity-sub-hub migration, "kernel session address" is the
    /// per-Activity MeshNode path; uniqueness comes from the kernel-id GUID.
    /// </summary>
    [Fact]
    public void MultipleKernelSessions_HaveUniqueAddresses()
    {
        var address1 = CreateKernelSession();
        var address2 = CreateKernelSession();

        address1.Should().NotBe(address2, "Each kernel session should have a unique address");
    }

    /// <summary>
    /// Minimal repro for the "variables not defined" bug.
    /// Submits block 1 (defines x), block 2 (uses x), block 3 (uses x again).
    /// </summary>
    [Fact(Timeout = DefaultTimeoutMs)]
    public void ThreeSubmissions_ShareState()
    {
        var client = GetClient();
        var kernelAddress = CreateKernelSession();

        var stream2 = client.GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            kernelAddress, new LayoutAreaReference("s2"));
        var stream3 = client.GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            kernelAddress, new LayoutAreaReference("s3"));

        client.Post(new SubmitCodeRequest("var sharedValue = 100;") { Id = "s1" }, o => o.WithTarget(kernelAddress));
        client.Post(new SubmitCodeRequest("MeshWeaver.Layout.Controls.Markdown($\"first: {sharedValue}\")") { Id = "s2" }, o => o.WithTarget(kernelAddress));
        client.Post(new SubmitCodeRequest("MeshWeaver.Layout.Controls.Markdown($\"second: {sharedValue * 2}\")") { Id = "s3" }, o => o.WithTarget(kernelAddress));

        var r2 = stream2.GetControlStream("s2").Should().Within(20.Seconds()).Match(x => x is not null);
        (r2 as MarkdownControl)!.Markdown.ToString().Should().Contain("first: 100");

        var r3 = stream3.GetControlStream("s3").Should().Within(20.Seconds()).Match(x => x is not null);
        (r3 as MarkdownControl)!.Markdown.ToString().Should().Contain("second: 200");
    }

    /// <summary>
    /// Replicates the interactive-showcase scenario: a silent "setup" block defines
    /// variables and local functions, then subsequent blocks reference them.
    /// If variables don't persist, subsequent blocks fail with CS0103 "does not exist".
    /// </summary>
    [Fact(Timeout = DefaultTimeoutMs)]
    public void InteractiveShowcase_VariablesPersistAcrossAllBlocks()
    {
        var client = GetClient();
        var kernelAddress = CreateKernelSession();

        // Act I — silent setup: variables, collections, and local functions
        const string actI = @"
var epoch = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
var now   = DateTime.UtcNow;
var uptime = now - epoch;

bool[] sieve = new bool[200];
Array.Fill(sieve, true);
sieve[0] = sieve[1] = false;
for (int i = 2; i < sieve.Length; i++)
    if (sieve[i])
        for (int j = i * 2; j < sieve.Length; j += i)
            sieve[j] = false;
var primes = Enumerable.Range(2, 198).Where(i => sieve[i]).ToList();

IEnumerable<long> Collatz(long n) {
    yield return n;
    while (n != 1) {
        n = n % 2 == 0 ? n / 2 : 3 * n + 1;
        yield return n;
    }
}

string corpus = ""the mesh is alive the mesh thinks the mesh connects everything and the mesh grows stronger every day"";
var wordFreq = corpus.Split(' ')
    .GroupBy(w => w)
    .OrderByDescending(g => g.Count())
    .ToDictionary(g => g.Key, g => g.Count());
";

        // Act II — uses `now`, `epoch` from Act I
        const string actII = @"MeshWeaver.Layout.Controls.Markdown($""Uptime: {(now - epoch).TotalDays:F0} days"")";

        // Act III — uses `primes` collection from Act I
        const string actIII = @"MeshWeaver.Layout.Controls.Markdown($""Primes below 200: {primes.Count}, largest: {primes.Last()}"")";

        // Act IV — uses `Collatz` local function from Act I
        const string actIV = @"MeshWeaver.Layout.Controls.Markdown($""Collatz(27) has {Collatz(27).Count() - 1} steps"")";

        // Act V — uses `wordFreq` dictionary from Act I
        const string actV = @"MeshWeaver.Layout.Controls.Markdown($""Most frequent word: '{wordFreq.First().Key}' ({wordFreq.First().Value}x)"")";

        var stream2 = client.GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            kernelAddress, new LayoutAreaReference("act-2"));
        var stream3 = client.GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            kernelAddress, new LayoutAreaReference("act-3"));
        var stream4 = client.GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            kernelAddress, new LayoutAreaReference("act-4"));
        var stream5 = client.GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            kernelAddress, new LayoutAreaReference("act-5"));

        // Submit all blocks in order (mimics what MarkdownView does)
        client.Post(new SubmitCodeRequest(actI) { Id = "act-1" }, o => o.WithTarget(kernelAddress));
        client.Post(new SubmitCodeRequest(actII) { Id = "act-2" }, o => o.WithTarget(kernelAddress));
        client.Post(new SubmitCodeRequest(actIII) { Id = "act-3" }, o => o.WithTarget(kernelAddress));
        client.Post(new SubmitCodeRequest(actIV) { Id = "act-4" }, o => o.WithTarget(kernelAddress));
        client.Post(new SubmitCodeRequest(actV) { Id = "act-5" }, o => o.WithTarget(kernelAddress));

        MarkdownControl GetMarkdown(ISynchronizationStream<JsonElement> stream, string areaId)
        {
            var control = stream.GetControlStream(areaId).Should().Within(20.Seconds()).Match(x => x is not null);
            return (MarkdownControl)control!;
        }

        GetMarkdown(stream2, "act-2").Markdown.ToString().Should().Contain("Uptime:",
            "Act II must see `now` and `epoch` from Act I");
        GetMarkdown(stream3, "act-3").Markdown.ToString().Should().Contain("Primes below 200: 46",
            "Act III must see `primes` collection from Act I");
        GetMarkdown(stream4, "act-4").Markdown.ToString().Should().Contain("Collatz(27) has 111 steps",
            "Act IV must see `Collatz` local function from Act I");
        GetMarkdown(stream5, "act-5").Markdown.ToString().Should().Contain("'the' (4x)",
            "Act V must see `wordFreq` dictionary from Act I");
    }

    /// <summary>
    /// End-to-end test that mirrors the real Blazor flow:
    /// 1) Parse the InteractiveShowcase.md markdown file
    /// 2) Extract SubmitCodeRequest objects via ExecutableCodeBlock.Initialize()
    /// 3) Post all submissions to the SAME kernel address (what MarkdownView does)
    /// 4) Verify each block produces output (Markdown with expected text)
    /// This exercises the exact pipeline used by the UI.
    /// </summary>
    [Fact(Timeout = DefaultTimeoutMs)]
    public void InteractiveShowcaseMd_FullPipeline_AllBlocksExecute()
    {
        var markdownPath = Path.Combine(
            Path.GetDirectoryName(GetType().Assembly.Location)!,
            "Markdown", "InteractiveShowcase.md");
        File.Exists(markdownPath).Should().BeTrue(
            $"Test fixture should exist at {markdownPath}");
        var markdownBody = File.ReadAllText(markdownPath);
        // Strip YAML front matter (between --- delimiters) as the real parser does
        if (markdownBody.StartsWith("---"))
        {
            var endIdx = markdownBody.IndexOf("---", 3);
            if (endIdx > 0)
                markdownBody = markdownBody[(endIdx + 3)..].TrimStart('\r', '\n');
        }

        // Parse markdown like MarkdownView / CollaborativeMarkdownView does
        var pipeline = MdExtensions.CreateMarkdownPipeline(null, null);
        var document = Markdig.Markdown.Parse(markdownBody, pipeline);
        var executableBlocks = document.Descendants<ExecutableCodeBlock>().ToList();
        foreach (var block in executableBlocks)
            block.Initialize();
        var submissions = executableBlocks
            .Select(b => b.SubmitCode)
            .Where(s => s != null)
            .Cast<SubmitCodeRequest>()
            .ToList();

        submissions.Should().NotBeEmpty("InteractiveShowcase.md must contain executable code blocks");
        Output.WriteLine($"Parsed {submissions.Count} submissions from markdown");

        var client = GetClient();
        var kernelAddress = CreateKernelSession();

        // Materialise the streams in a dictionary so the assertion loop can
        // pull them out by submission id. The workspace GetRemoteStream replays
        // its current snapshot to late subscribers, so the reactive assertion
        // below still observes results posted before it subscribes.
        var streams = submissions.ToDictionary(
            s => s.Id,
            s => client.GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
                kernelAddress, new LayoutAreaReference(s.Id)));

        // Post all submissions to the same kernel — exactly what MarkdownView does
        foreach (var submission in submissions)
        {
            Output.WriteLine($"Posting submission {submission.Id}: {submission.Code.Split('\n').FirstOrDefault()?.Trim()}");
            client.Post(submission, o => o.WithTarget(kernelAddress));
        }

        // Each block in InteractiveShowcase.md returns a Controls.Markdown(...), so we expect
        // a MarkdownControl result for each submission.
        for (var i = 0; i < submissions.Count; i++)
        {
            var submission = submissions[i];
            var stream = streams[submission.Id];
            try
            {
                var control = stream.GetControlStream(submission.Id)
                    .Should().Within(15.Seconds()).Match(x => x is not null);

                var asMarkdown = (control as MarkdownControl);
                asMarkdown.Should().NotBeNull($"Block #{i} ({submission.Id}) should return MarkdownControl");
                var rendered = asMarkdown!.Markdown.ToString() ?? "";
                Output.WriteLine($"Block #{i} output: {rendered}");
                rendered.Should().NotContain("Execution failed",
                    $"Block #{i} failed. Code:\n{submission.Code}");
            }
            catch (ObservableAssertionException)
            {
                Assert.Fail($"Block #{i} ({submission.Id}) timed out. Code:\n{submission.Code}");
            }
        }
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();
}
