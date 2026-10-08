using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// Governance guard: a data id that a click handler reads ONCE (<c>GetDataStream&lt;T&gt;(id).Take(1)</c>
/// inside a <c>WithClickAction</c> / <c>WithReactiveClickAction</c> lambda) is SEEDED by the same file
/// outside any click lambda, which means it is seeded when the view is rendered (<c>host.UpdateData(id, …)</c>).
///
/// <para><b>Why.</b> A data id that was never written emits nothing and never completes
/// (<c>GetDataStreamUnsetIdTest</c>). So a returned one-off read on such an id neither completes nor
/// faults, and nothing bounds a returned click observable. The button stays pending until the page
/// goes away. <c>Doc/GUI/ButtonPendingState</c> states the rule. Until this guard existed nothing
/// enforced it (MeshWeaver#6058). <c>WorkingTreeTab</c>'s <c>EditorContentId</c> was written only
/// inside the file-list click. Its Save button happens to be rendered only after that write, so the
/// site was safe only because of that ordering, never because of a seed.</para>
///
/// <para><b>Why a guard and not a framework bound.</b> A timeout on the click observable would turn
/// "the id was never written" into an error some seconds later. That is a symptom bound on a defect
/// the source already shows. The defect is the missing seed, and the seed is what lets an empty submit
/// reach the handler's own validation message.</para>
///
/// <para><b>Scope, stated honestly.</b> This guard is lexical, per file. It matches the id
/// EXPRESSION textually, so a constant, a local or a parameter all match when the seed spells it the
/// same way. It does not prove that the seed runs BEFORE the button renders. It proves only that a
/// write exists outside every click lambda. A read through a helper method the lambda calls is out
/// of its reach.</para>
/// </summary>
public class ClickReadIdIsSeededGuard
{
    private static readonly string[] ScannedRoots = ["src"];

    private static readonly Regex ClickAction =
        new(@"\bWith(?:Reactive)?ClickAction\s*\(", RegexOptions.Compiled);

    private static readonly Regex DataStreamRead =
        new(@"\.GetDataStream\s*<[^;()]*>\s*\(", RegexOptions.Compiled);

    private static readonly Regex OneOff = new(@"^\s*\.\s*(?:Take\s*\(\s*1\s*\)|FirstAsync\s*\()", RegexOptions.Compiled);

    private static readonly Regex Seed = new(@"\.UpdateData\s*\(", RegexOptions.Compiled);

    [Fact]
    public void EveryClickReadId_IsSeededOutsideTheClick()
    {
        var root = SourceScan.FindRepoRoot();
        var scanned = 0;
        var reads = 0;
        var offenders = new List<string>();

        foreach (var file in SourceScan.SourceFiles(root, ScannedRoots)
                     .Where(f => f.EndsWith(".cs", StringComparison.Ordinal)))
        {
            scanned++;
            var found = Offenders(File.ReadAllText(file), out var n);
            reads += n;
            offenders.AddRange(found.Select(o => $"{SourceScan.Relative(root, file)}:{o}"));
        }

        scanned.Should().BeGreaterThan(100, "the scan must have found the production tree");
        reads.Should().BeGreaterThan(5,
            "src/ carries a dozen one-off click reads — a low count means the detector stopped matching");
        offenders.Should().BeEmpty(
            "a click that reads a data id once must find that id written when the view is rendered. "
            + "An unwritten id emits nothing, so the returned read never completes and the button stays "
            + "pending for ever. Seed it with host.UpdateData(id, …) where the view is built. Offenders:\n"
            + string.Join("\n", offenders));
    }

