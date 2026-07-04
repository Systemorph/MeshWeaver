using System.Text.RegularExpressions;
using MeshWeaver.Mesh;

namespace MeshWeaver.AI.Navigation;

/// <summary>
/// How a <c>/navigate</c> argument row (or the <c>NavigateTo</c> tool's <c>path</c>) is interpreted.
/// The rule the user asked for: <b>try a direct path mapping first when the row is a single argument;
/// otherwise make sense of the free-text context the user typed on the row</b>.
/// </summary>
public enum NavigationInputKind
{
    /// <summary>Nothing to resolve — navigate to the current context / home.</summary>
    Empty,

    /// <summary>A single path-shaped token (<c>Doc/AI/ModelProviderSettings</c>, <c>@/rbuergi</c>, a
    /// quoted spaced path, a pasted URL, a <c>/search?…</c> route). Resolve by DIRECT PATH first.</summary>
    DirectPath,

    /// <summary>Free-text prose (<c>change my model</c>, <c>my notifications</c>). Resolve by CONTEXT —
    /// match an intent → skill, else semantic search for the best node.</summary>
    Phrase,
}

/// <summary>What a resolution landed on, so the caller can apply the right pane-aware action.</summary>
public enum NavigationTargetKind
{
    /// <summary>Could not resolve to anything usable.</summary>
    Unresolved,

    /// <summary>A mesh node path — renders in either pane (pane-aware navigation applies).</summary>
    Node,

    /// <summary>An app route (a Blazor <c>@page</c> such as <c>/search?…</c>, <c>/GlobalSettings</c>) —
    /// a page, not a renderable node, so it always navigates the main view by URL.</summary>
    Route,

    /// <summary>A behaviour skill (e.g. <c>/model</c>) — the caller RUNS the skill rather than navigating,
    /// because a skill "does more" than a route change.</summary>
    Skill,
}

/// <summary>
/// The outcome of resolving a navigation row: the concrete target, its kind, whether we had to correct /
/// interpret the literal input (so the caller can say "opened the closest match …"), and any alternatives.
/// </summary>
public record NavigationResolution
{
    /// <summary>The resolved path / route / skill path; <c>null</c> when <see cref="Kind"/> is Unresolved.</summary>
    public string? Target { get; init; }

    /// <summary>What <see cref="Target"/> is, so the caller applies the correct action.</summary>
    public NavigationTargetKind Kind { get; init; }

    /// <summary>True when the result is not the literal input — a URL correction, a search fallback, or an
    /// interpreted phrase. The caller surfaces this ("Couldn't find X — opened the closest match Y").</summary>
    public bool WasCorrected { get; init; }

    /// <summary>A short human-facing note about the resolution (shown under the chat input).</summary>
    public string? Message { get; init; }

    /// <summary>Other candidates worth offering, best-first.</summary>
    public IReadOnlyList<string> Alternatives { get; init; } = [];

    /// <summary>The canonical "nothing resolved" result.</summary>
    public static NavigationResolution Unresolved(string? message = null) =>
        new() { Kind = NavigationTargetKind.Unresolved, Target = null, Message = message };
}

/// <summary>
/// The PURE, deterministic core of navigation resolution — no mesh access, no async, fully unit-testable.
/// It classifies the row (<see cref="Classify"/>), corrects a path/URL into a clean mesh path or route
/// (<see cref="NormalizePath"/>), tells a route from a node (<see cref="IsRouteLike"/>), and ranks search
/// candidates for the resilient fallback (<see cref="Score"/> / <see cref="PickBest"/>). The reactive
/// orchestration that does existence checks and search lives in <c>NavigationResolver</c> and composes
/// these primitives.
/// </summary>
public static class NavigationTargetResolver
{
    private static readonly Regex MultiSlash = new("/{2,}", RegexOptions.Compiled);
    private static readonly Regex NodeSegment = new("(?i)/node/", RegexOptions.Compiled);

    // The real top-level Blazor @page routes (pages, not mesh nodes). A leading-slash token whose first
    // segment is one of these is an app route, navigated by URL — never rendered in the side panel.
    // A pattern switch, not a static collection (the no-static-state rule forbids even a constant set).
    private static bool IsPageRoute(string segment) => segment.ToLowerInvariant() switch
    {
        "search" or "chat" or "create" or "welcome" or "login"
            or "onboarding" or "privacy" or "dev" or "globalsettings" => true,
        _ => false,
    };

