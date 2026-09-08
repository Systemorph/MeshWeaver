using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// Governance ratchet for the rooted-Rx-connection defect class: a multicast chain
/// (<c>Replay(…)</c>, <c>Publish()</c>) whose <c>Connect()</c> handle nobody owns.
///
/// <para><b>The defect.</b> A bare <c>.AutoConnect(1)</c> keeps the handle its <c>Connect()</c>
/// returns to itself; a bare <c>.Connect()</c> whose result is dropped keeps it nowhere. The owner's
/// <c>Dispose()</c> therefore cannot reach the upstream — and the connect is often NOT synchronous:
/// <c>Defer(…).SubscribeOn(TaskPoolScheduler)</c> only QUEUES the upstream subscribe, and no teardown
/// phase joins a pool-queued Rx subscribe (<c>DisposalCompleted</c> covers the action blocks,
/// <c>IoPoolRegistry.DrainAll</c> covers <c>IIoPool</c> leaves, the <c>AsyncDisposeQueue</c> covers
/// enqueued cleanup). It ran whenever the pool reached it: MeshWeaver.Plugins run 34222933802
/// (2026-09-08, shard 1) captured 11 <c>ObjectDisposedException</c> stragglers across three suites,
/// every one <c>MeshNodeStreamCache.GetQueryRaw</c>'s Defer resolving <c>GetWorkspace()</c> from an
/// Autofac scope the mesh had already closed, the FutuRe pair 4 ms after <c>DISPOSE_DONE</c>. Core
/// #3737 fixed that one site; this guard keeps every other one fixed.</para>
///
/// <para><b>The fix at a site</b> is <c>MeshWeaver.Messaging.OwnedConnectionExtensions</c>:
/// <c>.Replay(1).AutoConnectOwnedBy(owner, nameof(Owner))</c> for a lazily-connected shared chain,
/// <c>.Publish().ConnectOwnedBy(owner)</c> for an eager feed — where <c>owner</c> is the hub whose
/// services the chain resolves (<c>RegisterForDisposal</c>, released in its ShutDown phase) or a
/// <see cref="System.Reactive.Disposables.CompositeDisposable"/> a singleton disposes with itself.
/// The helper registers the handle on connect, drops it when the chain terminates, disposes a
/// registration that arrives after the owner's disposal on the spot, and refuses a subscriber
/// arriving after the release with <see cref="ObjectDisposedException"/> rather than parking it on a
/// replay nothing will feed.</para>
///
/// <para><b>Why <c>RefCount()</c> is listed rather than rejected.</b> A ref-counted chain's
/// connection is owned by its SUBSCRIBERS — it releases with the last of them — so there is no
/// handle for an owner to hold; what must be owned is each subscription, which is the ordinary
/// <c>RegisterForDisposal</c> rule and is not visible at the <c>RefCount()</c> call site. Every such
/// site is therefore inventoried with the reason its subscriptions are owned, and the inventory may
/// only shrink unless a new line and a raised <see cref="RefCountBudget"/> land in the same change.</para>
///
/// <para><b>Why the ROOTED budget is zero from day one.</b> Every bare rooted site in <c>src/</c>
/// was converted in the change that added this guard (seven sites; see the allow file's header), so
/// the list starts empty for that form and may only ever be empty: adding a line is not a fix.</para>
/// </summary>
public class RootedRxConnectionRatchetGuard(ITestOutputHelper output)
{
    private const string AllowFileName = "RootedRxConnectionSites.allow";

    /// <summary>The one file that may spell <c>AutoConnect(</c> / <c>Connect()</c>: the helper itself.</summary>
    private const string HelperFile = "src/MeshWeaver.Messaging.Hub/OwnedConnectionExtensions.cs";

    /// <summary>Bare <c>.AutoConnect(</c> / <c>.Connect()</c> sites permitted in <c>src/</c>: none.</summary>
    private const int RootedBudget = 0;

    /// <summary>
    /// <c>.RefCount(</c> sites inventoried with a reason. Six on 2026-09-08. May only go down unless a
    /// new reasoned line and this constant move together.
    /// </summary>
    private const int RefCountBudget = 6;

    private static readonly string[] ScannedRoots = ["src"];

