using System.Text.RegularExpressions;

namespace MeshWeaver.Portal.E2E;

/// <summary>
/// Enumerates EVERY documentation page carrying executable <c>--render</c> examples, from the doc
/// sources copied beside the tests (<c>DocData/**/*.md</c>, see the csproj) — so new doc pages are
/// covered by the render sweep automatically, without anyone remembering to extend a hard-coded
/// list. The kernel-level "does every block still execute" guarantee lives in CI
/// (<c>DocExecutableBlocksTest</c>); this catalog feeds the BROWSER-level sweep that proves the
/// rendered page mounts every example without error placeholders.
/// </summary>
public static class DocExampleCatalog
{
    /// <summary>
    /// A fence line per CommonMark: up to 3 spaces of indentation, then a backtick/tilde run of
    /// length ≥ 3, then the info string. A deeper-indented fence (AuthoringDocumentation's escaped
    /// teaching samples sit at 4 spaces inside a <c>```text</c> fence) is CONTENT, not a fence.
    /// </summary>
    private static readonly Regex FenceLine = new(@"^ {0,3}(`{3,}|~{3,})\s*(.*)$", RegexOptions.Compiled);

    private static readonly Regex RenderFlag = new(@"(^|\s)--render(\s|$)", RegexOptions.Compiled);

    /// <summary>
    /// Pages whose C#-fenced <c>--render</c> example EMBEDS a layout area that needs a LANGUAGE
    /// WORKER (the python gate) — the fence-language heuristic can't see this: e.g.
    /// <c>Doc/DataMesh/CallingPython</c>'s render fence is <c>csharp</c>, but it renders a
    /// <c>PythonDemo</c> area that only resolves where the python worker is deployed. Without it the
    /// area renders "Area not found". Kept small + explicit (a visible skip, not a silent pass).
    /// </summary>
    private static readonly IReadOnlySet<string> WorkerDependentPages =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Doc/DataMesh/CallingPython" };

    /// <summary>One page's executable-example stats.</summary>
    public sealed record PageExamples(int RenderCount, bool CSharpOnly);

    /// <summary>A page whose examples can only EXECUTE where a language worker (python) is deployed —
    /// either a non-C# fence, or a C# fence that embeds a worker-dependent area (see
    /// <see cref="WorkerDependentPages"/>). The e2e stack ships no python gate, so these skip.</summary>
    public static bool NeedsLanguageWorker(string docPath, PageExamples examples)
        => !examples.CSharpOnly || WorkerDependentPages.Contains(docPath);

    /// <summary>All doc pages (path → number of <c>--render</c> examples), examples or not.</summary>
    public static IReadOnlyDictionary<string, int> AllPages()
        => AllPageExamples().ToDictionary(p => p.Key, p => p.Value.RenderCount, StringComparer.OrdinalIgnoreCase);

    /// <summary>All doc pages with example counts AND whether every example is C# — pages whose
    /// examples need a LANGUAGE WORKER (python) can only execute where that worker is deployed.</summary>
    public static IReadOnlyDictionary<string, PageExamples> AllPageExamples()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "DocData");
        var pages = new Dictionary<string, PageExamples>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(root))
            return pages;
        foreach (var file in Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            var rawPath = relative[..^".md".Length];
            if (rawPath.EndsWith("/index", StringComparison.OrdinalIgnoreCase))
                rawPath = rawPath[..^"/index".Length];
            else if (rawPath.Equals("index", StringComparison.OrdinalIgnoreCase))
                rawPath = "";
            var docPath = rawPath.Length == 0 ? "Doc" : $"Doc/{rawPath}";
            pages[docPath] = Analyze(File.ReadAllText(file));
        }
        return pages;
    }

    /// <summary>
    /// Counts top-level fenced blocks whose info string carries <c>--render</c> — fence-aware, so
    /// samples ESCAPED inside an enclosing fence (a <c>```text</c> or four-backtick teaching block)
    /// never count. Mirrors how the markdown pipeline decides what becomes an executable cell.
    /// </summary>
    public static int CountRenderExamples(string markdown) => Analyze(markdown).RenderCount;

    private static PageExamples Analyze(string markdown)
    {
        var count = 0;
        var csharpOnly = true;
        string? openMarker = null;
        foreach (var line in markdown.Split('\n'))
        {
            var m = FenceLine.Match(line.TrimEnd());
            if (!m.Success)
                continue;
            var marker = m.Groups[1].Value;
            var info = m.Groups[2].Value;
            if (openMarker is null)
            {
                openMarker = marker;
                if (RenderFlag.IsMatch(info))
                {
                    count++;
                    // The fence language routes the submission (```python → the Python worker).
                    var language = info.TrimStart().Split(' ', '\t')[0];
                    if (!string.IsNullOrEmpty(language)
                        && !language.Equals("csharp", StringComparison.OrdinalIgnoreCase)
                        && !language.Equals("cs", StringComparison.OrdinalIgnoreCase))
                        csharpOnly = false;
                }
            }
            // A closing fence: same character, at least the opening run's length, no info string.
            else if (marker[0] == openMarker[0] && marker.Length >= openMarker.Length && info.Trim().Length == 0)
            {
                openMarker = null;
            }
        }
        return new PageExamples(count, csharpOnly);
    }
}