    /// <summary>
    /// Classifies a raw navigation row. Empty → <see cref="NavigationInputKind.Empty"/>; a single
    /// path-shaped token (no unquoted whitespace, or a fully-quoted spaced path) →
    /// <see cref="NavigationInputKind.DirectPath"/>; free-text prose → <see cref="NavigationInputKind.Phrase"/>.
    /// </summary>
    public static NavigationInputKind Classify(string? row)
    {
        var s = row?.Trim();
        if (string.IsNullOrEmpty(s))
            return NavigationInputKind.Empty;

        // A fully-quoted string is one argument even with interior spaces (a spaced file/node path).
        if (s.Length >= 2 &&
            ((s[0] == '"' && s[^1] == '"') || (s[0] == '\'' && s[^1] == '\'')))
            return s.Length > 2 ? NavigationInputKind.DirectPath : NavigationInputKind.Empty;

        // Ignore a single leading @ (unified-content-reference prefix) for the token test.
        var core = s.StartsWith('@') ? s[1..] : s;
        if (core.Length == 0)
            return NavigationInputKind.Empty;

        // One token (no whitespace) → a path; multiple words → free-text context.
        return core.AsSpan().IndexOfAny(" \t\r\n") < 0
            ? NavigationInputKind.DirectPath
            : NavigationInputKind.Phrase;
    }

    /// <summary>
    /// Corrects a path/URL token into a clean, navigable path. Strips a leading <c>@</c> and surrounding
    /// quotes; strips a scheme+host (<c>https://host/…</c> → <c>/…</c>); percent-decodes the path part;
    /// removes a stray <c>/node/</c> segment; collapses duplicate slashes; trims a trailing slash. A route
    /// query string (after <c>?</c>) is preserved verbatim. Returns the cleaned token (a leading slash is
    /// preserved — it distinguishes an app route from a mesh path; see <see cref="IsRouteLike"/>).
    /// </summary>
    public static string NormalizePath(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return string.Empty;

        var s = token.Trim();

        // Strip one layer of matching surrounding quotes.
        if (s.Length >= 2 &&
            ((s[0] == '"' && s[^1] == '"') || (s[0] == '\'' && s[^1] == '\'')))
            s = s[1..^1].Trim();

        // Any remaining quote characters are illegal in a path — drop them.
        if (s.IndexOfAny(['"', '\'']) >= 0)
            s = s.Replace("\"", string.Empty).Replace("'", string.Empty);

        s = s.TrimStart('@');

        // Strip scheme + host: keep everything from the first '/' after "://".
        var schemeIdx = s.IndexOf("://", StringComparison.Ordinal);
        if (schemeIdx >= 0)
        {
            var afterScheme = schemeIdx + 3;
            var firstSlash = s.IndexOf('/', afterScheme);
            s = firstSlash >= 0 ? s[firstSlash..] : "/";
        }

        // Split off a query string so decoding/slash-collapsing never touches it.
        string query = string.Empty;
        var q = s.IndexOf('?');
        if (q >= 0)
        {
            query = s[q..];
            s = s[..q];
        }

        // Percent-decode the path part only (mesh paths never url-encode separators; a pasted URL might).
        if (s.IndexOf('%') >= 0)
        {
            try { s = Uri.UnescapeDataString(s); }
            catch (UriFormatException) { /* leave as-is */ }
        }

        s = NodeSegment.Replace(s, "/");     // remove a mistaken /node/ segment
        var hadLeadingSlash = s.StartsWith('/');
        s = MultiSlash.Replace(s, "/");      // collapse // → /
        if (s.Length > 1)
            s = s.TrimEnd('/');
        if (hadLeadingSlash && !s.StartsWith('/'))
            s = "/" + s;

        return s + query;
    }

    /// <summary>
    /// True when a normalized token is an APP ROUTE (a Blazor page) rather than a mesh node: it carries a
    /// query string, or it is a leading-slash path whose first segment is a known top-level page route.
    /// A mesh node path (<c>Doc/AI/X</c>, or a pasted <c>/rbuergi/Foo</c>) is NOT route-like.
    /// </summary>
    public static bool IsRouteLike(string? normalizedPath)
    {
        if (string.IsNullOrEmpty(normalizedPath))
            return false;
        if (normalizedPath.Contains('?'))
            return true;
        if (!normalizedPath.StartsWith('/'))
            return false;
        var firstSegment = normalizedPath[1..].Split('/', 2)[0];
        return IsPageRoute(firstSegment);
    }

    /// <summary>The last <c>/</c>-delimited segment of a path (used for name/last-segment matching).</summary>
    public static string LastSegment(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return string.Empty;
        var trimmed = path.TrimEnd('/');
        var idx = trimmed.LastIndexOf('/');
        return idx >= 0 ? trimmed[(idx + 1)..] : trimmed;
    }

