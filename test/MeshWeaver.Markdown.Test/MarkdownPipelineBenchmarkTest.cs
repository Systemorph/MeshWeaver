using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Markdig;
using Xunit;

namespace MeshWeaver.Markdown.Test;

/// <summary>
/// Microbenchmarks for the markdown render pipeline. These are not regression tests —
/// they print timings to test output so we can identify the hotspots that dominate
/// CollaborativeMarkdownView's per-keystroke rerender path.
///
/// Run with:
///   dotnet test test/MeshWeaver.Markdown.Test --filter "FullyQualifiedName~MarkdownPipelineBenchmark" --logger "console;verbosity=detailed"
/// </summary>
public class MarkdownPipelineBenchmarkTest(ITestOutputHelper output)
{
    // Realistic samples copied from the repo's own docs so the corpus is representative
    // of what the portal renders. Sizes are chosen to span the range CollaborativeMarkdownView
    // sees in practice (chat message → README → long architecture doc).
    private static readonly string DocsRoot = Path.Combine(
        FindRepoRoot(), "src", "MeshWeaver.Documentation", "Data", "Architecture");

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "MeshWeaver.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Could not find repo root");
    }

    private static string Load(string name) => File.ReadAllText(Path.Combine(DocsRoot, name));

    [Fact]
    public void Profile_RenderPipeline_Hotspots()
    {
        var samples = new (string Label, string Content)[]
        {
            ("small  (3KB)", Load("MessageBasedCommunication.md")),
            ("medium (13KB)", Load("PostgresSchemaArchitecture.md")),
            ("large  (66KB)", Load("AsynchronousCalls.md")),
        };

        // Sample with 50 annotation markers — exercises the regex-replace path
        // that the collaborative view runs every keystroke
        var annotated = BuildAnnotatedSample(samples[1].Content, 50);

        output.WriteLine("=== Markdown render pipeline microbenchmark ===");
        output.WriteLine($"Iterations: warmup=20, measured=200 per phase");
        output.WriteLine("");

        // Phase 1: pipeline construction. Cleared cache → measures cold path;
        // warm path → measures cache lookup.
        output.WriteLine("--- Phase 1: MarkdownExtensions.CreateMarkdownPipeline ---");
        MarkdownExtensions.ClearPipelineCache();
        Bench("pipeline build (cold, cache cleared each call)", iterations: 50, () =>
        {
            MarkdownExtensions.ClearPipelineCache();
            var p = MarkdownExtensions.CreateMarkdownPipeline(null, "rbuergi/SomeNode");
            return p.GetHashCode();
        });
        Bench("pipeline build (warm, cache hit)", iterations: 200, () =>
        {
            var p = MarkdownExtensions.CreateMarkdownPipeline(null, "rbuergi/SomeNode");
            return p.GetHashCode();
        });
        output.WriteLine("");

        // Phase 2: TransformAnnotations on content WITH NO markers (the common case —
        // every doc render calls this even when the doc has no comments/track-changes)
        output.WriteLine("--- Phase 2: AnnotationMarkdownExtension.TransformAnnotations (no markers) ---");
        foreach (var (label, content) in samples)
        {
            Bench($"  {label}", iterations: 200, () =>
                AnnotationMarkdownExtension.TransformAnnotations(content).Length);
        }
        output.WriteLine("");

        // Phase 3: TransformAnnotations on content WITH markers (collaborative editing case)
        output.WriteLine("--- Phase 3: TransformAnnotations (with 50 markers) ---");
        Bench($"  medium+50 markers", iterations: 200, () =>
            AnnotationMarkdownExtension.TransformAnnotations(annotated).Length);
        output.WriteLine("");

        // Phase 4: Parse only (with a pre-built pipeline so we isolate parse cost)
        output.WriteLine("--- Phase 4: Markdig.Markdown.Parse (pipeline pre-built) ---");
        var sharedPipeline = MarkdownExtensions.CreateMarkdownPipeline(null, "rbuergi/SomeNode");
        foreach (var (label, content) in samples)
        {
            Bench($"  {label}", iterations: 200, () =>
                Markdig.Markdown.Parse(content, sharedPipeline).Count);
        }
        output.WriteLine("");

        // Phase 5: Render-to-HTML using the standard renderer (parse done up front,
        // amortized across iterations — measure render only)
        output.WriteLine("--- Phase 5: document.ToHtml (standard renderer, parse pre-done) ---");
        foreach (var (label, content) in samples)
        {
            var doc = Markdig.Markdown.Parse(content, sharedPipeline);
            Bench($"  {label}", iterations: 200, () =>
                doc.ToHtml(sharedPipeline).Length);
        }
        output.WriteLine("");

        // Phase 6: Render with the SourceMap renderer (the collaborative view's renderer)
        output.WriteLine("--- Phase 6: SourceMapHtmlRenderer.RenderWithSourceMap (parse pre-done) ---");
        foreach (var (label, content) in samples)
        {
            var doc = Markdig.Markdown.Parse(content, sharedPipeline);
            Bench($"  {label}", iterations: 200, () =>
                SourceMapHtmlRenderer.RenderWithSourceMap(doc, sharedPipeline).Length);
        }
        output.WriteLine("");

        // Phase 7: Full MarkdownViewLogic.Render (the simple-MarkdownView path —
        // pipeline build + transform + parse + ToHtml + ExtractSubmissions)
        output.WriteLine("--- Phase 7: MarkdownViewLogic.Render (end-to-end, pipeline rebuilt every call) ---");
        foreach (var (label, content) in samples)
        {
            Bench($"  {label}", iterations: 200, () =>
                MarkdownViewLogic.Render(content, null, "rbuergi/SomeNode").Html.Length);
        }
        output.WriteLine("");

        // Phase 8: Full collaborative-view render (the per-keystroke path —
        // transform + pipeline build + parse + source-map render)
        output.WriteLine("--- Phase 8: Collaborative full render (per-keystroke path) ---");
        foreach (var (label, content) in samples)
        {
            Bench($"  {label}", iterations: 200, () =>
            {
                var transformed = AnnotationMarkdownExtension.TransformAnnotations(content);
                var pipeline = MarkdownExtensions.CreateMarkdownPipeline(null, "rbuergi/SomeNode");
                var doc = Markdig.Markdown.Parse(transformed, pipeline);
                return SourceMapHtmlRenderer.RenderWithSourceMap(doc, pipeline).Length;
            });
        }
        output.WriteLine("");

        output.WriteLine("=== End of benchmark ===");
    }

    private void Bench(string label, int iterations, Func<int> action)
    {
        // Warmup — JIT, populate caches, etc.
        for (int i = 0; i < 20; i++) action();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var sw = Stopwatch.StartNew();
        long sink = 0;
        for (int i = 0; i < iterations; i++) sink += action();
        sw.Stop();

        var totalUs = sw.Elapsed.TotalMicroseconds;
        var perOpUs = totalUs / iterations;
        var perOpMs = perOpUs / 1000.0;
        output.WriteLine(
            $"{label,-40} {perOpUs,10:F1} µs/op   ({perOpMs,7:F3} ms)   [sink={sink}]");
    }

    /// <summary>
    /// Wraps every paragraph break with a <c>&lt;!--insert:id:author:date--&gt;...&lt;!--/insert:id--&gt;</c>
    /// block to simulate a heavily annotated document.
    /// </summary>
    private static string BuildAnnotatedSample(string content, int markerCount)
    {
        var lines = content.Split('\n');
        var paragraphIndices = lines
            .Select((line, idx) => (line, idx))
            .Where(t => !string.IsNullOrWhiteSpace(t.line) && !t.line.StartsWith('#'))
            .Select(t => t.idx)
            .Take(markerCount)
            .ToList();

        for (int i = 0; i < paragraphIndices.Count; i++)
        {
            var idx = paragraphIndices[i];
            lines[idx] = $"<!--insert:m{i}:alice:2026-01-01-->{lines[idx]}<!--/insert:m{i}-->";
        }
        return string.Join('\n', lines);
    }
}
