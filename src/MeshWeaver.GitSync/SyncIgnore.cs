using System.Text;
using System.Text.RegularExpressions;

namespace MeshWeaver.GitSync;

/// <summary>
/// Gitignore-style ignore rules for GitHub sync. The SAME rule set is applied on export (node
/// paths relative to the Space root) and on import (repo file paths relative to the mirrored
/// subdirectory), so an ignored subtree never syncs in either direction. Semantics follow
/// <c>.gitignore</c>:
/// <list type="bullet">
///   <item><c>*</c> matches within a path segment, <c>**</c> across segments, <c>?</c> one character;</item>
///   <item>a pattern containing a <c>/</c> (other than a trailing one) is anchored to the Space
///     root; otherwise it matches at ANY depth;</item>
///   <item>a match ignores the node/file AND everything beneath it (a trailing <c>/</c> is
///     accepted and equivalent — node paths don't distinguish files from directories);</item>
///   <item><c>!pattern</c> re-includes; the LAST matching rule wins;</item>
///   <item>blank lines and <c>#</c> comment lines are skipped.</item>
/// </list>
/// Nothing is hardcoded in the sync pipeline: the rules come from
/// <see cref="GitHubSyncConfig.Ignore"/> on the Space's <c>_GitSync</c> config node, falling back
/// to <see cref="Default"/> when unset. <see cref="Default"/> ignores <c>Release/</c> — the
/// per-NodeType release-request records the compile pipeline appends on every release: machine
/// bookkeeping (~one node per recompile, forever), not content. A Space can override by setting
/// its own list; an explicit EMPTY list syncs everything.
/// </summary>
public sealed class SyncIgnore
{
    /// <summary>The default rule set when a config sets none: compile release-request records
    /// (<c>Release/</c> folders at any depth) don't sync. Immutable — a shared constant, never
    /// written at runtime (the allowed kind of static).</summary>
    public static readonly IReadOnlyList<string> Default = ["Release/"];

    private readonly List<(Regex Pattern, bool Negated)> _rules;

    /// <summary>Builds the matcher from gitignore-style patterns; null → <see cref="Default"/>.</summary>
    public SyncIgnore(IEnumerable<string>? patterns)
        => _rules = (patterns ?? Default)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0 && !p.StartsWith('#'))
            .Select(p =>
            {
                var negated = p.StartsWith('!');
                return (ToRegex(negated ? p[1..] : p), negated);
            })
            .ToList();

    /// <summary>The matcher for a Space's sync config — unset config/patterns → <see cref="Default"/>.</summary>
    public static SyncIgnore For(GitHubSyncConfig? config) => new(config?.Ignore);

    /// <summary>
    /// True when <paramref name="relativePath"/> ('/'-separated, relative to the Space root /
    /// mirrored subdirectory, no leading slash) is ignored. Last matching rule wins; no matching
    /// rule → not ignored. The empty path (the Space root itself) is never ignored.
    /// </summary>
    public bool IsIgnored(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath))
            return false;
        var ignored = false;
        foreach (var (pattern, negated) in _rules)
            if (pattern.IsMatch(relativePath))
                ignored = !negated;
        return ignored;
    }

    // Translates one gitignore-style glob into an anchored regex over relative paths.
    private static Regex ToRegex(string pattern)
    {
        pattern = pattern.TrimEnd('/');
        // A '/' anywhere else anchors the pattern to the root, like gitignore.
        var anchored = pattern.Contains('/');
        pattern = pattern.TrimStart('/');

        var sb = new StringBuilder(anchored ? "^" : @"(^|.*/)");
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '*')
            {
                if (i + 1 < pattern.Length && pattern[i + 1] == '*')
                {
                    sb.Append(".*");
                    i++;
                }
                else
                {
                    sb.Append("[^/]*");
                }
            }
            else if (c == '?')
                sb.Append("[^/]");
            else
                sb.Append(Regex.Escape(c.ToString()));
        }
        // The match ignores the path itself and everything beneath it.
        sb.Append("(/.*)?$");
        return new Regex(sb.ToString(), RegexOptions.Compiled | RegexOptions.CultureInvariant);
    }
}
