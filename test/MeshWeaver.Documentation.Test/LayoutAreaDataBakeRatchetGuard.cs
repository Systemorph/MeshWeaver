using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 <b>Layout areas are TEMPLATES: they emit at once and BIND their data — they never load it on
/// the hub and bake it into controls.</b> This ratchet makes sure the number of areas that still do
/// only ever goes DOWN (Doc/GUI/DataBinding → "Templates first, data later").
///
/// <para><b>The shape it counts — a "bake unit".</b> A method, local function or lambda whose
/// parameters name a <c>LayoutAreaHost</c> (typed, or the conventional untyped
/// <c>(host, ctx) =&gt;</c>) and whose body BOTH reads data — <see cref="LoadPattern"/>: a node
/// stream, a query, a workspace stream, or a same-file helper that does one of those — AND builds
/// controls (<see cref="ControlPattern"/>). That pair is the anti-pattern: the hub waits for the
/// data, then constructs controls out of the values, so the page shows a spinner until the slowest
/// read answers and the result is a snapshot. The sanctioned shape declares the controls with node
/// PATHS / pointers (<c>LayoutAreaReference.GetMeshNodeDataContext</c>,
/// <c>MeshNodeThumbnailControl { NodePath }</c>, <c>Controls.MeshSearch</c>) and lets the GUI
/// resolve them through <c>IMeshNodeStreamCache</c>.</para>
///
/// <para><b>Honest about what a text scan cannot see.</b> It is a heuristic: a load reached through
/// a helper in ANOTHER file, or a service method it does not name, is missed (recall is a floor,
/// not a census); and an area that reads data only to decide STRUCTURE — a permission, which
/// branch to show — is counted too, because from the text it is indistinguishable from baking. The
/// allow file is therefore an inventory to shrink, not a verdict on each line. The counts are per
/// file (bake units in that file), which is robust to a method being renamed.</para>
///
/// <para><b>A ratchet may only SHRINK.</b> A new file, a raised count or a raised TOTAL fails. A
/// stale line (fewer found than allowed) is REPORTED, never failed — two PRs converting areas
/// concurrently would otherwise red <c>main</c> on whichever merged second.</para>
/// </summary>
public class LayoutAreaDataBakeRatchetGuard(ITestOutputHelper output)
{
    /// <summary>Production C# and the in-mesh NodeType sources under <c>samples/</c> and the
    /// documentation tree (compiled at runtime, invisible to <c>dotnet build</c>).</summary>
    private static readonly string[] ScannedRoots = ["src", "memex", "samples"];

    private const string AllowFileName = "LayoutAreaDataBakeSites.allow";

    /// <summary>The seeded inventory's size. Lower it in the same change that lowers a line.</summary>
    private const int TotalBudget = 34;

