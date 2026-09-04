using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// Governance ratchet for the un-localizable activity transcript (#3236): a NEW
/// <c>new LogMessage("…", level)</c> that is not followed by <c>.WithKey(…)</c> may not be added.
///
/// <para><b>Why this needed a guard at all.</b> The gap was invisible because nothing failed.
/// <c>LocalizationTest</c> holds the two catalogs against each other and the plugins-repo drift guard
/// holds the client mirror against core — but neither can see text that never reaches a catalog.
/// 53 write sites persisted rendered ENGLISH onto the node, 0 of them localizable, and every gate in
/// the fleet was green over it. A German viewer opening an import, a compile, a write-conflict or a
/// GitSync activity read English, every line, for as long as the feature has existed.</para>
///
/// <para><b>What "keyed" means here.</b> The write site cannot resolve a language — it runs
/// server-side with no viewer in scope, and several viewers with different locales later read the
/// same stored row. So a localizable entry carries the catalog KEY plus its named arguments
/// (<c>LogMessage.WithKey</c>) and the RENDERER resolves it off the viewer's
/// <c>AccessContext.Locale</c>. <c>Message</c> stays the English fallback, which is why every
/// un-migrated site and every row already in the database keeps rendering exactly as before.</para>
///
/// <para><b>Why the file is SEEDED rather than empty.</b> Twenty sites are legitimately unkeyable and
/// are expected to stay that way, so an empty allow file would be a lie that someone would have to
/// re-litigate at every one of them. They fall in two groups:</para>
/// <list type="bullet">
///   <item><b>Generic plumbing</b> — an <c>ILogger</c> adapter (<c>ActivityLogLogger</c>,
///   <c>Activity.Log&lt;TState&gt;</c>) or a helper whose whole input is a <c>string message</c>
///   parameter (<c>NodeTypeCompilationActivity.AppendLog</c>, <c>MeshNodeCompilationService.Append*</c>,
///   <c>ActivityRunner</c>, <c>ActivityLog.Fail</c>). These sites do not KNOW a sentence; a caller
///   that wants a key builds the <see cref="MeshWeaver.Data.LogMessage"/> itself and hands it to the
///   batched overload (<c>AppendLogs</c> / <c>ActivityLogAppender.Append</c>), which is why no
///   signature change is needed to migrate one.</item>
///   <item><b>Verbatim upstream text</b> — <c>ex.Message</c>, a Roslyn diagnostic, a descendant hub's
///   own refusal, a composed multi-clause import summary. No catalog can carry these, and pretending
///   otherwise would produce a template with one <c>{detail}</c> placeholder and no translation.</item>
/// </list>
///
/// <para><b>The ratchet may only SHRINK.</b> A new file, a raised count, or a raised TOTAL is a
/// failure. A line that has gone stale (its site was migrated) is REPORTED, not failed: two PRs
/// migrating sites concurrently would otherwise red <c>main</c> on whichever merged second, and a
/// gate that punishes the direction it is asking for teaches people to stop going that way.</para>
///
/// <para>🚨 <b>What this guard cannot see, stated so nobody reads its green as more than it is.</b>
/// It matches the SPELLED constructor. A target-typed <c>Messages = [new("…", level)]</c> is
/// invisible to it — and that is not hypothetical: three such sites in
/// <c>LayoutClientExtensions</c> were missing from the issue's own 53-site census for exactly this
/// reason. <see cref="NoTargetTypedLogMessageConstructionHidesFromTheRatchet"/> closes that hole by
/// banning the target-typed spelling outright; keep the two tests together.</para>
/// </summary>
public class UnkeyedActivityLogMessageRatchetGuard(ITestOutputHelper output)
{
    /// <summary>
    /// The seeded inventory's size. Per-file entries stop a new unkeyed site in a file that already
    /// carries some; this stops the list as a WHOLE from growing — including by the trick of adding
    /// a new file's line. Lower it whenever you delete or lower an entry.
    /// </summary>
    private const int TotalBudget = 21;

    /// <summary>Production roots. <c>test/</c> is deliberately out of scope: a test's activity log is
    /// never rendered to a viewer, so keying one would be ceremony.</summary>
    private static readonly string[] ScannedRoots = ["src"];

