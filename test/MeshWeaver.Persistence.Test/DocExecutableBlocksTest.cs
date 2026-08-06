using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Documentation;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Kernel;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Persistence.Test;

/// <summary>
/// EXECUTES every executable code block (<c>--render</c> / <c>--execute</c>) of every embedded
/// documentation page through a REAL kernel session — the same path the Blazor interactive-markdown
/// view takes when a reader opens the page (and the same path the cell toolbar's Run button re-posts).
/// One kernel session per page, blocks submitted in document order, so blocks that share REPL state
/// (block #2 referencing block #1's variable) execute exactly as they do on the rendered page.
///
/// <para>This is the runtime complement of <see cref="DocumentationCodeBlockCompilationTest"/> (which
/// only compiles): a block that compiles but throws, times out, or errors at runtime fails HERE,
/// naming the page and the block. Together they make the docs' executable examples a contract.</para>
///
/// <para>Non-C# blocks (e.g. <c>python</c> fences) route to a connected foreign-language worker in
/// production; no worker is connected in this harness, so they are skipped LOUDLY (named in the test
/// output) rather than silently ignored.</para>
/// </summary>
[Collection("KernelTests")]
public class DocExecutableBlocksTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override bool ShareMeshAcrossTests => true;

    /// <summary>
    /// Coverage ratchet — the number of doc pages carrying at least one executable block at the time
    /// this test was last updated. Converting an executable block back to a prose-only fence (or
    /// deleting a page's examples) drops the count below the ratchet and fails
    /// <see cref="Coverage_DoesNotRegress"/>. RAISE the ratchet when you add executable pages.
    /// </summary>
    private const int MinPagesWithExecutableBlocks = 51;

    /// <summary>Coverage ratchet — total executable blocks across all doc pages. See above.</summary>
    private const int MinExecutableBlocks = 122;

    private static readonly Lazy<IReadOnlyDictionary<string, IReadOnlyList<SubmitCodeRequest>>> PageSubmissions =
        new(() =>
        {
            var assembly = typeof(DocumentationExtensions).Assembly;
            var prefix = $"{assembly.GetName().Name}.Data.";
            var result = new Dictionary<string, IReadOnlyList<SubmitCodeRequest>>();
            foreach (var name in assembly.GetManifestResourceNames()
                         .Where(n => n.StartsWith(prefix, StringComparison.Ordinal)
                                     && n.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(n => n, StringComparer.Ordinal))
            {
                using var stream = assembly.GetManifestResourceStream(name)!;
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var markdown = reader.ReadToEnd();
                var submissions = MarkdownViewLogic.ExtractCodeSubmissions(markdown, null, null);
                if (submissions is { Count: > 0 })
                    result[name] = submissions;
            }
            return result;
        });

    /// <summary>Every embedded doc page that carries at least one executable block.</summary>
    public static TheoryData<string> PagesWithExecutableBlocks
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var page in PageSubmissions.Value.Keys)
                data.Add(page);
            return data;
        }
    }

    /// <summary>Activity-hosted kernel session — the same shape the markdown view creates per page view.</summary>
    private async Task<Address> CreateKernelSession(string pageName)
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var kernelId = Guid.NewGuid().ToString("N");
        const string ownerPath = "rbuergi";
        var activityNamespace = $"{ownerPath}/_Activity";
        var activityNode = new MeshNode($"docblocks-{kernelId}", activityNamespace)
        {
            Name = $"Doc-block kernel session ({pageName})",
            NodeType = "Activity",
            MainNode = ownerPath,
            State = MeshNodeState.Active,
            Content = new ActivityLog("KernelExecution") { Status = ActivityStatus.Running }
        };
        await meshService.CreateNode(activityNode).Should().Emit();
        return new Address($"{activityNamespace}/docblocks-{kernelId}");
    }

    [Theory(Timeout = 240_000)]
    [MemberData(nameof(PagesWithExecutableBlocks))]
    public async Task EveryExecutableBlock_ExecutesInKernel(string embeddedResourceName)
    {
        var submissions = PageSubmissions.Value[embeddedResourceName];
        var client = GetClient();
        var kernelAddress = await CreateKernelSession(embeddedResourceName);
        var failures = new List<string>();

        // Document order on ONE session: later blocks may reference earlier blocks' REPL state.
        foreach (var submission in submissions)
        {
            if (!string.Equals(submission.Language, "csharp", StringComparison.OrdinalIgnoreCase))
            {
                Output.WriteLine(
                    $"SKIPPED block '{submission.Id}' (language '{submission.Language}'): foreign-language " +
                    "blocks execute on a connected worker (py/python-kernel), which this harness does not run.");
                continue;
            }

            Output.WriteLine($"--- executing block '{submission.Id}' on {kernelAddress}");
            var response = await AwaitResponseAsync(submission, o => o.WithTarget(kernelAddress), client);
            if (response.Message.Success)
                Output.WriteLine($"    succeeded: '{submission.Id}'");
            else
                failures.Add($"page '{embeddedResourceName}' block '{submission.Id}' failed:\n{response.Message.Error}");
        }

        failures.Should().BeEmpty(
            "every executable block on {0} must execute green in the kernel — the page runs this exact "
            + "code on every view. Failures:\n{1}",
            embeddedResourceName, string.Join("\n\n", failures));
    }

    /// <summary>
    /// The coverage ratchet: documentation code samples are executable (see
    /// Doc/Architecture/AuthoringDocumentation → "Code samples are executable"). If a change converts
    /// executable blocks back into prose-only fences, this fails — deliberately. Raise the constants
    /// when adding executable pages/blocks.
    /// </summary>
    [Fact]
    public void Coverage_DoesNotRegress()
    {
        var pages = PageSubmissions.Value;
        var totalBlocks = pages.Values.Sum(s => s.Count);
        Output.WriteLine($"Pages with executable blocks: {pages.Count}; total executable blocks: {totalBlocks}");

        pages.Count.Should().BeGreaterThanOrEqualTo(MinPagesWithExecutableBlocks,
            "documentation pages with executable code samples must not regress to prose-only fences "
            + "(raise the ratchet when adding pages)");
        totalBlocks.Should().BeGreaterThanOrEqualTo(MinExecutableBlocks,
            "the total number of executable doc blocks must not regress "
            + "(raise the ratchet when adding blocks)");
    }

    /// <summary>
    /// A <c>--render</c> block must END IN AN EXPRESSION, never a statement.
    ///
    /// <para>
    /// 🚨 This is the one defect <see cref="EveryExecutableBlock_ExecutesInKernel"/> structurally
    /// CANNOT catch. Roslyn scripting yields a submission value only for a trailing EXPRESSION; a
    /// trailing statement (anything ending in <c>;</c>) yields nothing. The cell then runs perfectly
    /// — <see cref="SubmitCodeResponse.Success"/> is <c>true</c>, there is no <c>Error</c> — but it
    /// produces NO control, so the page's area never receives its first data and renders the
    /// "Rendering {area}… awaiting first data" skeleton forever. And
    /// <see cref="SubmitCodeResponse"/> carries only <c>SubmissionId</c>/<c>Success</c>/<c>Error</c>
    /// — no produced value — so no execution-level assertion can see the difference. The check has
    /// to be on the SOURCE.
    /// </para>
    ///
    /// <para>
    /// Found 2026-08-06: <c>DataMesh/CRUD</c>, <c>DataMesh/NodeTypeConfiguration</c> and
    /// <c>GUI/NodeMenu</c> had each acquired a trailing semicolon and had been rendering an empty
    /// skeleton in the browser, green in CI the whole time. Only the browser sweep
    /// (<c>DocExamplesRenderTest</c>) saw it, and that suite does not run in CI — so nothing caught
    /// it. This fact closes that gap in the suite that DOES run.
    /// </para>
    /// </summary>
    [Fact]
    public void RenderBlocks_EndInAnExpression_SoTheAreaReceivesAControl()
    {
        var assembly = typeof(DocumentationExtensions).Assembly;
        var prefix = $"{assembly.GetName().Name}.Data.";
        var offenders = new List<string>();
        var checkedBlocks = 0;

        foreach (var name in assembly.GetManifestResourceNames()
                     .Where(n => n.StartsWith(prefix, StringComparison.Ordinal)
                                 && n.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(n => n, StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var lines = reader.ReadToEnd().Split('\n');

            for (var i = 0; i < lines.Length; i++)
            {
                // A --render fence, per CommonMark: ≤3 spaces of indent then a ``` run. A
                // deeper-indented fence is CONTENT (an escaped teaching sample), not a fence.
                if (!Regex.IsMatch(lines[i], @"^ {0,3}`{3,}\s*csharp\s+.*--render(\s|$)"))
                    continue;

                var body = new List<string>();
                var j = i + 1;
                for (; j < lines.Length && !Regex.IsMatch(lines[j], @"^ {0,3}`{3,}\s*\r?$"); j++)
                    body.Add(lines[j].TrimEnd('\r'));

                var last = body.LastOrDefault(l =>
                    !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith("//", StringComparison.Ordinal));
                checkedBlocks++;

                if (last is not null && last.TrimEnd().EndsWith(";", StringComparison.Ordinal))
                {
                    var area = Regex.Match(lines[i], @"--render\s+(\S+)").Groups[1].Value;
                    offenders.Add(
                        $"{name[prefix.Length..]} (--render {area}): last line is a STATEMENT — '{last.Trim()}'");
                }

                i = j;
            }
        }

        Output.WriteLine($"--render blocks checked: {checkedBlocks}");
        checkedBlocks.Should().BeGreaterThan(0, "the scan must actually find --render blocks");
        offenders.Should().BeEmpty(
            "a --render block must end in an EXPRESSION so the submission yields the control the "
            + "area binds to; a trailing ';' executes green but renders an empty skeleton forever. "
            + "Drop the trailing semicolon. Offenders:\n{0}",
            string.Join("\n", offenders));
    }
}