    /// <summary>
    /// The rooted forms. <c>.AutoConnect(</c> does not match <c>.AutoConnectOwnedBy(</c> (the next
    /// character is <c>O</c>, not <c>(</c>); <c>.Connect()</c> — empty parens — does not match
    /// <c>.ConnectOwnedBy(</c>.
    /// </summary>
    private static readonly Regex Rooted = new(@"\.AutoConnect\s*\(|\.Connect\s*\(\s*\)", RegexOptions.Compiled);

    private static readonly Regex RefCount = new(@"\.RefCount\s*\(", RegexOptions.Compiled);

    private enum Form { Rooted, RefCount }

    private sealed record Site(string File, int Line, string Symbol, Form Form, string Text);

    private sealed record Allowance(string File, string Symbol, string Reason);

    [Fact]
    public void EveryRootedRxConnectionInSrc_IsOwnedOrInventoried()
    {
        var root = SourceScan.FindRepoRoot();
        var allowed = ReadAllowFile(Path.Combine(root, "test", AllowFileName));
        var sites = Scan(root);

        var failures = new List<string>();
        var byKey = allowed.ToDictionary(a => Key(a.File, a.Symbol), a => a, StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);

        foreach (var site in sites.OrderBy(s => s.File, StringComparer.Ordinal).ThenBy(s => s.Line))
        {
            var key = Key(site.File, site.Symbol);
            if (!byKey.ContainsKey(key))
            {
                failures.Add(site.Form == Form.Rooted
                    ? $"  ROOTED     {site.File}:{site.Line} in `{site.Symbol}` — `{site.Text.Trim()}`. The connection "
                      + "handle is held by the chain, not by an owner, so no Dispose() can release it. "
                      + "Use `.AutoConnectOwnedBy(hub | CompositeDisposable, nameof(Owner))` or "
                      + "`.ConnectOwnedBy(owner)` from MeshWeaver.Messaging.OwnedConnectionExtensions. "
                      + "Do NOT add a line to " + AllowFileName + "."
                    : $"  REFCOUNT   {site.File}:{site.Line} in `{site.Symbol}` — `{site.Text.Trim()}`. A "
                      + "ref-counted chain's connection is owned by its subscribers; add a line to "
                      + AllowFileName + " stating who owns those subscriptions (RegisterForDisposal, a "
                      + "hub-scoped consumer, a completing one-shot) AND raise RefCountBudget in the "
                      + "same change — or convert to an owned connection.");
                continue;
            }
            used.Add(key);
        }

        // Budgets — counted over the allow file's LIVE entries, by the form the tree shows for them.
        var rootedAllowed = sites.Where(s => s.Form == Form.Rooted && byKey.ContainsKey(Key(s.File, s.Symbol)))
            .Select(s => Key(s.File, s.Symbol)).Distinct(StringComparer.Ordinal).Count();
        if (rootedAllowed > RootedBudget)
            failures.Add(
                $"  BUDGET     {rootedAllowed} bare rooted site(s) are allow-listed; the budget is {RootedBudget}. "
                + "A bare AutoConnect( / Connect() is never inventoried — convert it.");

        var refCountAllowed = sites.Where(s => s.Form == Form.RefCount && byKey.ContainsKey(Key(s.File, s.Symbol)))
            .Select(s => Key(s.File, s.Symbol)).Distinct(StringComparer.Ordinal).Count();
        if (refCountAllowed > RefCountBudget)
            failures.Add(
                $"  BUDGET     {refCountAllowed} RefCount site(s) are allow-listed; the budget is {RefCountBudget}. "
                + "Raise RefCountBudget in the same change that adds the reasoned line, so the growth is a "
                + "reviewed decision rather than a drift.");

        foreach (var stale in byKey.Keys.Where(k => !used.Contains(k)).OrderBy(k => k, StringComparer.Ordinal))
            failures.Add(
                $"  STALE      {stale} is allow-listed but no such site exists — the site moved, was "
                + "renamed, or was converted. Delete the line (and lower RefCountBudget if it was one).");

        foreach (var a in allowed.Where(a => string.IsNullOrWhiteSpace(a.Reason)))
            failures.Add($"  NO REASON  {Key(a.File, a.Symbol)} — every inventoried site carries a one-line reason.");

        Assert.True(failures.Count == 0,
            "A multicast chain's connection must be OWNED by the object whose services it resolves, so "
            + "that the owner's Dispose() releases it; a bare AutoConnect(1) / Connect() keeps the handle "
            + "where no teardown can reach it, and a pool-queued connect then runs against a closed scope "
            + "(11 disposed-scope stragglers in one Plugins run, 34222933802 — all this shape).\n"
            + string.Join("\n", failures));
    }

