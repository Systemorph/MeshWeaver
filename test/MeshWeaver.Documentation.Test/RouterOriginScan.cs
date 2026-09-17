using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// The shared matcher the two router-origin ratchets read <c>src/</c> with.
///
/// <para>🚨 <b>ONE matcher, deliberately.</b> <see cref="RouterAsNodeOperationOriginRatchetGuard"/>
/// asks "is a LIFECYCLE message posted off the router?" and
/// <see cref="RouterAsRouterCapableReceiverRatchetGuard"/> asks "is ANY targeted work posted off a
/// hub this file has already declared router-capable?" — two different denominators, but exactly the
/// same question about a call: what is its receiver, what does it target, and is the receiver one of
/// the seams. A second implementation of that would be a second set of evasion holes to find: the
/// spellings below were not guessed, they are the ones a review round on
/// <see href="https://github.com/Systemorph/MeshWeaver/pull/4477">#4477</see> demonstrated could slip
/// a violating post past a matcher that read the receiver as preceding TEXT rather than as an
/// expression.</para>
/// </summary>
internal static class RouterOriginScan
{
    /// <summary>
    /// The request/response and fire-and-forget entry points, tolerant of an explicit type argument
    /// and of the line break C# style puts between the receiver and the call.
    ///
    /// <para>Group 1 is set only for <c>Post</c>, and group 2 carries the explicit type argument,
    /// because the two entry points mean OPPOSITE things by it: <c>Post&lt;TMessage&gt;</c> names the
    /// message, whereas <c>Observe&lt;TResponse&gt;</c> names the RESPONSE.</para>
    /// </summary>
    internal static readonly Regex CallMarker =
        new(@"\.\s*(?:(Post)|Observe)\s*(?:<([^<>()]*)>\s*)?\(", RegexOptions.Compiled);

    /// <summary>A call to ANY of the three seams. All count: a site that hops for reads, for node
    /// lifecycle or for a stream SUBSCRIPTION is off the router either way — each returns the hub
    /// unchanged unless its address type is the mesh type, so writing one is the author's statement
    /// that the receiver can be the router (#4614 added the third).</summary>
    internal static readonly Regex SeamCall =
        new(@"(?:NodeOperationIssuingHub|ReadIssuingHub|StreamSubscribingHub)\s*\(\s*\)", RegexOptions.Compiled);

    /// <summary>
    /// A seam call, matched so that the RECEIVER it is called on can be read off the text before it.
    /// Writing this call is the author's own statement, in production code, that the receiver can be
    /// the router, so it is the whole basis of the receiver-derived denominator.
    ///
    /// <para>🚨 It deliberately captures NOTHING. The receiver is taken by
    /// <see cref="ReceiverOfSeamCall"/>, which runs the SAME <see cref="Receiver"/> walker the post
    /// scan uses — because the two have to agree on what "the same receiver" is. A regex that
    /// grabbed the identifier immediately before the seam would read
    /// <c>this.hub.NodeOperationIssuingHub()</c> as declaring <c>hub</c> while <c>Receiver</c> reads
    /// a sibling <c>this.hub.Post(…)</c> as <c>this.hub</c>: the two spellings would not match, the
    /// sibling would fall out of the denominator as an unrelated receiver, and a SYNTAX-ONLY
    /// qualification would evade the ratchet. That is the same class of hole #4477's review round
    /// closed for the sibling guard, and it was found the same way here (Copilot on #4487).</para>
    /// </summary>
    internal static readonly Regex SeamCallMarker =
        new(@"\.\s*(?:NodeOperationIssuingHub|ReadIssuingHub|StreamSubscribingHub)\s*\(\s*\)", RegexOptions.Compiled);

    /// <summary>A name bound to a seam call — <c>var issuingHub = hub.NodeOperationIssuingHub();</c>
    /// and the lazily-cached property spelling both land here.</summary>
    /// <remarks>
    /// The gap excludes parentheses as well as statement punctuation. Without that, a default
    /// parameter value binds the alias to the wrong name.
    /// </remarks>
    internal static readonly Regex SeamAlias =
        new(@"\b([A-Za-z_][A-Za-z0-9_]*)\s*(?:=>|\?\?=|=)\s*[^;{}()]*?"
            + @"(?:NodeOperationIssuingHub|ReadIssuingHub|StreamSubscribingHub)\s*\(\s*\)", RegexOptions.Compiled);

    internal static readonly Regex BareIdentifier =
        new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    /// <summary>The <c>o.WithTarget(<i>expr</i>)</c> inside a post's options lambda.</summary>
    internal static readonly Regex TargetOption =
        new(@"\bWithTarget\s*\(", RegexOptions.Compiled);

    /// <summary>A file a concurrent build is writing is not evidence.</summary>
    internal static string ReadOrEmpty(string path)
    {
        try { return File.ReadAllText(path); }
        catch (IOException) { return string.Empty; }
    }

