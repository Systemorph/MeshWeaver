using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// Governance guard: no click handler subscribes with ONE argument — <c>.Subscribe(onNext)</c> with
/// no error arm — inside a <c>WithClickAction</c> / <c>WithReactiveClickAction</c> lambda.
///
/// <para><b>Why.</b> The shape was everywhere a button reads its form once:
/// <c>ctx.Host.Stream.GetDataStream&lt;T&gt;(formId).Take(1).Subscribe(data =&gt; …)</c> and then
/// <c>return Task.CompletedTask</c>. The click is answered as DONE before the read has even
/// emitted, and a fault in the read — or in the body that handles it — has no observer: Rx rethrows
/// it on whatever thread produced it, the person sees a button that did nothing, and the log names no
/// area. #5968 fixed the data-binding machinery's own subscriptions; this is the click-time half.</para>
///
/// <para><b>The fix at a site.</b> RETURN the read to the click — <c>WithReactiveClickAction(ctx =&gt;
/// read.Take(1).Do(body).Select(_ =&gt; Unit.Default))</c>. The host then owns the subscription:
/// completion answers the click, an error is logged with the area and hub and refused to the client,
/// whose button leaves its pending state showing the reason (<c>LayoutAreaHost.FailClick</c>,
/// <c>Doc/GUI/ButtonPendingState</c>). A continuation that must run AFTER the click has been
/// answered (a background write) keeps its own <c>.Subscribe(onNext, onError)</c> with an error arm
/// that reports and shows the fault.</para>
///
/// <para><b>Scope, stated honestly.</b> This is lexical: it sees a one-argument <c>Subscribe</c>
/// written INSIDE the click lambda. A helper method the lambda calls is outside its reach — the
/// sweep that introduced this guard converted those by hand (<c>EditorExtensions</c>'s collection
/// chips, <c>PromptCell.Send</c>, the Settings/IconPicker helpers), and they now return the
/// observable, so the lambda reads <c>ctx =&gt; Helper(ctx)</c> and has nothing to subscribe.
/// <c>Subscribe(observer)</c> forwarding a whole observer is also one argument; none exists inside a
/// click lambda in <c>src/</c>, and one would be flagged and must be justified by restructuring.</para>
/// </summary>
public class ClickActionSubscribeHasErrorArmGuard
{
    private static readonly string[] ScannedRoots = ["src"];

    private static readonly Regex ClickAction =
        new(@"\bWith(?:Reactive)?ClickAction\s*\(", RegexOptions.Compiled);

    private static readonly Regex Subscribe = new(@"\.Subscribe\s*\(", RegexOptions.Compiled);

    /// <summary>Zero tolerance: there is no seeded inventory, because the sweep left none.</summary>
    [Fact]
    public void NoClickActionSubscribesWithoutAnErrorArm()
    {
        var root = SourceScan.FindRepoRoot();
        var scanned = 0;
        var clickLambdas = 0;
        var offenders = new List<string>();

        foreach (var file in SourceScan.SourceFiles(root, ScannedRoots)
                     .Where(f => f.EndsWith(".cs", StringComparison.Ordinal)))
        {
            scanned++;
            var text = File.ReadAllText(file);
            var found = Offenders(text, out var lambdas);
            clickLambdas += lambdas;
            offenders.AddRange(found.Select(line => $"{SourceScan.Relative(root, file)}:{line}"));
        }

        scanned.Should().BeGreaterThan(100, "the scan must have found the production tree");
        clickLambdas.Should().BeGreaterThan(20,
            "src/ carries dozens of click actions — a low count means the detector stopped matching");
        offenders.Should().BeEmpty(
            "a one-argument Subscribe inside a click action has no error arm: a fault is rethrown "
            + "unobserved and the person sees a dead button. Return the read to the click with "
            + "WithReactiveClickAction(ctx => read.Take(1).Do(…).Select(_ => Unit.Default)) instead. "
            + "Offenders:\n" + string.Join("\n", offenders));
    }

    /// <summary>
    /// Negative control on the detector itself: the exact pre-sweep shape is flagged, the converted
    /// shape and a two-argument subscribe are not, and parentheses inside strings or comments do not
    /// confuse the argument count.
    /// </summary>
    [Fact]
    public void TheDetector_FlagsTheOldShape_AndPassesTheConvertedOnes()
    {
        const string oldShape = """
            Controls.Button("x").WithClickAction(ctx =>
            {
                ctx.Host.Stream.GetDataStream<string>("f").Take(1).Subscribe(d => Use(d, "a,(b"));
                return Task.CompletedTask;
            });
            """;
        Offenders(oldShape, out var lambdas).Should().ContainSingle();
        lambdas.Should().Be(1);

        const string converted = """
            Controls.Button("x").WithReactiveClickAction(ctx =>
                ctx.Host.Stream.GetDataStream<string>("f").Take(1).Do(d => Use(d)).Select(_ => Unit.Default));
            Controls.Button("y").WithClickAction(ctx =>
            {
                Write(ctx).Subscribe(_ => { }, ex => Report(ctx, ex)); // .Subscribe(x)
                return Task.CompletedTask;
            });
            """;
        Offenders(converted, out lambdas).Should().BeEmpty();
        lambdas.Should().Be(2);

        // Outside a click lambda the rule does not apply (a live binding has its own error path).
        Offenders("stream.Subscribe(x => Use(x));", out _).Should().BeEmpty();
    }

    /// <summary>1-based line numbers of one-argument <c>Subscribe</c> calls inside click lambdas.</summary>
    private static IReadOnlyList<int> Offenders(string text, out int clickLambdas)
    {
        var code = SourceScan.MaskCommentsAndStrings(text);
        var lines = new SortedSet<int>();
        clickLambdas = 0;
        foreach (Match click in ClickAction.Matches(code))
        {
            var open = click.Index + click.Length - 1;
            var close = MatchingClose(code, open);
            if (close < 0)
                continue;
            clickLambdas++;
            var body = code.Substring(open, close - open);
            foreach (Match sub in Subscribe.Matches(body))
            {
                var subOpen = open + sub.Index + sub.Length - 1;
                var subClose = MatchingClose(code, subOpen);
                if (subClose > 0 && TopLevelArguments(code, subOpen, subClose) == 1)
                    lines.Add(LineOf(code, subOpen));
            }
        }
        return lines.ToList();
    }

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

    private static int TopLevelArguments(string code, int open, int close)
    {
        var depth = 0;
        var count = 1;
        var empty = true;
        for (var i = open + 1; i < close; i++)
        {
            var c = code[i];
            if (c is '(' or '[' or '{')
                depth++;
            else if (c is ')' or ']' or '}')
                depth--;
            else if (c == ',' && depth == 0)
                count++;
            if (!char.IsWhiteSpace(c))
                empty = false;
        }
        return empty ? 0 : count;
    }

    private static int LineOf(string code, int index) => code.Take(index).Count(c => c == '\n') + 1;
}