    /// <summary>
    /// Non-vacuity, part 1 — the CLASSIFIER, on synthetic source: the bare forms are flagged, the
    /// helper spellings are not, prose is not, and the symbol attribution reaches the member that
    /// encloses the call rather than a nearer local or parameter.
    /// </summary>
    [Fact]
    public void TheClassifierTellsABareConnectionFromAnOwnedOne()
    {
        const string bare = """
            namespace N;
            public sealed class Owner
            {
                private readonly IObservable<int> shared;
                public Owner(IObservable<int> source, ILogger? logger = null)
                {
                    shared = source.Replay(1).AutoConnect(1);
                }
                public IObservable<int> Feed() => source.Publish().Connect();
                public IObservable<int> Counted => source.Replay(1).RefCount();
            }
            """;
        var sites = Sites("x.cs", bare);
        Assert.Equal(3, sites.Count);
        Assert.Equal(("Owner", Form.Rooted), (sites[0].Symbol, sites[0].Form));
        Assert.Equal(("Feed", Form.Rooted), (sites[1].Symbol, sites[1].Form));
        Assert.Equal(("Counted", Form.RefCount), (sites[2].Symbol, sites[2].Form));

        const string owned = """
            public sealed class Owner
            {
                private readonly IObservable<int> shared = source.Replay(1).AutoConnectOwnedBy(hub, nameof(Owner));
                public Owner() { source.Publish().ConnectOwnedBy(hub); }
            }
            """;
        Assert.Empty(Sites("y.cs", owned));

        // Prose quoting the shape is not a call site — this file and the allow file both do it.
        const string prose = """
            public sealed class Owner
            {
                // a bare .AutoConnect(1) or .Connect() is the defect; "x.RefCount()" too
                public void M() { }
            }
            """;
        Assert.Empty(Sites("z.cs", prose));

        // Expression-bodied member spanning lines: the member is the declaration, not a call inside it.
        const string expressionBodied = """
            public sealed class Resolver
            {
                private sealed record Resolution(DateTimeOffset At, string? Id);
                private IObservable<Resolution> Lookup(string url, string? user) =>
                    ResolveToken(user)
                        .SelectMany(token => client.Get(url, token))
                        .Replay(1)
                        .AutoConnect(1);
            }
            """;
        var expr = Sites("r.cs", expressionBodied);
        Assert.Single(expr);
        Assert.Equal("Lookup", expr[0].Symbol);
    }

    /// <summary>
    /// Non-vacuity, part 2 — the WALK. The rooted inventory is empty by design, so the only evidence
    /// the tree is still being read is that the scan sees the helper's call sites (the converted
    /// rooted connections) and the inventoried RefCount sites.
    /// </summary>
    [Fact]
    public void TheScannerStillSeesTheConvertedSitesAndTheInventory()
    {
        var root = SourceScan.FindRepoRoot();
        var helperCalls = 0;
        foreach (var file in SourceScan.SourceFiles(root, ScannedRoots))
        {
            if (SourceScan.Relative(root, file) == HelperFile) continue;
            var code = SourceScan.MaskCommentsAndStrings(File.ReadAllText(file));
            helperCalls += HelperCalls.Matches(code).Count;
        }

        Assert.True(helperCalls >= 7,
            $"The scan found {helperCalls} AutoConnectOwnedBy/ConnectOwnedBy call sites in src/ (expected at least "
            + "7 — the sites converted on 2026-09-08). Either the helper was renamed, or SourceScan is not "
            + "reading the production tree, in which case the empty rooted inventory is proving nothing.");

        var sites = Scan(root);
        Assert.True(sites.Count(s => s.Form == Form.RefCount) >= 1,
            "The scan found no RefCount site at all — the pattern no longer matches this codebase or the "
            + "tree was not read; the inventory above is then vacuous.");

        foreach (var site in sites.OrderBy(s => s.File, StringComparer.Ordinal).ThenBy(s => s.Line))
            output.WriteLine($"{site.Form,-8} {site.File}:{site.Line}  {site.Symbol}");
    }