    /// <summary>
    /// A deterministic relevance score of a candidate node against a query token (path or phrase). Higher
    /// is better; <c>0</c> means "no meaningful match" (the caller discards it). Favours exact path, then
    /// exact last-segment/name, then containment, with a shorter-path (more specific) tie-breaker folded in.
    /// Pure — the resilient search fallback re-ranks raw search hits through this.
    /// </summary>
    public static int Score(string? query, MeshNode candidate)
    {
        if (candidate is null || string.IsNullOrWhiteSpace(query))
            return 0;

        var qy = query.Trim().TrimStart('@').Trim('/').ToLowerInvariant();
        if (qy.Length == 0)
            return 0;
        var qLast = LastSegment(qy);

        var path = (candidate.Path ?? string.Empty).Trim('/').ToLowerInvariant();
        var name = (candidate.Name ?? candidate.Id ?? string.Empty).ToLowerInvariant();
        var pLast = LastSegment(path);

        var score = 0;
        if (path == qy) score += 1000;
        else if (pLast == qLast && qLast.Length > 0) score += 500;
        if (name == qy || name == qLast) score += 400;
        if (score == 0)
        {
            // Substring / token overlap — the free-text phrase case.
            if (name.Contains(qy)) score += 160;
            if (path.Contains(qy)) score += 120;
            var overlap = qy.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Count(tok => tok.Length > 2 && (name.Contains(tok) || path.Contains(tok)));
            score += overlap * 40;
        }
        if (score == 0)
            return 0;

        // Tie-breakers baked into the score: prefer shorter (more specific) paths, then explicit Order.
        score += Math.Max(0, 40 - path.Length / 4);
        score += Math.Max(0, 20 - (candidate.Order ?? 0));
        return score;
    }

    /// <summary>
    /// Picks the best candidate for a query from a raw search result set, or <c>null</c> when none scores
    /// above zero. Deterministic: highest <see cref="Score"/>, ties broken by shorter path then ordinal
    /// name — so the same inputs always yield the same target.
    /// </summary>
    public static MeshNode? PickBest(string? query, IEnumerable<MeshNode> candidates)
    {
        MeshNode? best = null;
        var bestScore = 0;
        foreach (var c in candidates ?? [])
        {
            var s = Score(query, c);
            if (s <= 0)
                continue;
            if (s > bestScore ||
                (s == bestScore && best is not null && ComparePreferred(c, best) < 0))
            {
                best = c;
                bestScore = s;
            }
        }
        return best;
    }

    /// <summary>
    /// Scores a SKILL against a free-text phrase for the "navigate to a skill" intent. The strong signal is
    /// the skill's name word appearing as a token in the phrase (<c>"change my model"</c> → <c>/model</c>);
    /// description-token overlap is a weak reinforcement. Returns <c>0</c> when the phrase does not mention
    /// the skill's name word at all — so a phrase only routes to a skill when it clearly asks for one, and
    /// otherwise falls through to node search. Pure.
    /// </summary>
    public static int ScoreSkill(string? phrase, string? skillName, string? description)
    {
        if (string.IsNullOrWhiteSpace(phrase) || string.IsNullOrWhiteSpace(skillName))
            return 0;

        var nameWord = skillName.Trim().TrimStart('/').ToLowerInvariant();
        if (nameWord.Length == 0)
            return 0;

        var tokens = phrase.ToLowerInvariant()
            .Split([' ', '\t', '\r', '\n', ',', '.', '?', '!'], StringSplitOptions.RemoveEmptyEntries);

        // The name word must appear in the phrase — the required strong signal.
        if (!tokens.Contains(nameWord))
            return 0;

        var score = 200;
        var desc = (description ?? string.Empty).ToLowerInvariant();
        foreach (var tok in tokens)
            if (tok.Length > 2 && tok != nameWord && desc.Contains(tok))
                score += 15;
        return score;
    }

    /// <summary>
    /// Picks the best skill for a phrase (highest <see cref="ScoreSkill"/> over name + description), or
    /// <c>null</c> when the phrase doesn't clearly name a skill. Deterministic ties: highest score, then
    /// shorter path, then ordinal name.
    /// </summary>
    public static MeshNode? PickBestSkill(string? phrase, IEnumerable<MeshNode> skills)
    {
        MeshNode? best = null;
        var bestScore = 0;
        foreach (var s in skills ?? [])
        {
            var score = ScoreSkill(phrase, s.Name ?? s.Id, s.Description);
            if (score <= 0)
                continue;
            if (score > bestScore ||
                (score == bestScore && best is not null && ComparePreferred(s, best) < 0))
            {
                best = s;
                bestScore = score;
            }
        }
        return best;
    }

    // Prefer the shorter path, then ordinal-least name — a stable, total order for ties.
    private static int ComparePreferred(MeshNode a, MeshNode b)
    {
        var la = (a.Path ?? string.Empty).Length;
        var lb = (b.Path ?? string.Empty).Length;
        if (la != lb)
            return la - lb;
        return string.CompareOrdinal(a.Name ?? a.Id, b.Name ?? b.Id);
    }
}
