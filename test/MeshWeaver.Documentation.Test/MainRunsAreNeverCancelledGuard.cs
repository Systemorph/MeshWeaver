using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 <b>A run on <c>main</c> must never be cancelled by the next merge</b> (#2412).
///
/// <para><b>Why this is correctness, not runner-minute tuning.</b> Cancelling a superseded run is
/// the right default on a PR branch — the later push tests a strict successor of what was killed,
/// and that is where #2316's ~28% of runner demand is saved. On <c>main</c> the same setting
/// destroys two different things, and neither is recoverable by re-running later:</para>
///
/// <list type="number">
///   <item><description><b>Nothing builds the combination that LANDED.</b> The repo runs
///   <c>strict: false</c> branch protection, so each PR is tested against the <c>main</c> it
///   branched from — the MERGED tree is first compiled by main's own run. Cancel that and a
///   semantic conflict between two independently-green PRs ships unbuilt. On 2026-08-26 five
///   merges inside fifteen seconds put <c>CS0246: 'MeshOperations' could not be found</c> on
///   main exactly this way.</description></item>
///   <item><description><b>Nothing publishes.</b> <c>main-cd.yml</c> gates delivery on the
///   required check <c>Consolidate test results</c> reaching <c>success</c> FOR THAT SHA. CD does
///   still wake on a cancelled run — it subscribes with <c>types: [completed]</c> and cancelled
///   counts as completed — it simply finds no success to act on. A burst of merges therefore
///   publishes nothing, leaving the tail to the hourly reconciler.</description></item>
/// </list>
///
/// <para>Both failures are silent in the same way the rest of this repo's worst bugs are: a
/// cancelled run and a run that was never needed look identical in the runs list, so "main is
/// quiet" reads as healthy. Measured the day the guard was written, main's five consecutive runs
/// between 20:28 and 20:38 were ALL <c>cancelled</c>, each by the next merge.</para>
///
/// <para>🚨 <b>This guard EVALUATES the expression; it does not pattern-match it.</b> The first
/// version asserted only that the text mentioned <c>refs/heads/main</c> — which
/// <c>${{ github.ref == 'refs/heads/main' }}</c> satisfies while cancelling on main, the exact
/// opposite of the intent. That is this repo's standing lesson applied to a gate's own code: a
/// verification step that cannot fail is not a verification step, and a spelling check is not a
/// semantic one. <see cref="Evaluate"/> is a small evaluator for the GitHub expression subset,
/// with its own self-test rows in <see cref="TheEvaluator_IsItselfCorrect"/> — including that
/// counterexample.</para>
/// </summary>
public class MainRunsAreNeverCancelledGuard
{
    private const string Workflow = ".github/workflows/dotnet-test.yml";

    /// <summary>
    /// Both halves of the intent are pinned. Cancelling must be OFF for a push to main (the
    /// correctness half) and must stay ON for a pull-request run (the cost half — silently
    /// disabling superseding everywhere would hand back #2316's ~28% saving with nobody noticing).
    /// </summary>
    [Fact]
    public void BuildAndTest_CancelsOnPrBranches_ButNeverOnMain()
    {
        var body = File.ReadAllText(Path.Combine(FindRepoRoot(), Workflow));

        var cancel = Regex.Match(body, @"^\s*cancel-in-progress:\s*(?<value>.+?)\s*$", RegexOptions.Multiline);

        Assert.True(cancel.Success,
            $"{Workflow} no longer declares cancel-in-progress. If concurrency was removed entirely "
            + "that is fine for main, but this guard can no longer see it — re-point or delete it "
            + "deliberately rather than letting it rot.");

        var expression = cancel.Groups["value"].Value;

        var onMain = Evaluate(expression, new Dictionary<string, string>
        {
            ["github.event_name"] = "push",
            ["github.ref"] = "refs/heads/main",
        });

        Assert.False(onMain,
            $"{Workflow} cancels in-progress runs on main: '{expression}' evaluates TRUE for a push "
            + "to refs/heads/main. Each merge then kills the run for the merge before it, so nothing "
            + "compiles the tree that landed (strict: false means the merged combination is first "
            + "built by main's own run — this is how CS0246 reached main on 2026-08-26) and nothing "
            + "publishes (CD gates on 'Consolidate test results' reaching success for that SHA, "
            + "which a cancelled run never produces). See #2412.");

        var onPullRequest = Evaluate(expression, new Dictionary<string, string>
        {
            ["github.event_name"] = "pull_request",
            ["github.ref"] = "refs/pull/2447/merge",
        });

        Assert.True(onPullRequest,
            $"{Workflow} no longer supersedes runs on PR branches: '{expression}' evaluates FALSE "
            + "for a pull_request run. That is the half worth keeping — a later push to a PR tests a "
            + "strict successor of what was cancelled, and it is where #2316's ~28% of runner demand "
            + "is saved. Exclude main specifically, not every ref.");
    }

    /// <summary>
    /// 🚨 The evaluator is code, so it gets the same treatment every gate here does: rows that
    /// prove it can return BOTH answers, including the inverted expression that defeated the
    /// original substring check, and the shapes a "tidy-up" would most plausibly introduce.
    /// </summary>
    [Theory]
    // The shipped expression: off for main, on for a PR.
    [InlineData("${{ github.event_name != 'workflow_dispatch' && github.ref != 'refs/heads/main' }}", "push", "refs/heads/main", false)]
    [InlineData("${{ github.event_name != 'workflow_dispatch' && github.ref != 'refs/heads/main' }}", "pull_request", "refs/pull/1/merge", true)]
    // The pre-fix expression, and a bare literal: both cancel on main.
    [InlineData("${{ github.event_name != 'workflow_dispatch' }}", "push", "refs/heads/main", true)]
    [InlineData("true", "push", "refs/heads/main", true)]
    // 🚨 Copilot's counterexample on this PR: mentions refs/heads/main, still cancels there.
    [InlineData("${{ github.ref == 'refs/heads/main' }}", "push", "refs/heads/main", true)]
    // A disjunct is not a conjunct — `||` re-admits main whenever the other side is true.
    [InlineData("${{ github.event_name != 'workflow_dispatch' || github.ref != 'refs/heads/main' }}", "push", "refs/heads/main", true)]
    // Negation and parentheses, the two shapes a rewrite reaches for.
    [InlineData("${{ !(github.ref == 'refs/heads/main') }}", "push", "refs/heads/main", false)]
    [InlineData("${{ !(github.ref == 'refs/heads/main') }}", "pull_request", "refs/pull/1/merge", true)]
    [InlineData("false", "pull_request", "refs/pull/1/merge", false)]
    public void TheEvaluator_IsItselfCorrect(string expression, string eventName, string @ref, bool expected)
        => Assert.Equal(expected, Evaluate(expression, new Dictionary<string, string>
        {
            ["github.event_name"] = eventName,
            ["github.ref"] = @ref,
        }));

    /// <summary>
    /// A recursive-descent evaluator for the slice of the GitHub expression language a
    /// <c>cancel-in-progress</c> value can reasonably use: <c>&amp;&amp;</c>, <c>||</c>, <c>!</c>,
    /// parentheses, <c>==</c>/<c>!=</c>, single-quoted strings, <c>true</c>/<c>false</c>, and
    /// context lookups. Precedence is <c>!</c> &gt; comparison &gt; <c>&amp;&amp;</c> &gt;
    /// <c>||</c>, as in Actions. Truthiness follows Actions: a non-empty string is true.
    ///
    /// <para>Anything outside that subset throws rather than guessing — an unparseable expression
    /// must fail the guard loudly, never silently evaluate to "fine".</para>
    /// </summary>
    private static bool Evaluate(string raw, IReadOnlyDictionary<string, string> context)
    {
        var text = raw.Trim();
        var wrapped = Regex.Match(text, @"^\$\{\{(?<inner>.*)\}\}$", RegexOptions.Singleline);
        if (wrapped.Success)
            text = wrapped.Groups["inner"].Value;
        // A bare scalar may be YAML-quoted (`cancel-in-progress: "true"`); the expression form
        // never is. Strip only a matched pair of double quotes, so a single-quoted GitHub string
        // literal is left for the tokenizer.
        if (text.Length > 1 && text[0] == '"' && text[^1] == '"')
            text = text[1..^1];

        var tokens = Tokenize(text);
        var position = 0;
        var value = ParseOr(tokens, ref position, context);

        if (position != tokens.Count)
            throw new InvalidOperationException(
                $"could not fully parse the cancel-in-progress expression '{raw}' (stopped at token "
                + $"{position} of {tokens.Count}). Extend this evaluator deliberately rather than "
                + "letting the guard pass on an expression it does not understand.");

        return Truthy(value);
    }

    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var i = 0;

        while (i < text.Length)
        {
            var c = text[i];

            if (char.IsWhiteSpace(c)) { i++; continue; }

            if (c == '\'')
            {
                var end = text.IndexOf('\'', i + 1);
                if (end < 0) throw new InvalidOperationException($"unterminated string literal in '{text}'");
                tokens.Add(text[i..(end + 1)]);
                i = end + 1;
                continue;
            }

            if (i + 1 < text.Length && text[i..(i + 2)] is "&&" or "||" or "==" or "!=")
            {
                tokens.Add(text[i..(i + 2)]);
                i += 2;
                continue;
            }

            if (c is '(' or ')' or '!')
            {
                tokens.Add(c.ToString());
                i++;
                continue;
            }

            var start = i;
            while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '.' or '-')) i++;
            if (i == start)
                throw new InvalidOperationException($"unexpected character '{c}' in expression '{text}'");
            tokens.Add(text[start..i]);
        }

        return tokens;
    }

    private static object ParseOr(List<string> tokens, ref int position, IReadOnlyDictionary<string, string> context)
    {
        var left = ParseAnd(tokens, ref position, context);
        while (position < tokens.Count && tokens[position] == "||")
        {
            position++;
            var right = ParseAnd(tokens, ref position, context);
            left = Truthy(left) || Truthy(right);
        }
        return left;
    }

    private static object ParseAnd(List<string> tokens, ref int position, IReadOnlyDictionary<string, string> context)
    {
        var left = ParseComparison(tokens, ref position, context);
        while (position < tokens.Count && tokens[position] == "&&")
        {
            position++;
            var right = ParseComparison(tokens, ref position, context);
            left = Truthy(left) && Truthy(right);
        }
        return left;
    }

    private static object ParseComparison(List<string> tokens, ref int position, IReadOnlyDictionary<string, string> context)
    {
        var left = ParseUnary(tokens, ref position, context);
        if (position < tokens.Count && tokens[position] is "==" or "!=")
        {
            var op = tokens[position++];
            var right = ParseUnary(tokens, ref position, context);
            var equal = string.Equals(Stringify(left), Stringify(right), StringComparison.Ordinal);
            return op == "==" ? equal : !equal;
        }
        return left;
    }

    private static object ParseUnary(List<string> tokens, ref int position, IReadOnlyDictionary<string, string> context)
    {
        if (position < tokens.Count && tokens[position] == "!")
        {
            position++;
            return !Truthy(ParseUnary(tokens, ref position, context));
        }
        return ParsePrimary(tokens, ref position, context);
    }

    private static object ParsePrimary(List<string> tokens, ref int position, IReadOnlyDictionary<string, string> context)
    {
        if (position >= tokens.Count)
            throw new InvalidOperationException("expression ended where a value was expected");

        var token = tokens[position++];

        if (token == "(")
        {
            var inner = ParseOr(tokens, ref position, context);
            if (position >= tokens.Count || tokens[position] != ")")
                throw new InvalidOperationException("missing closing parenthesis");
            position++;
            return inner;
        }

        if (token.StartsWith('\'')) return token.Trim('\'');
        if (token == "true") return true;
        if (token == "false") return false;

        return context.TryGetValue(token, out var value)
            ? value
            : throw new InvalidOperationException(
                $"the expression reads context value '{token}', which this guard does not model. "
                + "Add it to the contexts under test rather than loosening the assertion.");
    }

    private static bool Truthy(object value) => value switch
    {
        bool b => b,
        string s => s.Length > 0,
        _ => throw new InvalidOperationException($"cannot take the truth value of '{value}'"),
    };

    private static string Stringify(object value) => value switch
    {
        bool b => b ? "true" : "false",
        string s => s,
        _ => value.ToString() ?? string.Empty,
    };

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx")))
            dir = dir.Parent;
        return dir?.FullName
            ?? throw new InvalidOperationException(
                "Could not locate the repo root (MeshWeaver.slnx) from " + AppContext.BaseDirectory);
    }
}