    /// <summary>Every name this file binds to a seam call.</summary>
    internal static HashSet<string> SeamAliasesIn(string code) =>
        SeamAlias.Matches(code).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Every receiver a seam is CALLED ON in <paramref name="code"/> (already masked), read with the
    /// same <see cref="Receiver"/> walker the post scan uses so the two spellings compare equal —
    /// see <see cref="SeamCallMarker"/> for why that matters.
    /// </summary>
    internal static IEnumerable<string> ReceiverOfSeamCall(string code) =>
        SeamCallMarker.Matches(code)
            .Select(m => Receiver(code, m.Index))
            .Where(r => r.Length > 0);

    /// <summary>
    /// Whether <paramref name="receiver"/> is off the router: it either IS a seam call, or names a
    /// hub bound to one — the bare identifier (<c>issuingHub</c>) or the last link of a qualified
    /// one (<c>this.issuingHub</c>). Qualified links carrying an argument list are left to
    /// <see cref="SeamCall"/>.
    /// </summary>
    internal static bool IsOffRouter(string receiver, IReadOnlySet<string> aliases)
    {
        if (SeamCall.IsMatch(receiver)) return true;
        if (aliases.Contains(receiver)) return true;
        var lastDot = receiver.LastIndexOf('.');
        if (lastDot < 0) return false;
        var tail = receiver[(lastDot + 1)..];
        return !tail.Contains('(') && aliases.Contains(tail);
    }

    /// <summary>
    /// The targets named in the post's options lambda, whitespace collapsed. Empty when the post
    /// names none.
    /// </summary>
    internal static IReadOnlyList<string> TargetsOf(string code, int openParen)
    {
        var arguments = ArgumentList(code, openParen);
        return TargetOption.Matches(arguments)
            .Select(m => Collapse(SourceScan.FirstArgument(arguments, m.Index + m.Length - 1)))
            .ToList();
    }

    /// <summary>
    /// Whether the post names no target at all, or names only the RECEIVER's own address — the two
    /// spellings of "this hub is telling itself", where the issuing seam has nothing to move and
    /// adopting it would change which hub the message reaches.
    /// </summary>
    internal static bool IsSelfDirected(string code, int openParen, string receiver)
    {
        var targets = TargetsOf(code, openParen);
        return targets.Count == 0
               || targets.All(t => string.Equals(t, Collapse(receiver) + ".Address", StringComparison.Ordinal));
    }

    /// <summary>The whole argument list of the call whose open paren is at <paramref name="openParen"/>.</summary>
    internal static string ArgumentList(string code, int openParen)
    {
        var depth = 0;
        for (var i = openParen; i < code.Length; i++)
        {
            if (code[i] is '(' or '[' or '{') depth++;
            else if (code[i] is ')' or ']' or '}' && --depth == 0) return code[(openParen + 1)..i];
        }

        return code[(openParen + 1)..];
    }

    internal static string Collapse(string expression) =>
        string.Concat(expression.Where(c => !char.IsWhiteSpace(c)));

    internal static int LineOf(string code, int index) =>
        code.AsSpan(0, index).Count('\n') + 1;

    /// <summary>
    /// The primary expression immediately left of the <c>.</c> at <paramref name="dot"/> — an
    /// identifier, or a dotted chain whose links may carry argument lists
    /// (<c>hub.NodeOperationIssuingHub()</c>). Whitespace is collapsed so a receiver wrapped across
    /// lines compares equal to one that is not.
    ///
    /// <para>Walking the expression rather than taking the preceding text is what keeps the verdict
    /// honest: a window back to the previous <c>;</c> picks up the tail of whatever lambda came
    /// before, which on the copy helper's ternary read as a receiver of
    /// <c>"…NodeCopyDisposition.Updated); }) : hub"</c>.</para>
    /// </summary>
    internal static string Receiver(string code, int dot)
    {
        var i = dot;
        while (true)
        {
            i = SkipWhitespaceBack(code, i);
            if (i > 0 && code[i - 1] is ')' or ']')
            {
                i = SkipGroupBack(code, i);
                i = SkipWhitespaceBack(code, i);
            }

            if (i > 0 && IsIdentifierChar(code[i - 1]))
            {
                while (i > 0 && IsIdentifierChar(code[i - 1])) i--;
            }
            else
            {
                break;
            }

            var beforeName = SkipWhitespaceBack(code, i);
            // A single '.' continues the chain; '..' is a range and is not part of one.
            if (beforeName > 0 && code[beforeName - 1] == '.'
                               && !(beforeName > 1 && code[beforeName - 2] == '.'))
            {
                i = beforeName - 1;
                continue;
            }

            break;
        }

        return string.Join(' ', code[i..dot].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static int SkipWhitespaceBack(string code, int i)
    {
        while (i > 0 && char.IsWhiteSpace(code[i - 1])) i--;
        return i;
    }

    /// <summary>Index of the opener matching the closer at <c>code[i - 1]</c>.</summary>
    private static int SkipGroupBack(string code, int i)
    {
        var close = code[i - 1];
        var open = close == ')' ? '(' : '[';
        var depth = 0;
        for (var j = i; j > 0; j--)
        {
            if (code[j - 1] == close) depth++;
            else if (code[j - 1] == open && --depth == 0) return j - 1;
        }

        return 0;
    }
}
