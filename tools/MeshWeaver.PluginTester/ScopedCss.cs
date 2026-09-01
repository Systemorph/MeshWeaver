using System.Collections.Immutable;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace MeshWeaver.PluginTester;

/// <summary>
/// Reproduces the SDK's CSS-isolation pipeline for <c>build-project</c>: the <c>b-…</c> scope
/// identifier (<c>ComputeCssScope</c>), the per-file selector rewrite (<c>RewriteCss</c> — what a
/// <c>*.razor.rz.scp.css</c> holds), and the per-project aggregate
/// (<c>wwwroot/&lt;TargetName&gt;.styles.css</c>) the portal's module-asset host links at runtime.
///
/// <para>🚨 <b>The scope value is a CONTRACT, verified against the SDK, not designed here.</b>
/// <see cref="GenerateScope"/> reproduces the SDK's <c>ComputeCssScope.GenerateScope</c> —
/// SHA-256 over the LOWERCASED project-relative path (forward slashes) concatenated with the
/// target name, first nine bytes little-endian to a non-negative integer, ten base-36 digits
/// least-significant first, <c>b-</c> prefix. Pinned by test against three values measured from a
/// real SDK build of MeshWeaver.Blazor (<c>components/codeblock.razor.css</c> → <c>b-h3pg7owarf</c>
/// et al.), so a drifted reimplementation cannot ship: the attribute the generator stamps into the
/// markup and the attribute this rewriter appends to the selectors must be the SAME string, and
/// both must match what an SDK-built neighbour in the same process would have produced.</para>
///
/// <para>🚨 <b>The rewriter covers the corpus, refuses the rest BY NAME.</b> The SDK rewrites CSS
/// with a full parser; this one is an evaluator in the build-project tradition — it handles
/// exactly what the module corpus uses (style rules, selector lists, pseudo-classes and
/// pseudo-elements, <c>::deep</c>, nested-block at-rules like <c>@media</c>/<c>@supports</c>/
/// <c>@container</c>, <c>@keyframes</c> with animation-name rewriting, <c>@font-face</c>,
/// comments) and throws naming the construct for anything else (<c>@import</c>, CSS nesting),
/// because a half-rewritten stylesheet renders as the silent unstyled page this tool exists to
/// prevent (#2221's signature). Fidelity is pinned by corpus-equivalence tests whose expected
/// halves are VERBATIM SDK outputs.</para>
/// </summary>
public static class ScopedCss
{
    /// <summary>
    /// The SDK's scope identifier for one <c>*.razor.css</c> file.
    /// </summary>
    /// <param name="projectRelativePath">Path of the CSS file relative to the project directory.
    /// Separators are normalised to <c>/</c> and the path is lowercased here — callers pass what
    /// <see cref="Path.GetRelativePath(string, string)"/> gave them.</param>
    /// <param name="targetName">The assembly's simple name (<c>$(TargetName)</c>).</param>
    public static string GenerateScope(string projectRelativePath, string targetName)
    {
        var normalized = projectRelativePath.Replace('\\', '/').ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized + targetName));
        // Nine little-endian bytes, absolute value, ten base-36 digits least-significant first —
        // byte-for-byte the SDK's ToBase36, and the part a from-memory guess gets wrong.
        var dividend = BigInteger.Abs(new BigInteger(hash.AsSpan(0, 9), isUnsigned: false));
        Span<char> digits = stackalloc char[10];
        for (var i = 0; i < 10; i++)
        {
            dividend = BigInteger.DivRem(dividend, 36, out var remainder);
            digits[i] = "0123456789abcdefghijklmnopqrstuvwxyz"[(int)remainder];
        }
        return "b-" + new string(digits);
    }

    /// <summary>
    /// Rewrites one scoped stylesheet: every selector gains <c>[scope]</c>, <c>::deep</c> becomes
    /// the scope boundary, <c>@keyframes</c> names (and the <c>animation</c>/<c>animation-name</c>
    /// declarations that reference them) gain a <c>-scope</c> suffix. Comments and formatting are
    /// preserved verbatim — the corpus-equivalence tests compare against SDK output byte-for-byte.
    /// </summary>
    public static string Rewrite(string css, string scope)
    {
        ArgumentNullException.ThrowIfNull(css);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        var keyframeNames = CollectKeyframeNames(css);
        var output = new StringBuilder(css.Length + 512);
        RewriteBlockContents(new Cursor(css), output, scope, keyframeNames, topLevel: true);
        return output.ToString();
    }

    /// <summary>
    /// Writes the per-project aggregate the portal links: each rewritten file in input order,
    /// separated by a blank line — the shape of the SDK's bundle minus the dependency
    /// <c>@import</c> header, which the module-asset host strips and re-derives anyway
    /// (MeshModuleStaticAssetExtensions), so it is deliberately not reproduced.
    /// </summary>
    /// <param name="files">Pairs of (project-relative path, rewritten content), in item order.</param>
    public static string Aggregate(IEnumerable<(string RelativePath, string Rewritten)> files)
    {
        var builder = new StringBuilder();
        foreach (var (relativePath, rewritten) in files)
        {
            if (builder.Length > 0)
                builder.Append('\n');
            builder.Append("/* ").Append(relativePath.Replace('\\', '/')).Append(".rz.scp.css */\n");
            builder.Append(rewritten);
            if (rewritten.Length > 0 && rewritten[^1] != '\n')
                builder.Append('\n');
        }
        return builder.ToString();
    }

    // ── the tokenizer ────────────────────────────────────────────────────────────────────────────

    private sealed class Cursor(string text)
    {
        public string Text { get; } = text;
        public int Position { get; set; }
        public bool AtEnd => Position >= Text.Length;
        public char Current => Text[Position];
        public bool StartsWith(string s) =>
            string.CompareOrdinal(Text, Position, s, 0, s.Length) == 0;
    }

    private static ImmutableHashSet<string> CollectKeyframeNames(string css)
    {
        var names = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        var cursor = new Cursor(css);
        while (!cursor.AtEnd)
        {
            if (cursor.StartsWith("/*")) { SkipComment(cursor); continue; }
            if (cursor.StartsWith("@keyframes"))
            {
                cursor.Position += "@keyframes".Length;
                SkipWhitespaceAndComments(cursor);
                var name = ReadIdentifier(cursor);
                if (name.Length > 0)
                    names.Add(name);
                continue;
            }
            cursor.Position++;
        }
        return names.ToImmutable();
    }

    private static void RewriteBlockContents(
        Cursor cursor, StringBuilder output, string scope,
        ImmutableHashSet<string> keyframeNames, bool topLevel)
    {
        while (!cursor.AtEnd)
        {
            if (!topLevel && cursor.Current == '}')
                return;
            if (cursor.StartsWith("/*"))
            {
                CopyComment(cursor, output);
                continue;
            }
            if (char.IsWhiteSpace(cursor.Current))
            {
                output.Append(cursor.Current);
                cursor.Position++;
                continue;
            }
            if (cursor.Current == '@')
            {
                RewriteAtRule(cursor, output, scope, keyframeNames);
                continue;
            }
            RewriteStyleRule(cursor, output, scope, keyframeNames);
        }
    }

    private static void RewriteAtRule(
        Cursor cursor, StringBuilder output, string scope, ImmutableHashSet<string> keyframeNames)
    {
        var nameStart = cursor.Position + 1;
        var nameEnd = nameStart;
        while (nameEnd < cursor.Text.Length
               && (char.IsAsciiLetterOrDigit(cursor.Text[nameEnd]) || cursor.Text[nameEnd] == '-'))
            nameEnd++;
        var name = cursor.Text[nameStart..nameEnd];

        switch (name.ToLowerInvariant())
        {
            case "media" or "supports" or "container" or "layer" or "scope":
                // Prelude verbatim, contents recursively — a selector inside @media scopes exactly
                // like one outside it.
                output.Append(cursor.Text, cursor.Position, nameEnd - cursor.Position);
                cursor.Position = nameEnd;
                CopyUntil(cursor, output, '{');
                if (cursor.AtEnd)
                    return;
                output.Append('{');
                cursor.Position++;
                RewriteBlockContents(cursor, output, scope, keyframeNames, topLevel: false);
                if (!cursor.AtEnd) { output.Append('}'); cursor.Position++; }
                return;
            case "keyframes":
                // The name gains the scope suffix; the frame blocks are percentages and
                // declarations, copied verbatim — nothing inside a keyframe is a selector.
                output.Append(cursor.Text, cursor.Position, nameEnd - cursor.Position);
                cursor.Position = nameEnd;
                var wsStart = cursor.Position;
                SkipWhitespaceAndComments(cursor);
                output.Append(cursor.Text, wsStart, cursor.Position - wsStart);
                var keyframeName = ReadIdentifier(cursor);
                output.Append(keyframeName).Append('-').Append(scope);
                CopyUntil(cursor, output, '{');
                CopyBalancedBlock(cursor, output);
                return;
            case "font-face":
                output.Append(cursor.Text, cursor.Position, nameEnd - cursor.Position);
                cursor.Position = nameEnd;
                CopyUntil(cursor, output, '{');
                CopyBalancedBlock(cursor, output);
                return;
            default:
                // @import in a SCOPED sheet, @charset, CSS nesting — outside the corpus and outside
                // what this rewriter can prove it reproduces. A named refusal beats a stylesheet
                // that half-applies.
                throw new InvalidOperationException(
                    $"@{name} in a scoped stylesheet — this rewriter reproduces the SDK's CSS "
                    + "isolation for the constructs the module corpus uses (@media/@supports/"
                    + "@container/@layer/@scope, @keyframes, @font-face, ::deep); it does not "
                    + $"reproduce @{name}, and a half-rewritten stylesheet renders as an unstyled "
                    + "page with nothing in the log. Restructure the stylesheet, or extend "
                    + "ScopedCss with SDK-verified expected output first.");
        }
    }

    private static void RewriteStyleRule(
        Cursor cursor, StringBuilder output, string scope, ImmutableHashSet<string> keyframeNames)
    {
        // The prelude: everything up to '{' at depth zero is the selector list.
        var start = cursor.Position;
        while (!cursor.AtEnd && cursor.Current != '{')
        {
            if (cursor.StartsWith("/*")) SkipComment(cursor);
            else if (cursor.Current is '(' or '[') SkipBalanced(cursor);
            else cursor.Position++;
        }
        var prelude = cursor.Text[start..cursor.Position];
        output.Append(RewriteSelectorList(prelude, scope));
        if (cursor.AtEnd)
            return;
        output.Append('{');
        cursor.Position++;
        // The declaration block: verbatim except animation name references. Depth tracking keeps a
        // stray nested brace from ending the rule early; rewriting only applies at this level.
        var body = new StringBuilder();
        var depth = 0;
        while (!cursor.AtEnd)
        {
            if (cursor.StartsWith("/*")) { CopyComment(cursor, body); continue; }
            var c = cursor.Current;
            if (c == '{') depth++;
            else if (c == '}')
            {
                if (depth == 0) break;
                depth--;
            }
            body.Append(c);
            cursor.Position++;
        }
        output.Append(RewriteAnimationNames(body.ToString(), scope, keyframeNames));
        if (!cursor.AtEnd) { output.Append('}'); cursor.Position++; }
    }

    /// <summary>
    /// Scopes every complex selector in a comma-separated list, preserving the surrounding
    /// whitespace exactly (the equivalence tests compare against SDK output verbatim).
    /// </summary>
    internal static string RewriteSelectorList(string prelude, string scope)
    {
        var output = new StringBuilder(prelude.Length + 32);
        var start = 0;
        var depth = 0;
        for (var i = 0; i < prelude.Length; i++)
        {
            var c = prelude[i];
            if (c is '(' or '[') depth++;
            else if (c is ')' or ']') depth--;
            else if (c == ',' && depth == 0)
            {
                output.Append(RewriteComplexSelector(prelude[start..i], scope)).Append(',');
                start = i + 1;
            }
        }
        output.Append(RewriteComplexSelector(prelude[start..], scope));
        return output.ToString();
    }

    private static string RewriteComplexSelector(string selector, string scope)
    {
        if (selector.Trim().Length == 0)
            return selector;

        var deep = selector.IndexOf("::deep", StringComparison.OrdinalIgnoreCase);
        if (deep >= 0)
        {
            // `::deep` marks the scope boundary: the part before it belongs to this component (and
            // is scoped), the part after it is intentionally unscoped. Leading `::deep X` becomes
            // `[scope] X`; `.a ::deep .b` becomes `.a[scope] .b` — measured against DialogView's
            // SDK output.
            var before = selector[..deep];
            var after = selector[(deep + "::deep".Length)..];
            if (before.Trim().Length == 0)
                return before + "[" + scope + "]" + after;
            return ScopeLastCompound(before) + after;
        }
        return ScopeLastCompound(selector);

        string ScopeLastCompound(string part)
        {
            // Insertion point: end of the last compound — after trailing whitespace is trimmed
            // logically (kept textually), before any pseudo-ELEMENT of that compound. Pseudo-classes
            // keep the scope AFTER them (`.dot:nth-child(1)[scope]`, measured); pseudo-elements
            // take it BEFORE (`.foo[scope]::before`), because an attribute cannot follow one.
            var end = part.Length;
            while (end > 0 && char.IsWhiteSpace(part[end - 1]))
                end--;
            if (end == 0)
                return part;
            // Find where the last compound starts: the last top-level combinator/whitespace.
            var compoundStart = 0;
            var depth = 0;
            for (var i = 0; i < end; i++)
            {
                var c = part[i];
                if (c is '(' or '[') depth++;
                else if (c is ')' or ']') depth--;
                else if (depth == 0 && (char.IsWhiteSpace(c) || c is '>' or '+' or '~'))
                    compoundStart = i + 1;
            }
            // Within the compound, a pseudo-element starts at the first `::` (or a legacy
            // single-colon `:before`/`:after`).
            var insert = end;
            for (var i = compoundStart; i < end - 1; i++)
            {
                if (part[i] != ':') continue;
                var legacy = part.AsSpan(i + 1).StartsWith("before", StringComparison.OrdinalIgnoreCase)
                             || part.AsSpan(i + 1).StartsWith("after", StringComparison.OrdinalIgnoreCase);
                if (part[i + 1] == ':' || legacy)
                {
                    insert = i;
                    break;
                }
            }
            return part[..insert] + "[" + scope + "]" + part[insert..];
        }
    }

    /// <summary>
    /// Rewrites <c>animation:</c> / <c>animation-name:</c> declarations so identifiers naming one
    /// of this file's <c>@keyframes</c> follow the rename. Only known names move — timing keywords
    /// (<c>infinite</c>, <c>ease-in-out</c>…) never match the collected set.
    /// </summary>
    internal static string RewriteAnimationNames(
        string block, string scope, ImmutableHashSet<string> keyframeNames)
    {
        if (keyframeNames.IsEmpty)
            return block;
        var output = new StringBuilder(block.Length + 64);
        var i = 0;
        while (i < block.Length)
        {
            var c = block[i];
            if (char.IsAsciiLetter(c) || c is '_' or '-')
            {
                var start = i;
                while (i < block.Length
                       && (char.IsAsciiLetterOrDigit(block[i]) || block[i] is '_' or '-'))
                    i++;
                var word = block[start..i];
                output.Append(word);
                if (keyframeNames.Contains(word))
                    output.Append('-').Append(scope);
                continue;
            }
            output.Append(c);
            i++;
        }
        return output.ToString();
    }

    // ── low-level copying ────────────────────────────────────────────────────────────────────────

    private static void SkipComment(Cursor cursor)
    {
        var end = cursor.Text.IndexOf("*/", cursor.Position + 2, StringComparison.Ordinal);
        cursor.Position = end < 0 ? cursor.Text.Length : end + 2;
    }

    private static void CopyComment(Cursor cursor, StringBuilder output)
    {
        var start = cursor.Position;
        SkipComment(cursor);
        output.Append(cursor.Text, start, cursor.Position - start);
    }

    private static void SkipWhitespaceAndComments(Cursor cursor)
    {
        while (!cursor.AtEnd)
        {
            if (char.IsWhiteSpace(cursor.Current)) cursor.Position++;
            else if (cursor.StartsWith("/*")) SkipComment(cursor);
            else return;
        }
    }

    private static string ReadIdentifier(Cursor cursor)
    {
        var start = cursor.Position;
        while (!cursor.AtEnd
               && (char.IsAsciiLetterOrDigit(cursor.Current) || cursor.Current is '_' or '-'))
            cursor.Position++;
        return cursor.Text[start..cursor.Position];
    }

    private static void CopyUntil(Cursor cursor, StringBuilder output, char stop)
    {
        while (!cursor.AtEnd && cursor.Current != stop)
        {
            output.Append(cursor.Current);
            cursor.Position++;
        }
    }

    private static void SkipBalanced(Cursor cursor)
    {
        var open = cursor.Current;
        var close = open == '(' ? ')' : ']';
        var depth = 0;
        while (!cursor.AtEnd)
        {
            if (cursor.Current == open) depth++;
            else if (cursor.Current == close && --depth == 0) { cursor.Position++; return; }
            cursor.Position++;
        }
    }

    private static void CopyBalancedBlock(Cursor cursor, StringBuilder output)
    {
        if (cursor.AtEnd || cursor.Current != '{')
            return;
        var depth = 0;
        while (!cursor.AtEnd)
        {
            var c = cursor.Current;
            if (c == '{') depth++;
            else if (c == '}' && --depth == 0)
            {
                output.Append('}');
                cursor.Position++;
                return;
            }
            output.Append(c);
            cursor.Position++;
        }
    }
}
