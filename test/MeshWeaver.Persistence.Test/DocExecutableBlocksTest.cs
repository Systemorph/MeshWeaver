using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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
    private const int MinExecutableBlocks = 114;

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
}