    /// <summary>Reads that make a unit wait on data.</summary>
    internal static readonly Regex LoadPattern = new(
        @"\b(GetMeshNodeStream|ObserveQuery|GetQuery|QueryAsync|GetRemoteStream|GetSingle|ReduceToTypes|GetMeshNode|GetMeshNodeOutcome|GetObservable|ObserveNode|ObserveChildren|GetChildren|GetNodeStream|GetStreamForPartition|GetMarkdownContent|GetFileContent|GetContentAsText|ObserveNodeTypeRelease)\s*(<[^;()]*>)?\s*\("
        + @"|\.Query\s*(<[^;()]*>)?\s*\("
        + @"|\b(Workspace|workspace)\s*\.\s*GetStream\s*(<[^;()]*>)?\s*\("
        + @"|\.GetStream\s*<[^;()]*>\s*\(",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Control construction.</summary>
    internal static readonly Regex ControlPattern = new(
        @"\bControls\s*\.\s*\w+|\bnew\s+\w+Control\b|\b\w+Control\s*\.\s*(For|From)\w*\(|\.WithView\s*\(",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex Header = new(
        @"(?<name>[A-Za-z_]\w*)\s*(<[^<>()]*(<[^<>()]*>[^<>()]*)*>)?\s*\((?<params>[^()]*(\([^()]*\)[^()]*)*)\)\s*(where[^{=;]*)?(?<open>\{|=>)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex UntypedAreaLambda = new(
        @"\(\s*(?:LayoutAreaHost\s+)?(host|h|area|layoutArea|layoutAreaHost)\s*,\s*(?:RenderingContext\s+)?\w+\s*\)\s*=>|\b(host|layoutArea)\s*=>",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly ImmutableHashSet<string> Keywords =
    [
        "if", "for", "foreach", "while", "switch", "catch", "using", "lock", "return", "new", "nameof",
        "typeof", "when", "select", "sizeof", "default", "base", "this", "static",
    ];

    /// <summary>The ratchet itself: no file may hold more bake units than its allow-file line, and the
    /// total may never exceed <see cref="TotalBudget"/>.</summary>
    [Fact]
    public void NoNewLayoutAreaBakesDataIntoControls()
    {
        var root = SourceScan.FindRepoRoot();
        var allowed = SourceScan.ReadAllowFile(Path.Combine(root, "test", AllowFileName), AllowFileName);
        var found = Scan(root);

        var failures = new List<string>();
        foreach (var (file, count) in found.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!allowed.TryGetValue(file, out var budget))
                failures.Add($"  NEW   {file}\t{count} — {string.Join(", ", UnitsIn(Path.Combine(root, file)))}");
            else if (count > budget)
                failures.Add($"  MORE  {file}\t{count} > {budget} allowed — a layout area that bakes "
                    + "data was ADDED to a file that already carries one.");
        }

        var total = allowed.Values.Sum();
        if (total > TotalBudget)
            failures.Add($"  TOTAL {total} allowances > {TotalBudget} budgeted — the inventory GREW. "
                + "Adding a line to " + AllowFileName + " is not a fix.");

        foreach (var (file, budget) in allowed.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var count = found.GetValueOrDefault(file, 0);
            if (count < budget)
                output.WriteLine($"STALE (please tidy): {file} — {count} found, {budget} allowed. "
                    + $"{(count == 0 ? "Delete the line" : $"Lower it to {count}")} and lower "
                    + $"TotalBudget by {budget - count}.");
        }

        Assert.True(failures.Count == 0,
            "A layout area loads data on the hub and builds controls out of it. Emit the TEMPLATE at "
            + "once and BIND the data instead: node-bound controls (LayoutAreaReference."
            + "GetMeshNodeDataContext(path) + a JsonPointerReference, MeshNodeThumbnailControl { NodePath }, "
            + "Controls.MeshSearch for lists) resolve on the GUI side through IMeshNodeStreamCache. "
            + "See Doc/GUI/DataBinding → \"Templates first, data later\" for the before/after.\n"
            + string.Join("\n", failures)
            + "\n\nCurrent inventory, in allow-file form:\n"
            + string.Join("\n", found.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => $"{kv.Key}\t{kv.Value}")));
    }

    /// <summary>
    /// Non-vacuity on the real tree: the scan must find bake units, or the matcher, the roots or the
    /// repo root is broken and the ratchet above would pass on no evidence.
    /// </summary>
    [Fact]
    public void TheScannerSeesTheProductionTree()
    {
        var found = Scan(SourceScan.FindRepoRoot());
        Assert.True(found.Values.Sum() > 0,
            "The scan found no layout area that bakes data anywhere under " + string.Join(", ", ScannedRoots)
            + ". Either every area was converted — then delete this guard and its allow file in the "
            + "same change — or the scanner is broken and the ratchet is checking nothing.");
    }

    /// <summary>What the matcher counts, and what it must not — re-run on every CI run against
    /// planted text, so a matcher that silently stops seeing the shape fails here.</summary>
    [Fact]
    public void TheMatcherCountsBakingNotBinding()
    {
        // The anti-pattern: wait for the node, then build a control out of its values.
        Assert.Equal(["Thumbnail"], UnitsInCode("""
            public static UiControl Thumbnail(LayoutAreaHost host, RenderingContext _)
                => Controls.Stack.WithView((h, c) => host.Workspace.GetMeshNodeStream()
                    .Select(node => MeshNodeThumbnailControl.FromNode(node, "x")));
            """));

        // An untyped area lambda reading a query and building a grid.
        Assert.Single(UnitsInCode("""
            void Register(LayoutDefinition layout) => layout.WithView("List", (host, ctx) =>
                host.Workspace.GetQuery("k", "nodeType:X").Select(nodes => Controls.Markdown("n")));
            """));

        // A load through a same-file helper still counts.
        Assert.Equal(["Overview"], UnitsInCode("""
            static IObservable<MeshNode?> Load(IWorkspace ws) => ws.GetMeshNodeStream("p");
            public static IObservable<UiControl?> Overview(LayoutAreaHost host, RenderingContext _)
                => Load(host.Workspace).Select(n => (UiControl?)Controls.Markdown(n?.Name ?? ""));
            """));

        // The sanctioned shape: a template bound by path — no read on the hub.
        Assert.Empty(UnitsInCode("""
            public static UiControl Thumbnail(LayoutAreaHost host, RenderingContext _)
                => new MeshNodeThumbnailControl(host.Hub.Address.ToString(), "");
            """));

        // A read that builds no control (a menu provider) is not a bake.
        Assert.Empty(UnitsInCode("""
            static IObservable<bool> Show(LayoutAreaHost host, RenderingContext ctx)
                => host.Workspace.GetMeshNodeStream().Select(n => n is not null);
            """));

        // Text in comments and strings is not code.
        Assert.Empty(UnitsInCode("""
            // public static UiControl X(LayoutAreaHost host) => host.Workspace.GetMeshNodeStream().Select(n => Controls.Markdown(""));
            var s = "UiControl X(LayoutAreaHost host) => GetMeshNodeStream().Select(Controls.Markdown)";
            """));
    }

    /// <summary>The names of the bake units in <paramref name="code"/> (lambdas as <c>lambda@Enclosing</c>).</summary>
    internal static IReadOnlyList<string> UnitsInCode(string code)
    {
        var masked = SourceScan.MaskCommentsAndStrings(code);
        var helpers = LoaderHelpers(masked);
        var helperCall = helpers.Count == 0
            ? null
            : new Regex(@"\b(" + string.Join("|", helpers.Select(Regex.Escape)) + @")\s*\(", RegexOptions.CultureInvariant);

        var result = new List<string>();
        foreach (var (headerStart, start, end, name) in Units(masked))
        {
            var body = masked[start..end];
            var loads = LoadPattern.IsMatch(body) || (helperCall?.IsMatch(body) ?? false);
            if (loads && ControlPattern.IsMatch(body))
                result.Add(name ?? "lambda@" + EnclosingName(masked, headerStart));
        }
        return result;
    }

    private static IEnumerable<string> UnitsIn(string path) => UnitsInCode(File.ReadAllText(path));

    private static Dictionary<string, int> Scan(string root)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var file in SourceScan.SourceFiles(root, ScannedRoots))
        {
            if (!file.EndsWith(".cs", StringComparison.Ordinal) && !file.EndsWith(".csx", StringComparison.Ordinal))
                continue;
            string text;
            try { text = File.ReadAllText(file); }
            catch (IOException) { continue; } // a file a concurrent build is writing is not evidence

            if (!text.Contains("LayoutAreaHost", StringComparison.Ordinal)
                && !UntypedAreaLambda.IsMatch(text))
                continue;

            var count = UnitsInCode(text).Count;
            if (count > 0)
                result[SourceScan.Relative(root, file)] = count;
        }
        return result;
    }

    /// <summary>Same-file helpers (not areas themselves) that read data and build no control — a
    /// call to one inside an area counts as a load.</summary>
    private static ImmutableHashSet<string> LoaderHelpers(string masked)
    {
        var names = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        foreach (Match m in Header.Matches(masked))
        {
            var name = m.Groups["name"].Value;
            if (Keywords.Contains(name) || m.Groups["params"].Value.Contains("LayoutAreaHost", StringComparison.Ordinal))
                continue;
            var (start, end) = BodySpan(masked, m);
            var body = masked[start..end];
            if (LoadPattern.IsMatch(body) && !ControlPattern.IsMatch(body))
                names.Add(name);
        }
        return names.ToImmutable();
    }

    /// <summary>Outermost units only: a lambda inside an area method is part of that area.</summary>
    private static IEnumerable<(int HeaderStart, int Start, int End, string? Name)> Units(string masked)
    {
        var all = new List<(int HeaderStart, int Start, int End, string? Name)>();
        foreach (Match m in Header.Matches(masked))
        {
            var name = m.Groups["name"].Value;
            if (Keywords.Contains(name) || !m.Groups["params"].Value.Contains("LayoutAreaHost", StringComparison.Ordinal))
                continue;
            var (start, end) = BodySpan(masked, m);
            all.Add((m.Index, start, end, name));
        }
        foreach (Match m in UntypedAreaLambda.Matches(masked))
        {
            // `Name(LayoutAreaHost host, RenderingContext ctx) =>` is a METHOD's parameter list,
            // already a unit above — a lambda's list follows `(`, `,`, `=` or `return`, never a name.
            if (m.Value[0] == '(' && PrecededByName(masked, m.Index))
                continue;
            var j = m.Index + m.Length;
            while (j < masked.Length && char.IsWhiteSpace(masked[j])) j++;
            var end = j < masked.Length && masked[j] == '{' ? BlockEnd(masked, j) : ExpressionEnd(masked, j);
            all.Add((m.Index, m.Index, end, null));
        }

        var outer = new List<(int HeaderStart, int Start, int End, string? Name)>();
        foreach (var unit in all.OrderBy(u => u.Start).ThenByDescending(u => u.End))
        {
            if (outer.Count > 0 && unit.Start >= outer[^1].Start && unit.End <= outer[^1].End)
                continue;
            outer.Add(unit);
        }
        return outer;
    }

    private static bool PrecededByName(string code, int index)
    {
        var i = index - 1;
        while (i >= 0 && char.IsWhiteSpace(code[i])) i--;
        if (i < 0 || !(char.IsLetterOrDigit(code[i]) || code[i] is '_' or '>'))
            return false;
        var end = i + 1;
        while (i >= 0 && (char.IsLetterOrDigit(code[i]) || code[i] == '_')) i--;
        return code[(i + 1)..end] != "return";
    }

    private static (int Start, int End) BodySpan(string masked, Match header)
    {
        var open = header.Groups["open"];
        return open.Value == "{"
            ? (open.Index, BlockEnd(masked, open.Index))
            : (open.Index + open.Length, ExpressionEnd(masked, open.Index + open.Length));
    }

    private static int BlockEnd(string code, int openBrace)
    {
        var depth = 0;
        for (var i = openBrace; i < code.Length; i++)
        {
            if (code[i] == '{') depth++;
            else if (code[i] == '}' && --depth == 0) return i + 1;
        }
        return code.Length;
    }

    /// <summary>An expression body or lambda ends at the first <c>;</c>, <c>,</c> or unbalanced
    /// closer at depth zero.</summary>
    private static int ExpressionEnd(string code, int start)
    {
        var depth = 0;
        for (var i = start; i < code.Length; i++)
        {
            var c = code[i];
            if (c is '(' or '{' or '[') depth++;
            else if (c is ')' or '}' or ']')
            {
                if (depth == 0) return i;
                depth--;
            }
            else if (c is ';' or ',' && depth == 0) return i;
        }
        return code.Length;
    }

    private static string EnclosingName(string masked, int position)
    {
        string? best = null;
        foreach (Match m in Header.Matches(masked[..position]))
            if (!Keywords.Contains(m.Groups["name"].Value))
                best = m.Groups["name"].Value;
        return best ?? "?";
    }
}
