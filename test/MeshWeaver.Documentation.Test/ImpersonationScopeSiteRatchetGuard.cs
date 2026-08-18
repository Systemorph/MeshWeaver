using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// Governance ratchet for the identity-latch defect class (#1790): the shape
/// <c>Observable.Using(() =&gt; access.ImpersonateAsSystem(), _ =&gt; work)</c> — or the same with
/// <c>ImpersonateAsHub</c> / <c>SwitchAccessContext</c> — may not appear at a NEW site.
///
/// <para><b>Why the shape is a defect and not a style.</b> Impersonation is an <c>AsyncLocal</c>
/// store/restore pair. Rx runs <c>Using</c>'s resource factory on the SUBSCRIBING thread and
/// disposes the resource when the inner observable TERMINATES — for a cross-hub request/response,
/// the owning hub's response thread. The store and the restore therefore land on different threads,
/// and the subscriber is left running as the impersonated identity: an ASP.NET request that
/// continues with <c>Permission.All</c>, a script run whose remaining statements resolve areas the
/// submitting user may not read, a hub action block that keeps <c>system-security</c>.</para>
///
/// <para><b>The fix at a site</b> is <c>access.RunAsSystem(() =&gt; work)</c> /
/// <c>RunAsHub(hub, …)</c> / <c>RunAs(identity, …)</c> (<c>ImpersonationScopeExtensions</c>), which
/// opens the scope at Subscribe — so the cold work is still issued impersonated, and anything it
/// schedules captures that ExecutionContext — and closes it again on the way OUT of that same
/// Subscribe. Compose the widest cold pipeline inside the <c>work</c> factory and the emission-time
/// behaviour is unchanged. Where the capture is genuinely synchronous, a plain <c>using</c> around
/// the call and its <c>Subscribe</c> is equally correct (<c>ActivityLogLogger</c>).</para>
///
/// <para><b>Why the file is seeded rather than empty.</b> The shape was the framework's idiom for
/// two years; <see cref="AllowFileName"/> is the inventory of what still carries it, so that the
/// NEXT one fails here instead of being discovered in production. The
/// <see cref="MeshWeaver.Messaging.AccessService"/> fix already closes the second half of the leak
/// at every listed site — a scope disposed on a thread that never opened it now writes nothing, so
/// no foreign identity is injected anywhere — which is why the remaining entries are debt rather
/// than live incidents. What is left at each of them is the subscribing-thread latch, and that only
/// the call site can close.</para>
///
/// <para><b>The ratchet may only SHRINK.</b> A new file, a raised count, or a raised TOTAL is a
/// failure. A line that has become stale (its site was fixed) is reported, not failed: two PRs
/// closing sites concurrently would otherwise red <c>main</c> on whichever merged second, and a
/// gate that punishes the direction it is asking for teaches people to stop shrinking. Delete the
/// stale line and lower <see cref="TotalBudget"/> in the same change.</para>
/// </summary>
public class ImpersonationScopeSiteRatchetGuard(ITestOutputHelper output)
{
    /// <summary>
    /// The seeded inventory's size. Per-file entries stop a new site in a file that already carries
    /// the shape; this stops the list as a WHOLE from growing — including by the trick of adding a
    /// new file's line. Lower it whenever you delete or lower an entry.
    /// </summary>
    private const int TotalBudget = 98;

    /// <summary>Production roots. <c>test/</c> and <c>samples/</c> are deliberately out of scope —
    /// the leak there costs test isolation, not a user's permissions, and listing 21 more entries
    /// would bury the ones that matter.</summary>
    private static readonly string[] ScannedRoots = ["src", "memex", "tools"];

    private const string Marker = "Observable.Using";

    /// <summary>The identity-scope factories. All three return an <see cref="IDisposable"/> whose
    /// Dispose writes an <c>AsyncLocal</c>, which is what makes them thread-affine.</summary>
    private static readonly string[] ScopeFactories = ["Impersonate", "SwitchAccessContext"];

    private const string AllowFileName = "ImpersonationScopeSites.allow";

    private static readonly string[] ExcludedSegments =
        ["bin", "obj", "node_modules", "TestResults", ".git", ".vs", "dist"];