    /// <summary>
    /// Negative control on the detector: the unseeded read is flagged; a read seeded at render (by
    /// constant or by local) is not; a seed that exists only inside another click does NOT count,
    /// because that is the WorkingTreeTab shape; and a live (non-one-off) binding is out of scope.
    /// </summary>
    [Fact]
    public void TheDetector_FlagsAnUnseededRead_AndPassesASeededOne()
    {
        const string unseeded = """
            Controls.Button("x").WithReactiveClickAction(ctx =>
                ctx.Host.Stream.GetDataStream<Dictionary<string, object?>>(FormId).Take(1).Do(d => Use(d)).Select(_ => Unit.Default));
            """;
        Offenders(unseeded, out var reads).Should().ContainSingle().Which.Should().Contain("FormId");
        reads.Should().Be(1);

        const string seededOnlyByAnotherClick = """
            Controls.Button("open").WithClickAction(ctx => { ctx.Host.UpdateData(ContentId, "x"); return Task.CompletedTask; });
            Controls.Button("save").WithReactiveClickAction(ctx =>
                ctx.Host.Stream.GetDataStream<string>(ContentId).Take(1).Do(c => Save(c)).Select(_ => Unit.Default));
            """;
        Offenders(seededOnlyByAnotherClick, out reads).Should().ContainSingle();
        reads.Should().Be(1);

        const string seeded = """
            host.UpdateData(FormId, new Dictionary<string, object?>());
            var formId = $"invite_{id}";
            host.UpdateData(formId, new Dictionary<string, object?>());
            Controls.Button("x").WithReactiveClickAction(ctx =>
                ctx.Host.Stream.GetDataStream<Dictionary<string, object?>>(FormId).Take(1).Do(d => Use(d)).Select(_ => Unit.Default));
            Controls.Button("y").WithReactiveClickAction(ctx =>
                ctx.Host.Stream.GetDataStream<Dictionary<string, object?>>(formId)
                    .Take(1).Do(d => Use(d)).Select(_ => Unit.Default));
            """;
        Offenders(seeded, out reads).Should().BeEmpty();
        reads.Should().Be(2);

        const string liveBinding = """
            Controls.Button("x").WithClickAction(ctx =>
            {
                ctx.Host.Stream.GetDataStream<string>(LiveId).Subscribe(v => Use(v), ex => Report(ex));
                return Task.CompletedTask;
            });
            """;
        Offenders(liveBinding, out reads).Should().BeEmpty();
        reads.Should().Be(0);
    }

    /// <summary>Each unseeded one-off click read as <c>line: id</c>, and the number of one-off reads seen.</summary>
    private static IReadOnlyList<string> Offenders(string text, out int oneOffReads)
    {
        var code = SourceScan.MaskCommentsAndStrings(text);
        oneOffReads = 0;

        var lambdas = new List<(int Open, int Close)>();
        foreach (Match click in ClickAction.Matches(code))
        {
            var open = click.Index + click.Length - 1;
            var close = MatchingClose(code, open);
            if (close > 0)
                lambdas.Add((open, close));
        }

        bool InsideClick(int index) => lambdas.Any(l => index > l.Open && index < l.Close);

        // Ids written outside every click lambda, keyed by the id expression as written.
        var seeded = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match seed in Seed.Matches(code))
        {
            var open = seed.Index + seed.Length - 1;
            if (!InsideClick(open))
                seeded.Add(Normalize(ArgumentText(text, code, open)));
        }

        var offenders = new List<string>();
        var seen = new HashSet<int>();
        foreach (var (lambdaOpen, lambdaClose) in lambdas)
        {
            foreach (Match read in DataStreamRead.Matches(code, lambdaOpen))
            {
                if (read.Index >= lambdaClose)
                    break;
                var open = read.Index + read.Length - 1;
                var close = MatchingClose(code, open);
                if (close < 0 || !seen.Add(open) || !OneOff.IsMatch(code[(close + 1)..Math.Min(code.Length, close + 64)]))
                    continue;
                oneOffReads++;
                var id = Normalize(ArgumentText(text, code, open));
                if (!seeded.Contains(id))
                    offenders.Add($"{LineOf(code, open)}: {id}");
            }
        }
        return offenders;
    }

    /// <summary>The first argument as written in the ORIGINAL text, so a string-literal id keeps its
    /// value (the masked copy blanks it). Masking preserves positions, so the indices are shared.</summary>
    private static string ArgumentText(string text, string code, int open)
    {
        var arg = SourceScan.FirstArgument(code, open);
        return text.Substring(open + 1, arg.Length);
    }

    private static string Normalize(string s) => Regex.Replace(s, @"\s+", "");

    private static int MatchingClose(string code, int open)
    {
        var depth = 0;
        for (var i = open; i < code.Length; i++)
        {
            switch (code[i])
            {
                case '(' or '[' or '{':
                    depth++;
                    break;
                case ')' or ']' or '}':
                    if (--depth == 0)
                        return i;
                    break;
            }
        }
        return -1;
    }

    private static int LineOf(string code, int index) => code.Take(index).Count(c => c == '\n') + 1;
}
