using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
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

    private const int DefaultTimeoutMs = 30000;

    // AddKernel() is already included via AddGraph() in base class
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder);

    /// <summary>
    /// Materialises a per-test Activity MeshNode whose hub hosts the kernel
    /// handlers (via <c>ActivityNodeType.HubConfiguration</c> + <c>AddKernelSubHubHandlers</c>),
    /// and returns its address. Replaces the legacy `kernel/*` standalone hub
    /// addressing — every kernel session is now an Activity-hosted sub-hub.
    /// </summary>
    private async Task<Address> CreateKernelSessionAsync()
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
        await meshService.CreateNode(activityNode).FirstAsync().ToTask();
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
    private Task<ActivityLog> WatchForActivityLogAsync(
        IMessageHub client, Address activityAddress, Func<ActivityLog, bool> predicate, TimeSpan? timeout = null)
        => client.GetWorkspace()
            .GetRemoteStream<MeshNode, MeshNodeReference>(activityAddress, new MeshNodeReference())
            .Select(change => change.Value?.Content as ActivityLog)
            .Where(log => log is not null && predicate(log!))
            .Select(log => log!)
            .Take(1)
            .Timeout(timeout ?? 15.Seconds())
            .FirstAsync()
            .ToTask(TestContext.Current.CancellationToken);

    [Fact(Timeout = DefaultTimeoutMs)]
    public async Task HelloWorld()
    {
        var client = GetClient();
        var kernelAddress = await CreateKernelSessionAsync();

        // Subscribe BEFORE posting — see WatchForActivityLogAsync's docstring.
        var logTask = WatchForActivityLogAsync(client, kernelAddress,
            l => l.Messages.Any(m => m.Message.Contains("Hello World")));

        client.Post(
            new SubmitCodeRequest("Console.WriteLine(\"Hello World\");"),
            o => o.WithTarget(kernelAddress));

        var log = await logTask;

        log.Messages.Select(m => m.Message)
            .Should().Contain(m => m.Contains("Hello World"));
    }

    [Fact(Timeout = 10000)]
    public async Task CalculatorDirectlyThroughKernel()
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
        var kernelAddress = await CreateKernelSessionAsync();

        // Subscribe BEFORE posting — see WatchForActivityLogAsync's docstring.
        var stream = client.GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(kernelAddress, new LayoutAreaReference(Area));

        client.Post(
            new SubmitCodeRequest(Code) { Id = Area },
            o => o.WithTarget(kernelAddress));

        var control = await stream.GetControlStream(Area)
            .Timeout(20.Seconds())
            .FirstAsync(x => x is not null);

        var stack = control.Should().BeOfType<StackControl>().Which;
        control = await stream.GetControlStream(stack.Areas.First().Area.ToString()!)
            .Timeout(10.Seconds())
            .FirstAsync(x => x is not null);
        var editor = control.Should().BeOfType<EditorControl>().Which;
        editor.DataContext.Should().NotBeNull();
        var data = await stream.GetDataStream<object?>(new(editor.DataContext))
            .Timeout(10.Seconds())
            .FirstAsync(x => x is not null);
        stream.UpdatePointer(3, editor.DataContext, new("summand1"));
        var md = await stream.GetControlStream(stack.Areas.Last().Area.ToString()!)
            .Timeout(5.Seconds())
            .FirstAsync(x => !(x as MarkdownControl)?.Markdown?.ToString()?.Contains("3") == true);

        md.Should().BeOfType<MarkdownControl>().Which.Markdown.ToString().Should().Contain("5");
    }

    /// <summary>
    /// Tests that SubmitCodeRequest produces a layout area result
    /// (the same path that Blazor interactive markdown views use).
    /// </summary>
    [Fact(Timeout = DefaultTimeoutMs)]
    public async Task SubmitCodeRequest_ProducesLayoutAreaResult()
    {
        var client = GetClient();
        var kernelAddress = await CreateKernelSessionAsync();
        const string viewId = "test-view-1";

        // Subscribe BEFORE posting — see WatchForActivityLogAsync's docstring.
        var stream = client.GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            kernelAddress, new LayoutAreaReference(viewId));

        client.Post(
            new SubmitCodeRequest("MeshWeaver.Layout.Controls.Markdown(\"Hello from kernel\")") { Id = viewId },
            o => o.WithTarget(kernelAddress));

        var control = await stream.GetControlStream(viewId)
            .Timeout(15.Seconds())
            .FirstAsync(x => x is not null);

        control.Should().BeOfType<MarkdownControl>();
        (control as MarkdownControl)!.Markdown.ToString().Should().Contain("Hello from kernel");
    }

    /// <summary>
    /// Tests that multiple SubmitCodeRequests to the same kernel
    /// share state (like a notebook — variables persist between cells).
    /// </summary>
    [Fact(Timeout = DefaultTimeoutMs)]
    public async Task MultipleSubmissions_ShareKernelState()
    {
        var client = GetClient();
        var kernelAddress = await CreateKernelSessionAsync();

        // Subscribe BEFORE posting — see WatchForActivityLogAsync's docstring.
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

        var control = await stream.GetControlStream("cell-2")
            .Timeout(15.Seconds())
            .FirstAsync(x => x is not null);

        control.Should().BeOfType<MarkdownControl>();
        (control as MarkdownControl)!.Markdown.ToString().Should().Contain("Value is 42");
    }

    /// <summary>
    /// Tests that each kernel session gets a unique address. After the
    /// kernel-as-Activity-sub-hub migration, "kernel session address" is the
    /// per-Activity MeshNode path; uniqueness comes from the kernel-id GUID.
    /// </summary>
    [Fact]
    public async Task MultipleKernelSessions_HaveUniqueAddresses()
    {
        var address1 = await CreateKernelSessionAsync();
        var address2 = await CreateKernelSessionAsync();

        address1.Should().NotBe(address2, "Each kernel session should have a unique address");
    }

    /// <summary>
    /// Minimal repro for the "variables not defined" bug.
    /// Submits block 1 (defines x), block 2 (uses x), block 3 (uses x again).
    /// </summary>
    [Fact(Timeout = DefaultTimeoutMs)]
    public async Task ThreeSubmissions_ShareState()
    {
        var client = GetClient();
        var kernelAddress = await CreateKernelSessionAsync();

        // Subscribe to each area BEFORE posting — see WatchForActivityLogAsync's docstring.
        var stream2 = client.GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            kernelAddress, new LayoutAreaReference("s2"));
        var stream3 = client.GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            kernelAddress, new LayoutAreaReference("s3"));

        client.Post(new SubmitCodeRequest("var sharedValue = 100;") { Id = "s1" }, o => o.WithTarget(kernelAddress));
        client.Post(new SubmitCodeRequest("MeshWeaver.Layout.Controls.Markdown($\"first: {sharedValue}\")") { Id = "s2" }, o => o.WithTarget(kernelAddress));
        client.Post(new SubmitCodeRequest("MeshWeaver.Layout.Controls.Markdown($\"second: {sharedValue * 2}\")") { Id = "s3" }, o => o.WithTarget(kernelAddress));

        var r2 = await stream2.GetControlStream("s2").Timeout(20.Seconds()).FirstAsync(x => x is not null);
        (r2 as MarkdownControl)!.Markdown.ToString().Should().Contain("first: 100");

        var r3 = await stream3.GetControlStream("s3").Timeout(20.Seconds()).FirstAsync(x => x is not null);
        (r3 as MarkdownControl)!.Markdown.ToString().Should().Contain("second: 200");
    }

    /// <summary>
    /// Replicates the interactive-showcase scenario: a silent "setup" block defines
    /// variables and local functions, then subsequent blocks reference them.
    /// If variables don't persist, subsequent blocks fail with CS0103 "does not exist".
    /// </summary>
    [Fact(Timeout = DefaultTimeoutMs)]
    public async Task InteractiveShowcase_VariablesPersistAcrossAllBlocks()
    {
        var client = GetClient();
        var kernelAddress = await CreateKernelSessionAsync();

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

        // Subscribe to each area BEFORE posting — see WatchForActivityLogAsync's docstring.
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

        async Task<MarkdownControl> GetMarkdown(ISynchronizationStream<JsonElement> stream, string areaId)
        {
            var control = await stream.GetControlStream(areaId).Timeout(20.Seconds()).FirstAsync(x => x is not null);
            return (MarkdownControl)control;
        }

        (await GetMarkdown(stream2, "act-2")).Markdown.ToString().Should().Contain("Uptime:",
            "Act II must see `now` and `epoch` from Act I");
        (await GetMarkdown(stream3, "act-3")).Markdown.ToString().Should().Contain("Primes below 200: 46",
            "Act III must see `primes` collection from Act I");
        (await GetMarkdown(stream4, "act-4")).Markdown.ToString().Should().Contain("Collatz(27) has 111 steps",
            "Act IV must see `Collatz` local function from Act I");
        (await GetMarkdown(stream5, "act-5")).Markdown.ToString().Should().Contain("'the' (4x)",
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
    public async Task InteractiveShowcaseMd_FullPipeline_AllBlocksExecute()
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
        var kernelAddress = await CreateKernelSessionAsync();

        // Subscribe to ALL areas BEFORE posting any submission — see
        // WatchForActivityLogAsync's docstring. Materialise the streams in a
        // dictionary so the assertion loop can pull them out by submission id.
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
                var control = await stream.GetControlStream(submission.Id)
                    .Timeout(15.Seconds())
                    .FirstAsync(x => x is not null);

                var asMarkdown = (control as MarkdownControl);
                asMarkdown.Should().NotBeNull($"Block #{i} ({submission.Id}) should return MarkdownControl");
                var rendered = asMarkdown!.Markdown.ToString() ?? "";
                Output.WriteLine($"Block #{i} output: {rendered}");
                rendered.Should().NotContain("Execution failed",
                    $"Block #{i} failed. Code:\n{submission.Code}");
            }
            catch (TimeoutException)
            {
                Assert.Fail($"Block #{i} ({submission.Id}) timed out. Code:\n{submission.Code}");
            }
        }
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();
}