    [Fact]
    public void NoNewSiteOpensAnImpersonationScopeInsideObservableUsing()
    {
        var root = FindRepoRoot();
        var allowed = ReadAllowFile(Path.Combine(root, "test", AllowFileName));
        var found = Scan(root);

        var failures = new List<string>();

        foreach (var (file, count) in found.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!allowed.TryGetValue(file, out var budget))
                failures.Add(
                    $"  NEW SITE   {file} ({count}) — replace Observable.Using(…Impersonate…) with "
                    + "AccessService.RunAsSystem/RunAsHub/RunAs, or a synchronous `using` around the "
                    + "call and its Subscribe. Do NOT add a line to " + AllowFileName + ".");
            else if (count > budget)
                failures.Add(
                    $"  MORE       {file} ({count} > {budget} allowed) — a site was ADDED to a file "
                    + "that already carries the shape.");
        }

        var total = allowed.Values.Sum();
        if (total > TotalBudget)
            failures.Add(
                $"  TOTAL      {total} allowances > {TotalBudget} budgeted — the inventory GREW. "
                + "Adding a line to " + AllowFileName + " is not a fix.");

        // Stale entries are reported, never failed: shrinking is the direction this guard exists to
        // encourage, and failing on it would red main whenever two PRs close sites concurrently.
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
            "The identity-latch shape `Observable.Using(() => access.Impersonate…(), _ => work)` "
            + "opens an AsyncLocal scope on the SUBSCRIBING thread and disposes it on whichever "
            + "thread the work terminates — leaving the subscriber running as the impersonated "
            + "identity (#1790). Use ImpersonationScopeExtensions.RunAsSystem/RunAsHub/RunAs.\n"
            + string.Join("\n", failures));
    }

    /// <summary>
    /// Non-vacuity, pinned in the same run: the scanner must actually SEE the shape. The seeded
    /// allow file is non-empty, so a scanner that silently matched nothing — a renamed marker, a
    /// masking bug that blanked every file — would report every entry as STALE rather than pass;
    /// this states the expectation directly so the failure names the scanner instead of the tree.
    /// </summary>
    [Fact]
    public void TheScannerFindsTheShapeItIsRatcheting()
    {
        var root = FindRepoRoot();
        var found = Scan(root);

        Assert.True(found.Count > 0,
            "The scanner found no occurrence of the shape anywhere under "
            + string.Join(", ", ScannedRoots)
            + ". Either every site was migrated — in which case empty "
            + AllowFileName + " and delete this assertion — or the scanner is broken, which would "
            + "make the ratchet above pass on no evidence.");

        // The doc comments in ImpersonationScopeExtensions and AccessService QUOTE the shape. A
        // scanner that counted them would ratchet against prose, so prove the masking works.
        var seam = Path.Combine(root, "src", "MeshWeaver.Messaging.Hub", "ImpersonationScopeExtensions.cs");
        Assert.True(File.Exists(seam), "the seam file must exist for this check to mean anything");
        Assert.True(File.ReadAllText(seam).Contains("Observable.Using(access.ImpersonateAsSystem"),
            "the seam's remarks must still quote the shape — otherwise this check proves nothing");
        Assert.False(found.ContainsKey(Path.Combine("src", "MeshWeaver.Messaging.Hub", "ImpersonationScopeExtensions.cs")
                .Replace(Path.DirectorySeparatorChar, '/')),
            "the scanner counted a DOC COMMENT as a call site — comment masking is broken, and every "
            + "count in the allow file is therefore unreliable.");
    }

    private static Dictionary<string, int> Scan(string root) =>
        ScannedRoots
            .Select(r => Path.Combine(root, r))
            .Where(Directory.Exists)
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            .Where(f => Path.GetExtension(f) is ".cs" or ".razor")
            .Where(f => !IsExcluded(root, f))
            .Select(f => (Relative: Relative(root, f), Count: CountSites(f)))
            .Where(x => x.Count > 0)
            .ToDictionary(x => x.Relative, x => x.Count, StringComparer.Ordinal);

    /// <summary>
    /// Occurrences of <see cref="Marker"/> whose FIRST argument — the resource factory — names one
    /// of <see cref="ScopeFactories"/>. Comments and string literals are masked first: the seam's
    /// own remarks quote the shape verbatim, and counting prose would make the ratchet meaningless.
    /// </summary>
    private static int CountSites(string path)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch (IOException) { return 0; } // a file a concurrent build is writing is not evidence

        if (!text.Contains(Marker, StringComparison.Ordinal)) return 0;

        var code = MaskCommentsAndStrings(text);
        var count = 0;
        var at = 0;
        while ((at = code.IndexOf(Marker, at, StringComparison.Ordinal)) >= 0)
        {
            var open = code.IndexOf('(', at + Marker.Length);
            at += Marker.Length;
            if (open < 0) continue;
            var firstArg = FirstArgument(code, open);
            if (ScopeFactories.Any(f => firstArg.Contains(f, StringComparison.Ordinal)))
                count++;
        }

        return count;
    }

    /// <summary>The text between <paramref name="openParen"/> and the first comma at its own nesting
    /// depth (or the matching close paren) — i.e. the call's first argument.</summary>
    private static string FirstArgument(string code, int openParen)
    {
        var depth = 0;
        for (var i = openParen; i < code.Length; i++)
        {
            switch (code[i])
            {
                case '(' or '[' or '{':
                    depth++;
                    break;
                case ')' or ']' or '}':
                    if (--depth == 0) return code[(openParen + 1)..i];
                    break;
                case ',' when depth == 1:
                    return code[(openParen + 1)..i];
            }
        }

        return code[(openParen + 1)..];
    }

    /// <summary>
    /// Blanks comment and string-literal characters, preserving offsets and newlines, so the scan
    /// sees code only. Handles line/block comments, ordinary, verbatim and raw string literals, and
    /// character literals — enough that a doc comment containing <c>Plugins/*</c> does not swallow
    /// the next thousand lines (which is exactly what a naive scanner did).
    /// </summary>
    private static string MaskCommentsAndStrings(string text)
    {
        var masked = new StringBuilder(text);
        var i = 0;
        var n = text.Length;

        void Blank(int from, int to)
        {
            for (var k = from; k < to && k < n; k++)
                if (text[k] != '\n')
                    masked[k] = ' ';
        }

        while (i < n)
        {
            var c = text[i];
            var next = i + 1 < n ? text[i + 1] : '\0';

            if (c == '/' && next == '/')
            {
                var end = text.IndexOf('\n', i);
                end = end < 0 ? n : end;
                Blank(i, end);
                i = end;
            }
            else if (c == '/' && next == '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? n : end + 2;
                Blank(i, end);
                i = end;
            }
            else if (c == '"' && i + 2 < n && text[i + 1] == '"' && text[i + 2] == '"')
            {
                var end = text.IndexOf("\"\"\"", i + 3, StringComparison.Ordinal);
                end = end < 0 ? n : end + 3;
                Blank(i, end);
                i = end;
            }
            else if (c == '@' && next == '"')
            {
                i = MaskVerbatim(text, masked, i + 1, Blank);
            }
            else if (c == '$' && next == '@' && i + 2 < n && text[i + 2] == '"')
            {
                Blank(i, i + 2);
                i = MaskVerbatim(text, masked, i + 2, Blank);
            }
            else if (c is '"' or '\'')
            {
                var quote = c;
                Blank(i, i + 1);
                i++;
                while (i < n && text[i] != quote && text[i] != '\n')
                {
                    var step = text[i] == '\\' ? 2 : 1;
                    Blank(i, i + step);
                    i += step;
                }

                if (i < n && text[i] == quote) { Blank(i, i + 1); i++; }
            }
            else
            {
                i++;
            }
        }

        return masked.ToString();
    }

    /// <summary>Masks a verbatim string starting at its opening quote; <c>""</c> is an escaped quote.</summary>
    private static int MaskVerbatim(string text, StringBuilder masked, int quote, Action<int, int> blank)
    {
        var n = text.Length;
        blank(quote, quote + 1);
        var i = quote + 1;
        while (i < n)
        {
            if (text[i] == '"')
            {
                if (i + 1 < n && text[i + 1] == '"') { blank(i, i + 2); i += 2; continue; }
                blank(i, i + 1);
                return i + 1;
            }

            blank(i, i + 1);
            i++;
        }

        return n;
    }

    /// <summary>
    /// <c>relative/path.cs&lt;TAB&gt;count</c>, one per line; <c>#</c> starts a comment.
    /// </summary>
    private static Dictionary<string, int> ReadAllowFile(string path)
    {
        Assert.True(File.Exists(path),
            $"{AllowFileName} is missing — without it this guard cannot tell a pre-existing site "
            + "from a new one and would report every occurrence as a failure. Restore it from git "
            + "rather than regenerating it: a regenerated file would silently bless whatever is in "
            + "the tree, which is the one thing a ratchet must never do.");

        return File.ReadAllLines(path)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l.Split('\t', StringSplitOptions.TrimEntries))
            .ToDictionary(parts => parts[0], parts => int.Parse(parts[1]), StringComparer.Ordinal);
    }

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    private static bool IsExcluded(string root, string path) =>
        Relative(root, path).Split('/').Any(s => ExcludedSegments.Contains(s, StringComparer.OrdinalIgnoreCase));

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