    private static readonly Regex HelperCalls = new(@"\.(?:AutoConnectOwnedBy|ConnectOwnedBy)\s*\(", RegexOptions.Compiled);

    private static string Key(string file, string symbol) => file + "\t" + symbol;

    private static List<Site> Scan(string root)
    {
        var result = new List<Site>();
        foreach (var file in SourceScan.SourceFiles(root, ScannedRoots))
        {
            var relative = SourceScan.Relative(root, file);
            if (relative == HelperFile) continue;
            string text;
            try { text = File.ReadAllText(file); }
            catch (IOException) { continue; } // a file a concurrent build is writing is not evidence
            result.AddRange(Sites(relative, text));
        }

        return result;
    }

    /// <summary>
    /// Every connection site in <paramref name="text"/>, attributed to its enclosing member.
    /// Comments and string literals are masked first, so prose quoting the shape is not counted.
    /// </summary>
    private static List<Site> Sites(string file, string text)
    {
        var result = new List<Site>();
        if (!text.Contains("Connect", StringComparison.Ordinal) && !text.Contains("RefCount", StringComparison.Ordinal))
            return result;

        var code = SourceScan.MaskCommentsAndStrings(text);
        var lines = code.Split('\n');
        var depths = DepthAtLineStart(code);

        foreach (var (regex, form) in new[] { (Rooted, Form.Rooted), (RefCount, Form.RefCount) })
        {
            foreach (Match match in regex.Matches(code))
            {
                var line = code.AsSpan(0, match.Index).Count('\n');
                result.Add(new Site(file, line + 1, EnclosingMember(lines, depths, line), form, lines[line]));
            }
        }

        return result.OrderBy(s => s.Line).ToList();
    }

    /// <summary>Brace depth at the START of each line of masked code.</summary>
    private static int[] DepthAtLineStart(string code)
    {
        var lines = code.Split('\n');
        var depths = new int[lines.Length];
        var depth = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            depths[i] = depth;
            foreach (var c in lines[i])
                depth += c == '{' ? 1 : c == '}' ? -1 : 0;
        }

