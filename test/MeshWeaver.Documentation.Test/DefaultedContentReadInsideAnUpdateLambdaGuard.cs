using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 <b>Zero-tolerance ratchet (#3623): an untyped <c>Update</c> lambda may not turn a FAILED READ
/// into a WRITTEN DEFAULT.</b>
///
/// <para><b>The defect class.</b> A read of <c>MeshNode.Content</c> must stay bad-data tolerant —
/// <c>ContentAs&lt;T&gt;</c> / <c>As&lt;T&gt;</c> answer <c>null</c> rather than throw, because a
/// settings tab that throws is worse than one showing a fail-closed default. <b>That same tolerance
/// destroys data on a WRITE.</b> Inside <c>stream.Update(node =&gt; …)</c>,
/// <c>node.ContentAs&lt;T&gt;(opts) ?? new T()</c> makes <i>"there is nothing here"</i> and <i>"I
/// could not read what is here"</i> the same answer, and the write then persists a default-valued
/// record over every field the caller never touched.</para>
///
/// <para><b>All three unreadable shapes happen in a running mesh</b>, and none of them throws:
/// untyped JSON whose <c>$type</c> will not resolve; the as-written <c>JsonObject</c> DOM before the
/// materialisation pipeline re-types it; and a same-named record from another collectible assembly,
/// which every NodeType recompile mints. From outside, all three look like "the content was empty".</para>
///
/// <para><b>The fix is a type argument, never care.</b>
/// <c>Update&lt;TContent&gt;((node, content) =&gt; …)</c> — whose <c>null</c> means ABSENT and only
/// absent, and which faults with a <c>MeshNodeStreamException</c> (path, runtime type, JSON
/// excerpt, <b>write not applied</b>) when the content is present and unreadable. See
/// <c>Doc/Architecture/CqrsAndContentAccess</c> → "Writes".</para>
///
/// <para><b>Why a guard rather than care.</b> The wrong spelling is the one that reads naturally,
/// it compiles, it passes every test that does not seed unreadable content, and the loss is
/// invisible at the call site AND at runtime: the write completes, nothing is logged, and the
/// damage surfaces later as a field that "went missing". Two production incidents came from exactly
/// this — <c>Admin/UpdatePolicy</c> losing its own policy ahead of a portal rolling itself onto a
/// withdrawn version line (#3542), and <c>ThreadInput.AppendUserInput</c> resetting <c>Status</c> to
/// <c>Idle</c> (the CheckInbox flake).</para>
///
/// <para>🚨 <b>There is no allow file, deliberately.</b> Every occurrence has a mechanical fix that
/// preserves create-on-absent behaviour exactly, so an exemption would only ever mean "this one may
/// keep destroying records".</para>
/// </summary>
public class DefaultedContentReadInsideAnUpdateLambdaGuard
{
    /// <summary>Production trees only. A TEST may legitimately build a defaulted record while
    /// constructing a fixture; only shipped write paths are the subject.</summary>
    private static readonly string[] ScannedRoots = ["src", "memex"];

    /// <summary>
    /// An UNTYPED update lambda: <c>.Update(node =&gt;</c>. The typed overloads are
    /// <c>.Update&lt;T&gt;(…)</c> and do not match, which is the whole point — the type argument is
    /// what removes the guess.
    /// </summary>
    private static readonly Regex UpdateLambda = new(
        @"\.Update\(\s*[A-Za-z_]\w*\s*=>", RegexOptions.Compiled);

    /// <summary>
    /// A defaulting content read: <c>ContentAs&lt;T&gt;(…) ?? new</c> or <c>… as T ?? new</c>, with
    /// any whitespace or newline between the read and the fallback (they are almost always on
    /// separate lines). One level of nested parentheses in the argument list is tolerated.
    /// </summary>
    private static readonly Regex DefaultedRead = new(
        @"(?:ContentAs|As)\s*<\s*\w+\s*>\s*\((?:[^()]|\([^()]*\))*\)\s*\?\?\s*new\b"
        + @"|\.Content\s+as\s+\w+\s*\?\?\s*new\b",
        RegexOptions.Compiled);

    /// <summary>
    /// How far after the lambda's <c>=&gt;</c> a read still counts as being inside it. Generous
    /// rather than exact: a brace-matched parse would be more precise and much easier to get subtly
    /// wrong, and the cost of overreach here is a false POSITIVE — which is loud, reviewable, and
    /// the safe direction for a ratchet that must not silently stop enforcing.
    /// </summary>
    private const int LambdaWindow = 1600;

    [Fact]
    public void NoUntypedUpdateLambdaTurnsAFailedReadIntoAWrittenDefault()
    {
        var root = SourceScan.FindRepoRoot();
        var offenders = new List<string>();
        var files = 0;
        var lambdas = 0;

        foreach (var file in SourceScan.SourceFiles(root, ScannedRoots))
        {
            files++;
            // 🚨 Masked, not raw: this very rule is DESCRIBED in comments across the tree (including
            // in the files it was applied to, which quote the old shape to say why it went), and a
            // raw scan would flag the explanations as offences.
            var text = SourceScan.MaskCommentsAndStrings(File.ReadAllText(file));

            foreach (Match lambda in UpdateLambda.Matches(text))
            {
                lambdas++;
                var length = Math.Min(LambdaWindow, text.Length - lambda.Index);
                foreach (Match read in DefaultedRead.Matches(text.Substring(lambda.Index, length)))
                    offenders.Add(
                        $"{SourceScan.Relative(root, file)}:{LineOf(text, lambda.Index)} — "
                        + Collapse(read.Value));
            }
        }

        // 🚨 The DENOMINATOR. A zero here has two causes — "nothing offends" and "nothing was
        // examined" — and only one of them is good news. SourceFiles already refuses an empty file
        // set (#2844); this adds the half it cannot see: a regex that stopped matching the shape it
        // is aimed at, e.g. after a rename of Update or a reformat of the lambda syntax.
        Assert.True(
            lambdas >= 40,
            $"only {lambdas} untyped Update lambda(s) found across {files} file(s) — the scan is "
            + "not looking at what it thinks it is. There were 72 on 2026-09-07, and they do not "
            + "vanish in a batch: a count this low means UpdateLambda stopped matching (a rename, a "
            + "reformat, a moved tree), so this guard is reporting green having examined nothing.");

        Assert.True(
            offenders.Count == 0,
            "an untyped Update lambda reads Content through a DEFAULTING parse. `?? new T()` there "
            + "makes 'there is nothing here' and 'I could not read what is here' the same answer, "
            + "and the write persists a default-valued record over every field the caller never "
            + "touched — silently: the write completes, nothing is logged, and the loss surfaces "
            + "later as a field that went missing (#3542, #3623).\n\n"
            + "FIX: `stream.Update<TContent>((node, content) => node with { Content = "
            + "(content ?? new TContent()) with { … } })`. Its `null` means ABSENT and only absent, "
            + "so create-on-absent is preserved exactly, and PRESENT-but-unreadable content faults "
            + "with a MeshNodeStreamException naming the path, the runtime type and a JSON excerpt "
            + "— with the write NOT applied. Doc/Architecture/CqrsAndContentAccess -> Writes.\n\n"
            + "There is no allow file: the fix is mechanical and behaviour-preserving, so an "
            + "exemption could only ever mean 'this one may keep destroying records'.\n\n"
            + string.Join("\n", offenders.Select(o => "  " + o)));
    }

    private static int LineOf(string text, int index) =>
        text.Take(index).Count(c => c == '\n') + 1;

    private static string Collapse(string value) =>
        Regex.Replace(value, @"\s+", " ").Trim();
}
