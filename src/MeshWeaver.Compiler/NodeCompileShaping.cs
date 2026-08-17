using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.RegularExpressions;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Compiler;

/// <summary>
/// The pure source-shaping half of a dynamic NodeType compile — everything between "the mesh
/// answered these Code nodes" and "this is the text handed to Roslyn". Extracted from
/// <c>MeshNodeCompilationService</c> (#1707) so that every rule that shapes the GENERATED INPUT
/// of a compile lives inside the full-MVID identity boundary: the include pattern and rebasing,
/// the query-result fold, the snapshot race semantics, the executable filter, the dedup, and the
/// join order. The mesh-actor half (which hub reads a node, under which identity, with which
/// timeout) stays in MeshWeaver.Graph and is threaded in as delegates.
/// </summary>
public static class NodeCompileShaping
{
    /// <summary>
    /// Regex matching @@path references in code files. The capture must LOOK LIKE A NODE PATH —
    /// it starts with a word character and continues with path characters only — because this
    /// pattern runs over RAW C# SOURCE, where <c>@@</c> also appears in prose: XML doc comments
    /// citing the markdown embed idiom (<c>@@("area/Search")</c>) and string literals in tests
    /// asserting exactly that idiom.
    ///
    /// <para>🚨 The permissive predecessor (<c>@@([^\s#\]]+)</c>, shared with the AI
    /// InlineReferenceResolver, which reads PROSE where that is correct) scraped those fragments
    /// as include paths — <c>("area/CoverCta")&lt;/c&gt;</c>, <c>("Install/area/CoverCta")"),</c> —
    /// and each garbage match cost a SERIAL 15s GetMeshNode timeout on the resolving hub. On memex
    /// 2026-07-29 that stall starved the Store root's activation reads (its subtree holds ~44 Code
    /// nodes): SubscribeRequest hit its 60s ceiling and the page died with "activation faulted".
    /// A scanner over source code must reject anything a node path cannot begin with — quotes,
    /// parentheses, XML markup.</para>
    /// </summary>
    internal static readonly Regex CodeIncludePattern = new(@"@@([\w][\w\-./]*)", RegexOptions.Compiled);

    /// <summary>
    /// Rebases a mount-relative <c>@@</c> include path onto the prefix the INCLUDING node lives
    /// under, so the same content resolves from whichever mount point it is served.
    ///
    /// <para>🚨 Why this exists: include paths are authored mount-relative. A sample tree that sits
    /// at the mesh root in the Monolith (<c>FutuRe/GroupAnalysis/Source/…</c>, which is what
    /// <c>FutuReAnalysisTest</c> exercises) is served from a PREFIX in a statically-imported
    /// partition (<c>MeshWeaver/samples/Graph/Data/FutuRe/…</c>). Resolving the authored path
    /// verbatim there finds nothing, and an unresolved include is left VERBATIM in the source —
    /// so Roslyn parses the <c>@@</c> line itself and reports CS9008 / CS8803 / CS0103 on symbol
    /// names that are really path segments. On memex-cloud 2026-08-12 that parked 15 NodeTypes
    /// (FutuRe, Cession, SocialMedia, Northwind/AnalyticsCatalog) and cost a serial
    /// 15s read per unresolved include on the ACTIVATION path — the "waiting for code" stall.</para>
    ///
    /// <para>The anchor is the DEEPEST occurrence of the include's first segment in the including
    /// node's path — the most local reading. An already-absolute include anchors at index 0 and is
    /// returned unchanged, so a root mount keeps behaving exactly as before.</para>
    /// </summary>
    internal static string AnchorIncludePath(string includePath, string? anchorPath)
    {
        if (string.IsNullOrEmpty(anchorPath) || string.IsNullOrEmpty(includePath))
            return includePath;

        var slash = includePath.IndexOf('/');
        var first = slash < 0 ? includePath : includePath[..slash];
        var segments = anchorPath.Split('/');
        for (var i = segments.Length - 1; i > 0; i--)
        {
            if (string.Equals(segments[i], first, StringComparison.Ordinal))
                return string.Join('/', segments, 0, i) + '/' + includePath;
        }
        return includePath;
    }

    /// <summary>
    /// Resolves every <c>@@</c> include in <paramref name="code"/> recursively, left-to-right,
    /// substituting the included node's code (or leaving the directive VERBATIM when the read
    /// finds nothing — the caller's compiler then reports on the <c>@@</c> line itself).
    /// The mesh read is the caller's business: <paramref name="readInclude"/> receives the
    /// ANCHORED path plus the authored path as fallback (null when identical) and reports the
    /// node together with the path that actually produced it, so nested includes anchor there —
    /// MeshWeaver.Graph supplies the System-impersonated, bounded read.
    /// </summary>
    internal static IObservable<string> ResolveCodeIncludes(
        string code,
        HashSet<string> resolved,
        string? anchorPath,
        Func<string, string?, IObservable<(MeshNode? Node, string Path)>> readInclude,
        ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(code) || !code.Contains("@@"))
            return Observable.Return(code);