        return depths;
    }

    private static readonly Regex TypeDeclaration = new(
        @"\b(?:class|record|struct|interface)\s+[A-Za-z_]\w*", RegexOptions.Compiled);

    /// <summary>
    /// A member declaration: optional attributes, modifiers, then either at least one modifier or at
    /// least one type token before the NAME, followed by a parameter list, an expression body, a
    /// block, an initializer or a terminator. A call (<c>ResolveToken(user)</c>) has neither a
    /// modifier nor a type token and does not match; a local (<c>var x = …</c>) is excluded by the
    /// <c>var</c> lookahead and by depth.
    /// </summary>
    private static readonly Regex MemberDeclaration = new(
        @"^\s*(?:\[[^\]]*\]\s*)*"
        + @"(?<mods>(?:(?:public|private|protected|internal|static|override|virtual|abstract|sealed|async|partial|new|readonly|extern|unsafe|required|volatile|const)\s+)*)"
        + @"(?<types>(?:(?!(?:var|return|throw|yield|await|else|using|case|default|new)\b)[A-Za-z_][\w.]*(?:<[^;(){}=]*>)?(?:\[[\],]*\])*\??\s+)*)"
        + @"(?<name>[A-Za-z_]\w*)\s*(?:<[^>]*>)?\s*(?:\(|=>|\{|=(?!=)|;|$)",
        RegexOptions.Compiled);

    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "if", "while", "for", "foreach", "switch", "using", "lock", "catch", "return", "new", "throw",
        "yield", "await", "case", "do", "try", "else", "get", "set", "init", "add", "remove", "is", "as",
        "in", "out", "ref", "params", "this", "base", "typeof", "nameof", "default", "sizeof", "checked",
        "unchecked", "fixed", "goto", "break", "continue", "when", "where", "select", "from", "var",
        "namespace", "class", "record", "struct", "interface", "enum", "delegate", "event",
    };

    /// <summary>
    /// The member enclosing <paramref name="hitLine"/>: the nearest preceding type declaration that
    /// ENCLOSES the hit fixes the member depth (one deeper than the type's line), and the nearest
    /// declaration-shaped line at that depth — the hit's own line included, for one-line members —
    /// names the member. Parameter and expression-body continuations (a previous line ending in
    /// <c>,</c>, <c>(</c> or <c>=&gt;</c>) are skipped, as are lines that begin with an operator.
    /// </summary>
    private static string EnclosingMember(string[] lines, int[] depths, int hitLine)
    {
        var typeLine = -1;
        for (var t = hitLine; t >= 0; t--)
        {
            if (depths[t] >= depths[hitLine] && !(t == hitLine)) continue;
            if (!TypeDeclaration.IsMatch(lines[t])) continue;
            if (Encloses(lines, depths, t, hitLine)) { typeLine = t; break; }
        }

        if (typeLine < 0) return "<no enclosing type>";
        var memberDepth = depths[typeLine] + 1;

        for (var k = hitLine; k > typeLine; k--)
        {
            if (depths[k] != memberDepth) continue;
            var line = lines[k];
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0 || trimmed[0] is '.' or '?' or ':' or '=' or ')' or '}' or '{') continue;
            if (IsContinuation(lines, k)) continue;
            var m = MemberDeclaration.Match(line);
            if (!m.Success) continue;
            var name = m.Groups["name"].Value;
            if (Keywords.Contains(name)) continue;
            if (m.Groups["mods"].Value.Length == 0 && m.Groups["types"].Value.Length == 0) continue;
            if (m.Groups["types"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(Keywords.Contains)) continue;
            return name;
        }

        return "<no enclosing member>";
    }

    /// <summary>
    /// Whether the type declared on <paramref name="typeLine"/> encloses <paramref name="hitLine"/>.
    /// The type's HEADER — a positional record's parameter list, a base list, constraints — sits at
    /// the type's own depth and runs to the line carrying the body's opening brace; only after that
    /// must every line stay deeper than the type. A one-line <c>record X(…);</c> has no body, so it
    /// encloses nothing.
    /// </summary>
    private static bool Encloses(string[] lines, int[] depths, int typeLine, int hitLine)
    {
        if (typeLine >= hitLine || depths[hitLine] <= depths[typeLine]) return false;

        var bodyStart = -1;
        for (var k = typeLine; k < hitLine; k++)
        {
            if (k + 1 < depths.Length && depths[k + 1] > depths[typeLine]) { bodyStart = k; break; }
            if (lines[k].Contains(';')) return false; // a header that ends in `;` declares a body-less type
        }

        if (bodyStart < 0) return false;
        for (var k = bodyStart + 1; k < hitLine; k++)
            if (depths[k] <= depths[typeLine] && lines[k].Trim().Length > 0) return false;

        return true;
    }

    private static bool IsContinuation(string[] lines, int k)
    {
        for (var p = k - 1; p >= 0; p--)
        {
            var prev = lines[p].TrimEnd();
            if (prev.Length == 0) continue;
            return prev.EndsWith(',') || prev.EndsWith('(') || prev.EndsWith("=>", StringComparison.Ordinal);
        }

        return false;
    }

    /// <summary><c>path&lt;TAB&gt;symbol&lt;TAB&gt;reason</c>, one per line; <c>#</c> starts a comment.</summary>
    private static List<Allowance> ReadAllowFile(string path)
    {
        Assert.True(File.Exists(path),
            $"{AllowFileName} is missing — without it this guard cannot tell an inventoried RefCount site "
            + "from a new one. Restore it from git rather than regenerating it.");

        return File.ReadAllLines(path)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l.Split('\t', StringSplitOptions.TrimEntries))
            .Select(parts =>
            {
                Assert.True(parts.Length >= 3,
                    $"{AllowFileName}: `{string.Join("\\t", parts)}` — every line is <path><TAB><member><TAB><reason>.");
                return new Allowance(parts[0], parts[1], string.Join(" ", parts.Skip(2)));
            })
            .ToList();
    }
}