    /// <summary>
    /// 🚨 The construction being matched, as a PATTERN and not a literal — <c>new LogMessage(</c> and
    /// <c>new  LogMessage (</c> and the form broken across lines are one construction, and a literal
    /// substring sees only the first.
    /// </summary>
    private static readonly Regex MarkerPattern =
        new(@"new\s+LogMessage\s*\(", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>The cheap pre-filter — must stay WEAKER than <see cref="MarkerPattern"/>.</summary>
    private const string MarkerPreFilter = "LogMessage";

    /// <summary>
    /// The target-typed spelling the ratchet is BLIND to. Banned outright rather than taught to the
    /// matcher: <c>new(</c> in a collection expression carries no type name at all, so no textual
    /// scanner can tell a <c>LogMessage</c> from anything else there.
    /// </summary>
    private static readonly Regex TargetTypedPattern =
        new(@"Messages\s*=\s*\[\s*new\s*\(", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>The catalog namespace every activity key lives under, matched as a STRING LITERAL so
    /// a key reached through a local (a per-branch <c>failureLeadKey</c>) is checked too.</summary>
    private static readonly Regex ActivityKeyLiteral =
        new("\"(activity\\.[A-Za-z0-9_]+(?:\\.[A-Za-z0-9_]+)*)\"",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private const string AllowFileName = "UnkeyedActivityLogMessages.allow";

    [Fact]
    public void NoNewSiteWritesAnUnkeyedActivityLogMessage()
    {
        var root = SourceScan.FindRepoRoot();
        var allowed = SourceScan.ReadAllowFile(Path.Combine(root, "test", AllowFileName), AllowFileName);
        var found = ScanUnkeyed(root);

        var failures = new List<string>();

        foreach (var (file, count) in found.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!allowed.TryGetValue(file, out var budget))
                failures.Add(
                    $"  NEW SITE   {file} ({count}) — a `new LogMessage(\"…\")` with no `.WithKey(…)`. "
                    + "Add a key to BOTH strings.en.json and strings.de.json and chain "
                    + ".WithKey(\"activity.…\", (\"name\", value)) onto the constructor. Do NOT add a "
                    + "line to " + AllowFileName + ".");
            else if (count > budget)
                failures.Add(
                    $"  MORE       {file} ({count} > {budget} allowed) — an unkeyed site was ADDED to "
                    + "a file that already carries some.");
        }

        var total = allowed.Values.Sum();
        if (total > TotalBudget)
            failures.Add(
                $"  TOTAL      {total} allowances > {TotalBudget} budgeted — the inventory GREW. "
                + "Adding a line to " + AllowFileName + " is not a fix.");

        // Stale entries are reported, never failed — see the class remarks.
        foreach (var (file, budget) in allowed.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var count = found.GetValueOrDefault(file, 0);
            if (count < budget)
                output.WriteLine(
                    $"STALE (please tidy): {file} — {count} found, {budget} allowed. "
                    + $"{(count == 0 ? "Delete the line" : $"Lower it to {count}")} and lower "
                    + $"TotalBudget by {budget - count}.");
        }

        Assert.True(failures.Count == 0,
            "An activity transcript is a RENDERED surface. A `new LogMessage(\"literal\", level)` "
            + "with no `.WithKey(…)` persists ENGLISH onto the node, and no renderer can translate "
            + "it afterwards — a German viewer reads that line in English forever (#3236).\n"
            + string.Join("\n", failures));
    }

    /// <summary>
    /// 🚨 Closes the ratchet's one structural blind spot. A target-typed
    /// <c>Messages = [new("…", LogLevel.Error)]</c> constructs a <see cref="MeshWeaver.Data.LogMessage"/>
    /// while naming no type, so <see cref="MarkerPattern"/> — and the issue's own <c>grep</c> census —
    /// cannot see it. Three real sites hid there. Spell the type; the ratchet then counts it, and the
    /// allow file records it honestly whether it is keyed or not.
    /// </summary>
    [Fact]
    public void NoTargetTypedLogMessageConstructionHidesFromTheRatchet()
    {
        var root = SourceScan.FindRepoRoot();
        var offenders = SourceScan.SourceFiles(root, ScannedRoots)
            .Select(f => (File: SourceScan.Relative(root, f), Count: TargetTypedIn(ReadOrEmpty(f))))
            .Where(x => x.Count > 0)
            .OrderBy(x => x.File, StringComparer.Ordinal)
            .ToArray();

        Assert.True(offenders.Length == 0,
            "A target-typed `Messages = [new(…)]` constructs a LogMessage while naming no type, so "
            + "the #3236 ratchet cannot see it and neither can a grep census. Spell it "
            + "`new LogMessage(…)`:\n"
            + string.Join("\n", offenders.Select(o => $"  {o.File} ({o.Count})")));
    }

    /// <summary>
    /// 🚨 EVERY <c>activity.*</c> key named in production source must exist in the ENGLISH catalog.
    /// This is the half <c>LocalizationTest</c> structurally cannot do: it compares the two catalogs
    /// against each other, so a key that is in NEITHER — a typo, a rename that missed the JSON —
    /// passes there and renders a raw <c>activity.delete.notFuond</c> token to every viewer.
    ///
    /// <para>Matched as a string LITERAL rather than as a <c>.WithKey("…")</c> argument on purpose:
    /// a key can legitimately reach <c>WithKey</c> through a local (the compile lane picks one of
    /// three per failure branch), and a matcher tied to the call shape would silently stop checking
    /// exactly the keys that are hardest to eyeball.</para>
    /// </summary>
    [Fact]
    public void EveryActivityKeyNamedInSourceIsInTheEnglishCatalog()
    {
        var root = SourceScan.FindRepoRoot();
        var used = ActivityKeysIn(root);

        Assert.True(used.Count > 0,
            "No `activity.*` key literal was found anywhere under " + string.Join(", ", ScannedRoots)
            + ". Either every activity message was un-keyed again — which is the #3236 regression —"
            + " or this scan is pointed at the wrong tree and has checked nothing.");

        var missing = used
            .Where(k => !LocalizationCatalog.Keys.Contains(k))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToArray();

        Assert.True(missing.Length == 0,
            "These activity keys are used in src/ but are in NO catalog, so they render as raw "
            + "tokens. Add them to BOTH strings.en.json and strings.de.json:\n  "
            + string.Join("\n  ", missing));
    }

    /// <summary>
    /// The reverse direction, kept separate because it is a tidiness failure rather than a rendering
    /// one: an <c>activity.*</c> key nobody names is dead weight that a later reader will assume is
    /// live. (Generic catalog orphans are <c>LocalizationTest</c>'s business; this narrows to the
    /// namespace whose keys are only ever named from source.)
    /// </summary>
    [Fact]
    public void NoActivityKeyInTheCatalogIsUnused()
    {
        var root = SourceScan.FindRepoRoot();
        var used = ActivityKeysIn(root);

        var orphans = LocalizationCatalog.Keys
            .Where(k => k.StartsWith("activity.", StringComparison.Ordinal))
            .Where(k => !used.Contains(k))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToArray();

        Assert.True(orphans.Length == 0,
            "These activity keys are in the catalogs but named nowhere in src/ — delete them, or "
            + "the writer that was meant to use them was never migrated:\n  "
            + string.Join("\n  ", orphans));
    }

    /// <summary>
    /// 🚨 Every language's rendering of an <c>activity.*</c> key must name the SAME placeholders.
    /// A translator who drops <c>{path}</c> silently deletes the one piece of information the line
    /// carries — "Kein Node unter dem Pfad" tells a German operator nothing — and one who mistypes
    /// it leaves a literal <c>{ptah}</c> on screen. Neither is visible to <c>LocalizationTest</c>,
    /// which compares KEY SETS, nor to the plugins-repo drift guard, which compares values against
    /// core rather than against each other.
    ///
    /// <para>Only <c>activity.*</c> is checked: the ~1,170 older keys are positional (<c>{0}</c>),
    /// where reordering across languages is deliberate and a count check would be the right test
    /// instead. Named placeholders are the shape this rule fits.</para>
    /// </summary>
    [Fact]
    public void EveryActivityKeyNamesTheSamePlaceholdersInEveryLanguage()
    {
        var placeholder = new Regex(@"\{([A-Za-z_][A-Za-z0-9_]*)\}",
            RegexOptions.CultureInvariant);
        var activityKeys = LocalizationCatalog.Keys
            .Where(k => k.StartsWith("activity.", StringComparison.Ordinal))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToArray();

        Assert.True(activityKeys.Length > 0,
            "the English catalog carries no activity.* key — this check would pass on nothing");

        var mismatches = new List<string>();
        foreach (var key in activityKeys)
        {
            var expected = Names(LocalizationCatalog.Get(key, Locales.Default));
            foreach (var locale in Locales.Supported.Where(l => l != Locales.Default))
            {
                var actual = Names(LocalizationCatalog.Get(key, locale));
                if (!expected.SetEquals(actual))
                    mismatches.Add(
                        $"  {key} [{locale}] names {{{string.Join(", ", actual.OrderBy(x => x, StringComparer.Ordinal))}}} "
                        + $"but English names {{{string.Join(", ", expected.OrderBy(x => x, StringComparer.Ordinal))}}}");
            }
        }

        Assert.True(mismatches.Count == 0,
            "An activity line's placeholders carry the only per-occurrence information it has — the "
            + "path, the count, the upstream detail. A translation that drops or renames one deletes "
            + "that information or prints a literal {token}:\n" + string.Join("\n", mismatches));

        HashSet<string> Names(string template) =>
            placeholder.Matches(template).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Non-vacuity, pinned in the same run: the scanner must actually SEE both shapes it
    /// discriminates. A scanner that matched nothing would report every allow entry as STALE rather
    /// than fail, and a scanner that could not recognise <c>.WithKey</c> would report the whole tree
    /// as unkeyed — the ratchet reads as evidence in both directions, so both must be checked
    /// against the PRODUCTION tree, not only against planted strings (#2844).
    /// </summary>
    [Fact]
    public void TheScannerFindsBothShapesItDiscriminates()
    {
        var root = SourceScan.FindRepoRoot();
        var (total, keyed) = ScanTotals(root);

        Assert.True(total > 0,
            "The scanner found no `new LogMessage(` anywhere under "
            + string.Join(", ", ScannedRoots) + " — that is a BROKEN SCAN, not a clean tree.");
        Assert.True(keyed > 0,
            "The scanner found LogMessage sites but recognised NONE of them as keyed, although the "
            + "tree carries migrated sites. The `.WithKey` half of the matcher is broken, which "
            + "would make the ratchet report every site as unkeyed.");
        Assert.True(total - keyed > 0,
            "The scanner recognised EVERY site as keyed. Either the whole population is migrated — "
            + "in which case empty " + AllowFileName + " and delete this assertion — or the "
            + "unkeyed half of the matcher is broken, which would make the ratchet pass on nothing.");

        output.WriteLine($"LogMessage sites under src/: {total} total, {keyed} keyed, {total - keyed} unkeyed.");

        // The seam's own remarks QUOTE the shape verbatim, so prove comment masking works — a
        // scanner that counted prose would ratchet against documentation.
        var seam = Path.Combine(root, "src", "MeshWeaver.Data.Contract", "LogMessage.cs");
        Assert.True(File.Exists(seam), "the seam file must exist for this check to mean anything");
        Assert.Contains("new LogMessage($\"Node not found at path: {path}\", LogLevel.Error)",
            File.ReadAllText(seam));
        Assert.False(ScanUnkeyed(root).ContainsKey("src/MeshWeaver.Data.Contract/LogMessage.cs"),
            "the scanner counted the seam's own DOC COMMENT as a call site — comment masking is "
            + "broken, and every count in the allow file is therefore unreliable.");
    }

    /// <summary>
    /// 🚨 NON-VACUITY OF THE MATCHER, spelling by spelling. Sibling to
    /// <see cref="TheScannerFindsBothShapesItDiscriminates"/>, which only proves it sees SOMETHING.
    /// This proves it sees each thing it claims to — including the two shapes that make the
    /// <c>.WithKey</c> lookahead non-trivial: an object initializer between the constructor and the
    /// call, and the call broken onto its own line.
    /// </summary>
    [Fact]
    public void TheMatcherSeesEverySpellingItClaimsTo()
    {
        // Unkeyed, in the shapes real sites take.
        Assert.Equal(1, UnkeyedIn("""var m = new LogMessage("boom", LogLevel.Error);"""));
        Assert.Equal(1, UnkeyedIn("""messages.Add(new LogMessage($"failed: {ex.Message}", LogLevel.Error));"""));
        Assert.Equal(1, UnkeyedIn("var m = new LogMessage(\n    \"boom\",\n    LogLevel.Error);"));
        // …the whitespace-tolerant spelling a literal marker would miss.
        Assert.Equal(1, UnkeyedIn("""var m = new  LogMessage ("boom", LogLevel.Error);"""));

        // Keyed — same line, wrapped line, and with an object initializer in between.
        Assert.Equal(0, UnkeyedIn("""var m = new LogMessage("boom", LogLevel.Error).WithKey("activity.x.y");"""));
        Assert.Equal(0, UnkeyedIn(
            "var m = new LogMessage(\"boom\", LogLevel.Error)\n    .WithKey(\"activity.x.y\", (\"a\", b));"));
        Assert.Equal(0, UnkeyedIn(
            "var m = new LogMessage(\"boom\", LogLevel.Error) { Scopes = [s] }.WithKey(\"activity.x.y\");"));
        Assert.Equal(0, UnkeyedIn(
            "var m = new LogMessage(\"boom\", LogLevel.Error)\n    { Scopes = [s] }\n    .WithKey(\"activity.x.y\");"));
        // A key chosen per branch is still keyed — the ratchet asks whether a key is attached, and
        // EveryActivityKeyNamedInSourceIsInTheEnglishCatalog is what checks WHICH key.
        Assert.Equal(0, UnkeyedIn(
            """var m = new LogMessage(t, LogLevel.Error).WithKey(ok ? "activity.a" : "activity.b");"""));

        // …and what it must NOT see, so widening never becomes matching everything.
        Assert.Equal(0, UnkeyedIn("""// new LogMessage("boom", LogLevel.Error) is the defect."""));
        Assert.Equal(0, UnkeyedIn("""var doc = "new LogMessage(\"boom\", LogLevel.Error)";"""));
        Assert.Equal(0, UnkeyedIn("""var m = new LogMessageEnvelope("boom", LogLevel.Error);"""));
        Assert.Equal(0, UnkeyedIn("""hub.Post(new LogMessages());"""));

        // A method NAMED WithKey on something else does not count — the lookahead is anchored to the
        // constructor's own closing paren, not to "the file mentions WithKey somewhere".
        Assert.Equal(1, UnkeyedIn(
            "var m = new LogMessage(\"boom\", LogLevel.Error);\nvar other = thing.WithKey(\"activity.x\");"));

        // The key-literal scan, which is what checks a key EXISTS. It reads prose too, on purpose
        // (see KeysIn): a key quoted in a doc comment must be a real one.
        Assert.Equal(new[] { "activity.a.b" }, KeysIn("""x.WithKey("activity.a.b");""").ToArray());
        Assert.Equal(new[] { "activity.in.prose" }, KeysIn("""// "activity.in.prose" is the key.""").ToArray());
        Assert.Empty(KeysIn("""var s = "activityNoDot";"""));
        Assert.Empty(KeysIn("""// activity.unquoted.is.not.a.literal"""));

        // The target-typed ban.
        Assert.Equal(1, TargetTypedIn("""Messages = [new("boom", LogLevel.Error)]"""));
        Assert.Equal(0, TargetTypedIn("""Messages = [new LogMessage("boom", LogLevel.Error)]"""));
        Assert.Equal(0, TargetTypedIn("""// Messages = [new("boom", LogLevel.Error)]"""));
    }

    private static string ReadOrEmpty(string path)
    {
        // A file a concurrent build is writing is not evidence.
        try { return File.ReadAllText(path); }
        catch (IOException) { return string.Empty; }
    }

    private static Dictionary<string, int> ScanUnkeyed(string root) =>
        SourceScan.SourceFiles(root, ScannedRoots)
            .Select(f => (Relative: SourceScan.Relative(root, f), Count: UnkeyedIn(ReadOrEmpty(f))))
            .Where(x => x.Count > 0)
            .ToDictionary(x => x.Relative, x => x.Count, StringComparer.Ordinal);

    private static (int Total, int Keyed) ScanTotals(string root)
    {
        var total = 0;
        var keyed = 0;
        foreach (var file in SourceScan.SourceFiles(root, ScannedRoots))
        {
            var (t, k) = CountIn(ReadOrEmpty(file));
            total += t;
            keyed += k;
        }

        return (total, keyed);
    }

    private static HashSet<string> ActivityKeysIn(string root)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in SourceScan.SourceFiles(root, ScannedRoots))
            keys.UnionWith(KeysIn(ReadOrEmpty(file)));
        return keys;
    }

    /// <summary>
    /// The key-literal matcher over TEXT — deliberately over the RAW text, comments included.
    ///
    /// <para>The obvious refinement (mask comments first) is the wrong trade here. A masker that got
    /// it wrong would DROP keys, and a dropped key is one this guard silently stops checking —
    /// exactly the failure mode it exists to prevent. Counting a quoted key in prose costs nothing
    /// but a doc comment having to quote a key that really exists, which is itself worth having.</para>
    /// </summary>
    internal static IEnumerable<string> KeysIn(string text) =>
        text.Contains("activity.", StringComparison.Ordinal)
            ? ActivityKeyLiteral.Matches(text).Select(m => m.Groups[1].Value).ToArray()
            : [];

    /// <summary>
    /// 🚨 Pre-filtered on <c>Messages</c>, NOT on <c>LogMessage</c>. A file whose only construction is
    /// the target-typed one need never spell the type — which is the whole reason this shape hides —
    /// so filtering on the type name would make this check blind to precisely its own subject.
    /// </summary>
    internal static int TargetTypedIn(string text) =>
        text.Contains("Messages", StringComparison.Ordinal)
            ? TargetTypedPattern.Matches(SourceScan.MaskCommentsAndStrings(text)).Count
            : 0;

    internal static int UnkeyedIn(string text)
    {
        var (total, keyed) = CountIn(text);
        return total - keyed;
    }

    /// <summary>
    /// The matcher itself, over TEXT rather than a path — so
    /// <see cref="TheMatcherSeesEverySpellingItClaimsTo"/> can pin what it recognises without needing
    /// a live example of every spelling to survive in the tree. Once a spelling reaches zero
    /// occurrences the tree stops being evidence that the matcher still sees it, and a narrowed
    /// pattern would then pass unnoticed — which is the failure this whole guard exists to prevent,
    /// one level up.
    /// </summary>
    internal static (int Total, int Keyed) CountIn(string text)
    {
        if (!text.Contains(MarkerPreFilter, StringComparison.Ordinal)) return (0, 0);

        var code = SourceScan.MaskCommentsAndStrings(text);
        var total = 0;
        var keyed = 0;
        foreach (Match marker in MarkerPattern.Matches(code))
        {
            var open = code.IndexOf('(', marker.Index + marker.Length - 1);
            if (open < 0) continue;
            var close = MatchingClose(code, open);
            if (close < 0) continue;
            total++;
            if (IsKeyedAfter(code, close)) keyed++;
        }

        return (total, keyed);
    }

    /// <summary>
    /// Whether a <c>.WithKey(</c> follows the constructor's closing paren — allowing an object
    /// initializer in between (<c>new LogMessage(…) { Scopes = […] }.WithKey(…)</c>), which is a real
    /// site shape, and any whitespace, which is how the call is usually wrapped.
    /// </summary>
    private static bool IsKeyedAfter(string code, int close)
    {
        var i = SkipWhitespace(code, close + 1);
        if (i < code.Length && code[i] == '{')
        {
            var end = MatchingClose(code, i);
            if (end < 0) return false;
            i = SkipWhitespace(code, end + 1);
        }

        return string.CompareOrdinal(code, i, ".WithKey(", 0, ".WithKey(".Length) == 0;
    }

    private static int SkipWhitespace(string code, int i)
    {
        while (i < code.Length && char.IsWhiteSpace(code[i])) i++;
        return i;
    }

    /// <summary>The index of the bracket closing the one at <paramref name="open"/>, or -1.</summary>
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
                    if (--depth == 0) return i;
                    break;
            }
        }

        return -1;
    }
}
