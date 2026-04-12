using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
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
using MeshWeaver.Messaging;
using Microsoft.DotNet.Interactive.Commands;
using Microsoft.DotNet.Interactive.Events;
using Xunit;
using MdExtensions = MeshWeaver.Markdown.MarkdownExtensions;

namespace MeshWeaver.Persistence.Test;

[Collection("KernelTests")]
public class MonolithKernelTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const int DefaultTimeoutMs = 30000;

    // AddKernel() is already included via AddGraph() in base class
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder);

    /// <summary>
    /// Returns a new kernel address. The mesh routing rule (RouteAddressToHostedHub)
    /// creates the kernel hub on demand when the first message arrives.
    /// </summary>
    private static Address CreateKernelSession()
        => AddressExtensions.CreateKernelAddress();

    [Fact(Timeout = DefaultTimeoutMs)]
    public async Task HelloWorld()
    {
        var client = GetClient();
        var kernelAddress = CreateKernelSession();

        var command = new SubmitCode("Console.WriteLine(\"Hello World\");");
        client.Post(
            new KernelCommandEnvelope(Microsoft.DotNet.Interactive.Connection.KernelCommandEnvelope.Serialize
                (Microsoft.DotNet.Interactive.Connection.KernelCommandEnvelope.Create(command)))
            {
                IFrameUrl = "http://localhost/area"
            },
            o => o.WithTarget(kernelAddress));
        var kernelEvent = await kernelEventsStream
            .Select(e => Microsoft.DotNet.Interactive.Connection.KernelEventEnvelope.Deserialize(e.Envelope).Event)
            .TakeUntil(e => e is CommandSucceeded || e is CommandFailed)
            .ToArray()
            .Timeout(15.Seconds())
            .FirstAsync(x => x is not null);

        var standardOutput = kernelEvent.OfType<StandardOutputValueProduced>().Single();
        var value = standardOutput.FormattedValues.Single();
        value.Value.TrimEnd('\n', '\r').Should().Be("Hello World");
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
        var kernelAddress = CreateKernelSession();

        client.Post(
            new SubmitCodeRequest(Code) { Id = Area },
            o => o.WithTarget(kernelAddress));

        var stream = client.GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(kernelAddress, new LayoutAreaReference(Area));
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
        var kernelAddress = CreateKernelSession();
        const string viewId = "test-view-1";

        client.Post(
            new SubmitCodeRequest("MeshWeaver.Layout.Controls.Markdown(\"Hello from kernel\")") { Id = viewId },
            o => o.WithTarget(kernelAddress));

        var stream = client.GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            kernelAddress, new LayoutAreaReference(viewId));
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
        var kernelAddress = CreateKernelSession();

        // First submission: define a variable
        client.Post(
            new SubmitCodeRequest("var myValue = 42;") { Id = "cell-1" },
            o => o.WithTarget(kernelAddress));

        // Second submission: use the variable and produce a result
        client.Post(
            new SubmitCodeRequest("MeshWeaver.Layout.Controls.Markdown($\"Value is {myValue}\")") { Id = "cell-2" },
            o => o.WithTarget(kernelAddress));

        var stream = client.GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            kernelAddress, new LayoutAreaReference("cell-2"));
        var control = await stream.GetControlStream("cell-2")
            .Timeout(15.Seconds())
            .FirstAsync(x => x is not null);

        control.Should().BeOfType<MarkdownControl>();
        (control as MarkdownControl)!.Markdown.ToString().Should().Contain("Value is 42");
    }

    /// <summary>
    /// Tests that each kernel session gets a unique address.
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
    public async Task ThreeSubmissions_ShareState()
    {
        var client = GetClient();
        var kernelAddress = CreateKernelSession();

        client.Post(new SubmitCodeRequest("var sharedValue = 100;") { Id = "s1" }, o => o.WithTarget(kernelAddress));
        client.Post(new SubmitCodeRequest("MeshWeaver.Layout.Controls.Markdown($\"first: {sharedValue}\")") { Id = "s2" }, o => o.WithTarget(kernelAddress));
        client.Post(new SubmitCodeRequest("MeshWeaver.Layout.Controls.Markdown($\"second: {sharedValue * 2}\")") { Id = "s3" }, o => o.WithTarget(kernelAddress));

        // Subscribe to each area independently — each needs its own LayoutAreaReference
        var stream2 = client.GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            kernelAddress, new LayoutAreaReference("s2"));
        var r2 = await stream2.GetControlStream("s2").Timeout(20.Seconds()).FirstAsync(x => x is not null);
        (r2 as MarkdownControl)!.Markdown.ToString().Should().Contain("first: 100");

        var stream3 = client.GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            kernelAddress, new LayoutAreaReference("s3"));
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

        // Submit all blocks in order (mimics what MarkdownView does)
        client.Post(new SubmitCodeRequest(actI) { Id = "act-1" }, o => o.WithTarget(kernelAddress));
        client.Post(new SubmitCodeRequest(actII) { Id = "act-2" }, o => o.WithTarget(kernelAddress));
        client.Post(new SubmitCodeRequest(actIII) { Id = "act-3" }, o => o.WithTarget(kernelAddress));
        client.Post(new SubmitCodeRequest(actIV) { Id = "act-4" }, o => o.WithTarget(kernelAddress));
        client.Post(new SubmitCodeRequest(actV) { Id = "act-5" }, o => o.WithTarget(kernelAddress));

        // Verify all blocks produced results using variables from Act I.
        // Each area needs its own LayoutAreaReference stream subscription.
        async Task<MarkdownControl> GetMarkdown(string areaId)
        {
            var s = client.GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
                kernelAddress, new LayoutAreaReference(areaId));
            var control = await s.GetControlStream(areaId).Timeout(20.Seconds()).FirstAsync(x => x is not null);
            return (MarkdownControl)control;
        }

        (await GetMarkdown("act-2")).Markdown.ToString().Should().Contain("Uptime:",
            "Act II must see `now` and `epoch` from Act I");
        (await GetMarkdown("act-3")).Markdown.ToString().Should().Contain("Primes below 200: 46",
            "Act III must see `primes` collection from Act I");
        (await GetMarkdown("act-4")).Markdown.ToString().Should().Contain("Collatz(27) has 111 steps",
            "Act IV must see `Collatz` local function from Act I");
        (await GetMarkdown("act-5")).Markdown.ToString().Should().Contain("'the' (4x)",
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
        var kernelAddress = CreateKernelSession();

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
            var stream = client.GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
                kernelAddress, new LayoutAreaReference(submission.Id));
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

    private readonly ReplaySubject<KernelEventEnvelope> kernelEventsStream = new();
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        return base.ConfigureClient(configuration).AddLayoutClient().WithHandler<KernelEventEnvelope>((_, e) =>
        {
            kernelEventsStream.OnNext(e.Message);
            return e.Processed();
        });
    }
}