        var matches = CodeIncludePattern.Matches(code);
        if (matches.Count == 0)
            return Observable.Return(code);

        // For each @@ match, fetch the referenced node via the caller's reader (NEVER await —
        // that's a 100% deadlock). Each result feeds the recursive resolution; the final
        // substituted string is built up in left-to-right order by serially aggregating the
        // per-match observables.
        IObservable<string> chain = Observable.Return(code);
        foreach (Match match in matches)
        {
            var authored = match.Groups[1].Value;
            var anchored = AnchorIncludePath(authored, anchorPath);
            var matchValue = match.Value;
            chain = chain.SelectMany(current =>
            {
                if (!resolved.Add(anchored))
                    return Observable.Return(current.Replace(matchValue, string.Empty));

                // The anchored candidate is tried FIRST and the authored path only as a fallback:
                // on a root mount the two are identical (one read, unchanged behaviour), and the
                // second read is reached only where today's single read already failed.
                return readInclude(anchored, anchored == authored ? null : authored)
                    .SelectMany(hit =>
                    {
                        if (hit.Node?.Content is CodeConfiguration cf
                            && !string.IsNullOrWhiteSpace(cf.Code))
                        {
                            logger.LogDebug("Resolved code include @@{Path}", hit.Path);
                            // Nested includes anchor from where THIS one was found, so a chain of
                            // mount-relative includes stays inside the same mount.
                            return ResolveCodeIncludes(
                                    cf.Code, resolved, hit.Path, readInclude, logger)
                                .Select(resolvedInner => current.Replace(matchValue, resolvedInner));
                        }
                        logger.LogWarning(
                            "Could not resolve code include @@{Path} referenced from {Anchor} "
                            + "— it stays VERBATIM in the source, so the compiler will report "
                            + "errors on the @@ line itself",
                            authored, anchorPath ?? "(no anchor)");
                        return Observable.Return(current);
                    });
            });
        }

