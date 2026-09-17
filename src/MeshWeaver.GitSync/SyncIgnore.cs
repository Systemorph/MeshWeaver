using System.Collections.Immutable;
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

    /// <summary>
    /// 🚨 Node paths this import must neither write nor prune for a reason that is NOT a pattern —
    /// the NodeTypes whose sources are held because no bundle for this instance's framework identity
    /// carries the fingerprint they would produce (MeshWeaver#3845 hole 4,
    /// <see cref="BundleKeyedHold"/>). Exact Space-relative paths and their subtrees.
    ///
    /// <para>They ride HERE rather than as patterns because a node id is not a glob: a path carrying
    /// <c>*</c>, <c>?</c> or <c>[</c> would be compiled into a matcher that holds more than the one
    /// type, and a hold that is wider than its reason is drift of its own. Kept as a set, matched by
    /// equality and prefix, so what is held is exactly what the decision named.</para>
    /// </summary>
    private readonly ImmutableHashSet<string> _heldPaths;

    /// <summary>Builds the matcher from gitignore-style patterns; null → <see cref="Default"/>.</summary>
    public SyncIgnore(IEnumerable<string>? patterns)
        : this(patterns, ImmutableHashSet<string>.Empty)
    {
    }

    private SyncIgnore(IEnumerable<string>? patterns, ImmutableHashSet<string> heldPaths)
    {
        _heldPaths = heldPaths;
        _rules = (patterns ?? Default)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0 && !p.StartsWith('#'))
            .Select(p =>
            {
                var negated = p.StartsWith('!');
                return (ToRegex(negated ? p[1..] : p), negated);
            })
            .ToList();
        _patterns = patterns is null ? null : [.. patterns];
    }

    /// <summary>The patterns this matcher was built from, so a derived matcher keeps them exactly
    /// (null = <see cref="Default"/>, which is NOT the same as an explicit empty list).</summary>
    private readonly ImmutableArray<string>? _patterns;

    /// <summary>The matcher for a Space's sync config — unset config/patterns → <see cref="Default"/>.</summary>
    public static SyncIgnore For(GitHubSyncConfig? config) => new(config?.Ignore);

    /// <summary>
    /// The same rules PLUS the held node paths of <paramref name="heldPaths"/> — the import's
    /// bundle-keyed hold (MeshWeaver#3845 hole 4). The Space's own configured patterns are carried
    /// over verbatim, so an unset list still means <see cref="Default"/> and an explicit empty one
    /// still syncs everything; the held set is additive and cannot be negated by a <c>!</c> rule.
    /// </summary>
    /// <param name="heldPaths">Space-relative node paths to hold, or empty for this instance.</param>
    /// <returns>This instance when nothing is held, otherwise a new matcher.</returns>
    public SyncIgnore WithHeldPaths(IEnumerable<string>? heldPaths)
    {
        var held = heldPaths is null
            ? ImmutableHashSet<string>.Empty
            : heldPaths.Where(p => !string.IsNullOrWhiteSpace(p))
                .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
        return held.IsEmpty && _heldPaths.IsEmpty
            ? this
            : new SyncIgnore(_patterns, _heldPaths.Union(held));
    }

    /// <summary>
    /// True when <paramref name="relativePath"/> ('/'-separated, relative to the Space root /
    /// mirrored subdirectory, no leading slash) is ignored. Last matching rule wins; no matching
    /// rule → not ignored. The empty path (the Space root itself) is never ignored.
    ///
    /// <para>🚨 A HELD path (<see cref="WithHeldPaths"/>) is ignored whatever the rules say,
    /// including a <c>!</c> re-include: a hold exists because this instance has no bytes for that
    /// type's sources, which no configured pattern is expressing an opinion about.</para>
    /// </summary>
    public bool IsIgnored(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath))
            return false;
        if (!_heldPaths.IsEmpty && IsHeld(relativePath))
            return true;
        var ignored = false;
        foreach (var (pattern, negated) in _rules)
            if (pattern.IsMatch(relativePath))
                ignored = !negated;
        return ignored;
    }

    /// <summary>A held path itself, or anything beneath one — node paths do not distinguish files
    /// from directories, so a hold covers its subtree exactly as a pattern match does.</summary>
    private bool IsHeld(string relativePath)
    {
        if (_heldPaths.Contains(relativePath))
            return true;
        foreach (var held in _heldPaths)
            if (relativePath.Length > held.Length + 1
                && relativePath[held.Length] == '/'
                && relativePath.StartsWith(held, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
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
        // 🚨 CASE-INSENSITIVE, unlike .gitignore (issue #1326). These rules are matched against
        // MESH node paths as well as repo file paths, and every other path comparison in the sync
        // pipeline is OrdinalIgnoreCase (ComputePrunableNodes, ChangedNodePaths, IsAtOrUnder,
        // the partition fold). A case-sensitive matcher here means `release/Foo` is exported and
        // — worse, since the prune now consults these rules — `Release/` vs `release/` decides
        // whether a node is DELETED. Path case is not a meaningful distinction in the mesh, so the
        // matcher must not invent one.
        return new Regex(sb.ToString(),
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    }
}