        return chain;
    }

    /// <summary>
    /// Fold one <see cref="QueryResultChange{T}"/> into the accumulating path→node map — pure, so
    /// the chunk-accumulation contract is unit-testable. Initial/Reset/Added/Updated set; Removed
    /// deletes. Keyed by path, so a re-delivered chunk is idempotent rather than a duplicate.
    /// </summary>
    internal static ImmutableDictionary<string, MeshNode> ApplyQueryChange(
        ImmutableDictionary<string, MeshNode> acc, QueryResultChange<MeshNode> change)
    {
        if (change?.Items is not { Count: > 0 } items)
            return acc;
        foreach (var node in items)
        {
            if (string.IsNullOrEmpty(node?.Path))
                continue;
            acc = change.ChangeType == QueryChangeType.Removed
                ? acc.Remove(node.Path)
                : acc.SetItem(node.Path, node);
        }
        return acc;
    }

    /// <summary>
    /// The snapshot race between the authoritative direct mesh read (<paramref name="directProbe"/> —
    /// emits at most one NON-EMPTY source set, stays silent and completes when it finds nothing) and
    /// the cached synced query's first emission (<paramref name="cachedFirst"/>). A pure combinator
    /// so the race's correctness semantics are deterministically unit-testable
    /// (CodeEditRecompileTest.SourceSnapshot_*).
    ///
    /// <para>🚨 An EMPTY cached answer must never WIN the race (issue #612, CI run 30004790036
    /// "sub-case b"). The cached synced query replays its latest set SYNCHRONOUSLY on subscribe,
    /// while the probe cannot answer before its chunk quiet window — so under the old
    /// <c>Merge(...).FirstAsync()</c> shape a cached query that had latched EMPTY (a missed
    /// source-create update under load — the stale-synced-query class) ALWAYS beat the probe, the
    /// compile consumed ZERO sources, the configuration lambda's CS0103 parked the type, and every
    /// retry — including the explicit un-parking RequestedReleaseAt re-trigger — re-failed
    /// identically: a permanent wedge at Status=Error with no release. The probe leg already
    /// refuses to emit empty for exactly this reason ("compiling the type against NOTHING [is]
    /// strictly worse than the stall"); this applies the same rule to the cached leg.</para>
    ///
    /// <para>Semantics: the first ESTABLISHED NON-EMPTY answer from either side wins immediately (a
    /// healthy cached query still settles the snapshot with zero probe latency — the #690 regression
    /// guard). EMPTY settles only by CONSENSUS: both legs completed without producing a source —
    /// then a source-less, configuration-only NodeType still compiles. A cached leg that never
    /// emits at all leaves the race to the probe / the caller's outer <c>Timeout</c>, unchanged.</para>
    ///
    /// <para>🚨 An UNESTABLISHED report (a leg whose queries errored — issue #1218) never WINS the
    /// race, and never loses it silently either. It is held back until the merged stream completes,
    /// so a probe with one dead query cannot veto a perfectly healthy cached answer; but if no leg
    /// ever produces a real source set, the unestablished report is what settles the race — and the
    /// caller then refuses to compile rather than handing Roslyn a set it knows to be short. The
    /// old shape simply dropped the failed leg's contribution (<c>.Catch(_ =&gt; empty)</c>) and let
    /// the PARTIAL remainder win, which is precisely how a starved cross-silo read became a
    /// phantom <c>CS0246</c> on 14 of 56 types during a memex-cloud rollout.</para>
    /// </summary>
    internal static IObservable<SourceSnapshot> RaceSourceSnapshot(
        IObservable<SourceSnapshot> directProbe,
        IObservable<SourceSnapshot> cachedFirst)
        => directProbe
            .Merge(cachedFirst)
            .Publish(shared => Observable.Merge(
                // A real set — wins the instant it arrives, from either leg.
                shared.Where(static s => s.IsEstablished && s.Sources.Count > 0),
                // "I could not read the sources" — consulted only once BOTH legs have had
                // their say (TakeLast emits on completion), so it can never pre-empt a
                // healthy answer that is merely slower.
                shared.Where(static s => !s.IsEstablished).TakeLast(1)))
            .Take(1)
            // Both legs completed, neither found anything, neither reported a failure: the
            // sources genuinely do not exist. That is a CONTENT fact and the compile proceeds.
            .DefaultIfEmpty(SourceSnapshot.Empty);

    /// <summary>
    /// The emit path's source-set fold: dedup by path (case-insensitive), keep only
    /// non-executable Code nodes with non-blank code, in the order the snapshot delivered them.
    /// Executable scripts run via the kernel (ExecuteScriptRequest), never folded into the parent
    /// NodeType's Roslyn unit — top-level statements would collide with class declarations from
    /// Source/ siblings; Test/ commonly mixes both shapes, and this filter lets them coexist.
    /// Returns the matched configurations together with the matched paths (activity-log
    /// material for the caller).
    /// </summary>
    internal static (List<CodeConfiguration> Sources, List<string> MatchedPaths)
        CollectCompileSources(IEnumerable<MeshNode> matches, string nodePath, ILogger logger)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var acc = new List<CodeConfiguration>();
        var matchedPaths = new List<string>();
        foreach (var n in matches)
        {
            if (string.IsNullOrEmpty(n.Path) || !seen.Add(n.Path))
                continue;
            if (n.Content is CodeConfiguration cf
                && !string.IsNullOrWhiteSpace(cf.Code))
            {
                if (cf.IsExecutable)
                {
                    logger.LogDebug(
                        "Source discovery for {NodePath}: skipping executable Code {CodePath} — runs via kernel only",
                        nodePath, n.Path);
                    continue;
                }
                acc.Add(cf);
                matchedPaths.Add(n.Path);
            }
        }
        return (acc, matchedPaths);
    }

    /// <summary>
    /// The LSP path's variant of <see cref="CollectCompileSources"/>: the same dedup +
    /// executable filter, but retaining paths and versions alongside configurations so language
    /// services can address each file.
    /// </summary>
    internal static List<(string Path, CodeConfiguration Config, long LastModifiedTicks)>
        CollectSourcePairs(IEnumerable<MeshNode> matches)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pairs = new List<(string, CodeConfiguration, long)>();
        foreach (var n in matches)
        {
            if (string.IsNullOrEmpty(n.Path) || !seen.Add(n.Path)) continue;
            if (n.Content is CodeConfiguration cf
                && !string.IsNullOrWhiteSpace(cf.Code)
                && !cf.IsExecutable)
            {
                pairs.Add((n.Path, cf, n.LastModified.UtcTicks));
            }
        }
        return pairs;
    }

    /// <summary>
    /// Combines the discovered source files into the one compile unit the emit path hands to the
    /// skeleton generator: none → null, one → as-is, several → joined with <c>"\n\n"</c> in
    /// snapshot order. The join order and separator are part of the emitted bytes, which is why
    /// this lives inside the identity boundary.
    /// </summary>
    internal static CodeConfiguration? CombineSources(IReadOnlyList<CodeConfiguration> codeFiles)
        => codeFiles.Count switch
        {
            0 => null,
            1 => codeFiles[0],
            _ => new CodeConfiguration { Code = string.Join("\n\n", codeFiles.Select(cf => cf.Code)) }
        };
}
