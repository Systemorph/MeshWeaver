using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// Governance ratchet for the defect the maintainer named on 2026-09-06 with the word
/// <i>"again"</i>: <c>async</c>/<c>await</c>/<c>Task&lt;T&gt;</c> in hub-reachable code (#3433).
///
/// <para><b>Why "again" — nothing banned the shape.</b> AGENTS.md states the rule three times and
/// absolutely, but every guard that enforces it enforces a specific IDIOM, not the shape:
/// <see cref="ObservableToTaskBridgeGuard"/> matches a hand-rolled Rx→<c>Task</c> bridge and
/// <c>.ToTask(</c>; <see cref="BlockingBridgeInTestRatchetGuard"/> and its production sibling match
/// <c>.Result</c>/<c>.Wait()</c>; <see cref="HandWovenGateRatchetGuard"/> matches gates;
/// <see cref="HubDisposalJoinRatchetGuard"/> scans a different subject entirely.
/// <c>EaGraphAuth.LoadAsync</c> was none of those. It was a plain
/// <c>await ws.GetMeshNodeStream(path).Take(1).Timeout(10s).FirstAsync().ObserveCompletion(…)</c> —
/// a SANCTIONED bridge, awaited from code an agent tool reaches — and every guard was green.</para>
///
/// <para><b>The honest difficulty, stated rather than designed around.</b> "Hub-reachable" is not a
/// syntactic property. A guard that simply banned <c>async</c> under <c>memex/</c> would red the EA
/// consent controller, which is a genuine ASP.NET boundary and legitimately Task-shaped. So this
/// guard picks two properties that ARE syntactic and that together cover the mechanism:</para>
///
/// <list type="number">
/// <item><b><see cref="NoNewAwaitOfAMeshRead"/></b> — the narrow one, and the one that reds on a
/// reintroduced line 197. A method that <c>await</c>s a mesh read or write is hub-reachable BY
/// CONSTRUCTION: whatever it is called from, the read's reply has to be processed by a hub, and the
/// hub's turn loop (<c>MessageService.DrainOne</c>) subscribes to one turn at a time and does not
/// dequeue the next until the current one terminates. A caller that cannot finish until the read
/// finishes therefore holds the queue the reply must travel through. Two ASP.NET-boundary sites
/// carry the shape legitimately and are seeded — see the allow file, which states the boundary for
/// each.</item>
/// <item><b><see cref="NoNewTaskShapedSeamInTheMeshContract"/></b> — the ROOT. A <c>Task</c> on an
/// interface in <c>MeshWeaver.Mesh.Contract</c> does not merely permit an await at the call site,
/// it FORCES one: a <c>Task&lt;T&gt;</c> has exactly one consumption idiom. That assembly exists so
/// hub-side modules and agent tools can consume host capabilities — <see cref="System.IObservable{T}"/>
/// is the only shape both a hub turn and an HTTP action can consume, and the HTTP side bridges once,
/// at its own edge.</item>
/// </list>
///
/// <para><b>Falsified, not asserted.</b> Both rules were watched RED on the shape they name and
/// green with it removed, before this file was committed —
/// <see cref="TheMatcherSeesEverySpellingItClaimsTo"/> and
/// <see cref="TheSeamMatcherSeesWhatItClaimsTo"/> re-run that experiment on every CI run against
/// planted text, so a matcher that silently stops seeing the shape fails here rather than reporting
/// a clean tree. <see cref="SourceScan.SourceFiles"/> additionally throws on an empty scan (#2844),
/// so "found nothing" can never read as "nothing to find".</para>
///
/// <para><b>A ratchet may only SHRINK.</b> A new file, a raised count, or a raised TOTAL is a
/// failure. A line that has become stale is REPORTED, not failed: two PRs closing sites
/// concurrently would otherwise red <c>main</c> on whichever merged second.</para>
/// </summary>
public class HubReachableAsyncGuard(ITestOutputHelper output)
{
    /// <summary>
    /// Production trees. <c>test/</c> is deliberately out of scope: a test that awaits a mesh read
    /// from its own (non-hub) thread is how the suite observes the mesh at all, and
    /// <c>EaCredentialReadTest</c> deliberately builds a turn that holds the drain in order to
    /// reproduce #3433. Listing them would make the ratchet a list of things it wants.
    /// </summary>
    private static readonly string[] ScannedRoots = ["src", "memex", "tools", "samples", "clients"];

    /// <summary>The seeded inventory's size for <see cref="NoNewAwaitOfAMeshRead"/>.</summary>
    private const int AwaitedReadTotalBudget = 2;

    /// <summary>The seeded inventory's size for <see cref="NoNewTaskShapedSeamInTheMeshContract"/>.</summary>
    private const int SeamTotalBudget = 8;

    private const string AwaitedReadAllowFile = "AwaitedMeshReadSites.allow";
    private const string SeamAllowFile = "TaskShapedMeshSeams.allow";

    /// <summary>The one assembly whose interfaces are consumed by hub-side modules and agent tools.</summary>
    private const string MeshContractProject = "src/MeshWeaver.Mesh.Contract";

    /// <summary>
    /// 🚨 THE LOAD-BEARING LIST for rule 1. An <c>await</c> counts only if its expression mentions
    /// one of these, so a mesh entry point named after NONE of them is invisible to this guard and
    /// the whole shape passes as zero. That is not hypothetical — it is exactly how
    /// <see cref="ImpersonationScopeSiteRatchetGuard"/>'s factory list hid 19 sites until #2441.
    /// <b>A new way to read or write the mesh must be added here in the same change that introduces
    /// it.</b> Every entry names an operation whose completion depends on a hub processing a
    /// message, and that is the whole criterion.
    /// </summary>
    private static readonly string[] MeshOperations =
    [
        "GetMeshNodeStream",   // the single-node read/write handle — line 197's own call
        "GetWorkspace(",       // the hop to it
        "GetDataStream",       // the typed data-source stream
        "GetRemoteStream",     // the cross-hub primitive underneath both
        "IMeshService",        // the query/lifecycle service, by type…
        "meshService.",        // …and by the name every call site gives it
        "ObserveQuery",
        "GetQuery(",
        ".Query(",
        ".CreateNode(",
        ".UpdateNode(",
        ".DeleteNode(",
        ".CopyNode(",
    ];

    /// <summary>
    /// An <c>await</c> keyword. Word-bounded on both sides so <c>awaited</c>, <c>Await</c> and a
    /// member named <c>…await…</c> are not matched.
    /// </summary>
    private static readonly Regex AwaitPattern =
        new(@"\bawait\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// A <c>Task</c>/<c>ValueTask</c>-returning member DECLARATION — the return type, an optional
    /// generic argument list, a member name, an optional type-parameter list, then the parameter
    /// list. Matched only inside an interface body (see <see cref="SeamMembersIn"/>), so a class's
    /// own Task-returning helper is out of scope by construction: the defect is the CONTRACT that
    /// forces a caller's hand, not every method that happens to be async.
    /// </summary>
    private static readonly Regex SeamMemberPattern = new(
        @"\b(?:Task|ValueTask)\s*(?:<[^;{=]*?>)?\s+[A-Za-z_]\w*\s*(?:<[^>(]*>)?\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex InterfaceDeclarationPattern = new(
        @"\binterface\s+(\w+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // ── Rule 1: awaiting a mesh read ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 🚨 <c>await &lt;a mesh read&gt;</c> may not appear at a NEW site.
    ///
    /// <para>The mechanism, measured rather than asserted (<c>EaCredentialReadTest</c>): the hub's
    /// turn loop subscribes to one turn at a time and re-schedules the drain only from that turn's
    /// terminal callback. A caller whose work does not finish until a cross-hub read finishes
    /// therefore holds the very queue the read's reply must pass through. The read then cannot
    /// complete, its <c>Timeout</c> fires, and whatever the caller does with that timeout is the
    /// user-visible bug — for #3433, telling a connected user to connect again.</para>
    ///
    /// <para><b>The fix at a site</b> is to compose and <c>Subscribe</c>: return
    /// <see cref="System.IObservable{T}"/>, chain with <c>.Select</c>/<c>.SelectMany</c>/
    /// <c>.Timeout</c>, and post the answer from the subscription. Where an external signature
    /// genuinely forces a <c>Task</c> — an MVC action, an <c>IHostedService</c> — the bridge belongs
    /// at THAT edge (<c>ReactiveCompletion.ObserveCompletion</c>) and must not be pushed back onto
    /// the seam the hub side also consumes.</para>
    /// </summary>
    [Fact]
    public void NoNewAwaitOfAMeshRead()
    {
        var root = SourceScan.FindRepoRoot();
        var allowed = SourceScan.ReadAllowFile(
            Path.Combine(root, "test", AwaitedReadAllowFile), AwaitedReadAllowFile);
        var found = ScanAwaitedReads(root);

        var failures = new List<string>();
        foreach (var (file, count) in found.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!allowed.TryGetValue(file, out var budget))
                failures.Add(
                    $"  NEW SITE   {file} ({count}) — this awaits a mesh read, which makes it "
                    + "hub-reachable by construction and therefore a deadlock (#3433). Compose "
                    + "reactively and Subscribe; if an external signature forces a Task, bridge at "
                    + "THAT edge with ObserveCompletion. Do NOT add a line to "
                    + AwaitedReadAllowFile + ".");
            else if (count > budget)
                failures.Add(
                    $"  MORE       {file} ({count} > {budget} allowed) — a site was ADDED to a file "
                    + "that already carries the shape.");
        }

        var total = allowed.Values.Sum();
        if (total > AwaitedReadTotalBudget)
            failures.Add(
                $"  TOTAL      {total} allowances > {AwaitedReadTotalBudget} budgeted — the "
                + "inventory GREW. Adding a line to " + AwaitedReadAllowFile + " is not a fix.");

        ReportStale(found, allowed, AwaitedReadAllowFile, nameof(AwaitedReadTotalBudget));

        Assert.True(failures.Count == 0,
            "Awaiting a mesh read parks whatever is waiting on the caller, and the hub's turn loop "
            + "runs ONE turn at a time — so the reply the await is waiting for queues behind the "
            + "wait itself. #3433 shipped exactly this and rendered the timeout as 'this user never "
            + "connected'.\n"
            + string.Join("\n", failures));
    }

    // ── Rule 2: a Task-shaped seam in the mesh contract ──────────────────────────────────────────

    /// <summary>
    /// 🚨 An interface in <c>MeshWeaver.Mesh.Contract</c> may not declare a NEW
    /// <c>Task</c>/<c>ValueTask</c>-returning member.
    ///
    /// <para>This is rule 1's cause. <c>IEaGraphAuth</c> declared three <c>Task</c> members, and a
    /// <c>Task&lt;T&gt;</c> has exactly one consumption idiom — so the agent-tool caller in
    /// MeshWeaver.Plugins had no choice but to <c>await</c> it. The seam did not permit the defect,
    /// it required it. That assembly is deliberately the SDK-free contract hub-side modules compile
    /// against; <see cref="System.IObservable{T}"/> is the one shape both a hub turn and an ASP.NET
    /// action can consume, because only the second of those can bridge.</para>
    ///
    /// <para>Scope is the DECLARATION, not the implementation: a class in that assembly may still
    /// have Task-returning internals (<c>IIoPool</c> takes and returns them by design — it IS the
    /// sanctioned async boundary), and this rule does not look at classes at all.</para>
    /// </summary>
    [Fact]
    public void NoNewTaskShapedSeamInTheMeshContract()
    {
        var root = SourceScan.FindRepoRoot();
        var allowed = SourceScan.ReadAllowFile(
            Path.Combine(root, "test", SeamAllowFile), SeamAllowFile);
        var found = ScanContractSeams(root);

        var failures = new List<string>();
        foreach (var (file, count) in found.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!allowed.TryGetValue(file, out var budget))
                failures.Add(
                    $"  NEW SEAM   {file} ({count}) — a Task-returning member on a mesh-contract "
                    + "interface forces every hub-side consumer to await it. Return "
                    + "IObservable<T>; let an HTTP/SDK consumer bridge at its own edge. Do NOT add "
                    + "a line to " + SeamAllowFile + ".");
            else if (count > budget)
                failures.Add(
                    $"  MORE       {file} ({count} > {budget} allowed) — a member was ADDED to a "
                    + "seam that already carries the shape.");
        }

        var total = allowed.Values.Sum();
        if (total > SeamTotalBudget)
            failures.Add(
                $"  TOTAL      {total} allowances > {SeamTotalBudget} budgeted — the inventory "
                + "GREW. Adding a line to " + SeamAllowFile + " is not a fix.");

        ReportStale(found, allowed, SeamAllowFile, nameof(SeamTotalBudget));

        Assert.True(failures.Count == 0,
            "A Task-returning member on an interface in " + MeshContractProject + " is what FORCED "
            + "the await that #3433 is about: the mesh contract is compiled against by hub-side "
            + "modules and agent tools, and a Task can only be consumed by awaiting it.\n"
            + string.Join("\n", failures));
    }

    // ── Non-vacuity ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 🚨 The experiment this guard was falsified with, re-run every CI run against planted text:
    /// each marker is shown a site it MUST see and a lookalike it must NOT. A matcher that silently
    /// stopped matching would otherwise report a clean tree — the failure mode that makes a guard
    /// worse than none, because it reads as evidence.
    /// </summary>
    [Fact]
    public void TheMatcherSeesEverySpellingItClaimsTo()
    {
        // 🚨 LINE 197 ITSELF — verbatim from the revision that shipped the defect. This is the
        // assertion the guard was watched RED on: with EaGraphAuth reverted, the file it lives in
        // is not in the allow file and NoNewAwaitOfAMeshRead fails naming it.
        Assert.Equal(1, CountAwaitedReadsIn(
            "node = await ws.GetMeshNodeStream(PathFor(userObjectId))\n"
            + "    .Take(1).Timeout(TimeSpan.FromSeconds(10)).FirstAsync()\n"
            + "    .ObserveCompletion(ex => logger?.LogWarning(ex, \"failed\"), ct);"));

        // The plain shapes, one per marker family.
        Assert.Equal(1, CountAwaitedReadsIn("var n = await hub.GetMeshNodeStream(path).FirstAsync();"));
        Assert.Equal(1, CountAwaitedReadsIn("var w = await hub.GetWorkspace().GetStream().FirstAsync();"));
        Assert.Equal(1, CountAwaitedReadsIn("var s = await ws.GetDataStream<T>(r).FirstAsync();"));
        Assert.Equal(1, CountAwaitedReadsIn("var s = await ws.GetRemoteStream<MeshNode, R>(a, r).FirstAsync();"));
        Assert.Equal(1, CountAwaitedReadsIn("var q = await meshService.Query<MeshNode>(request).FirstAsync();"));
        Assert.Equal(1, CountAwaitedReadsIn("var q = await sp.GetRequiredService<IMeshService>().Query(r).FirstAsync();"));
        Assert.Equal(1, CountAwaitedReadsIn("var q = await ws.ObserveQuery(request).FirstAsync();"));
        Assert.Equal(1, CountAwaitedReadsIn("var q = await ws.GetQuery(request).FirstAsync();"));
        Assert.Equal(1, CountAwaitedReadsIn("await svc.CreateNode(node).FirstAsync();"));
        Assert.Equal(1, CountAwaitedReadsIn("await svc.UpdateNode(node).FirstAsync();"));
        Assert.Equal(1, CountAwaitedReadsIn("await svc.DeleteNode(path).FirstAsync();"));
        Assert.Equal(1, CountAwaitedReadsIn("await svc.CopyNode(a, b).FirstAsync();"));

        // Spelled across lines — the form a single-line grep cannot see.
        Assert.Equal(1, CountAwaitedReadsIn(
            "var node = await workspace\n    .GetMeshNodeStream(path)\n    .Take(1)\n    .FirstAsync();"));

        // …and what it must NOT see, so widening never becomes matching everything.
        Assert.Equal(0, CountAwaitedReadsIn("await httpClient.GetStringAsync(url);"));
        Assert.Equal(0, CountAwaitedReadsIn(
            "// await ws.GetMeshNodeStream(path).FirstAsync() is the #3433 defect."));
        Assert.Equal(0, CountAwaitedReadsIn(
            "var doc = \"await ws.GetMeshNodeStream(path).FirstAsync()\";"));
        // The sanctioned shape: compose and Subscribe, no await anywhere near the read.
        Assert.Equal(0, CountAwaitedReadsIn(
            "ws.GetMeshNodeStream(path).Take(1).Subscribe(n => Use(n), ex => Log(ex));"));
        // An await BEFORE the read's statement, on unrelated work, is not this shape.
        Assert.Equal(0, CountAwaitedReadsIn(
            "await SomethingElse();\nws.GetMeshNodeStream(path).Subscribe(n => Use(n));"));
    }

    /// <summary>
    /// The same experiment for rule 2: the seam matcher must see a Task member on an interface, and
    /// must not see one on a class, in a comment, or as a parameter type.
    /// </summary>
    [Fact]
    public void TheSeamMatcherSeesWhatItClaimsTo()
    {
        // The three members #3433's seam declared, verbatim.
        Assert.Equal(3, CountSeamMembersIn(
            "public interface IEaGraphAuth\n{\n"
            + "    Task<bool> ExchangeAndStoreAsync(string code, string uri, string user, CancellationToken ct);\n"
            + "    Task<string?> GetAccessTokenAsync(string userObjectId, CancellationToken ct);\n"
            + "    Task<bool> IsConnectedAsync(string userObjectId, CancellationToken ct);\n"
            + "}"));
        Assert.Equal(1, CountSeamMembersIn(
            "public interface IThing\n{\n    ValueTask DoAsync(CancellationToken ct);\n}"));
        Assert.Equal(1, CountSeamMembersIn(
            "public interface IThing\n{\n    Task<IReadOnlyList<string>> ListAsync<TKey>(TKey k);\n}"));

        // A CLASS is not a seam — this rule is about the contract that forces a caller's hand.
        Assert.Equal(0, CountSeamMembersIn(
            "public sealed class Thing\n{\n    public Task<bool> DoAsync() => Task.FromResult(true);\n}"));
        // The reactive shape the rule is asking for.
        Assert.Equal(0, CountSeamMembersIn(
            "public interface IThing\n{\n    IObservable<bool> Do(string id);\n}"));
        // A Task as a PARAMETER, not a return type.
        Assert.Equal(0, CountSeamMembersIn(
            "public interface IThing\n{\n    IObservable<bool> Observe(Task<bool> pending);\n}"));
        // Prose quoting the shape — every remark in this repo does that.
        Assert.Equal(0, CountSeamMembersIn(
            "public interface IThing\n{\n    // Task<bool> DoAsync(CancellationToken ct) was the defect.\n}"));
    }

    /// <summary>
    /// Non-vacuity against the REAL tree, not planted text: both scanners must find the seeded
    /// inventory they ratchet. A scanner pointed at the wrong tree, or broken by a masking change,
    /// would otherwise report every allow-file line as stale and pass — the #2844 shape, one level
    /// up from the empty-scan check <see cref="SourceScan.SourceFiles"/> already makes.
    /// </summary>
    [Fact]
    public void BothScannersFindTheInventoryTheyRatchet()
    {
        var root = SourceScan.FindRepoRoot();

        Assert.True(ScanAwaitedReads(root).Count > 0,
            "The awaited-read scanner found NO site anywhere under "
            + string.Join(", ", ScannedRoots) + ". Either every site was migrated — in which case "
            + "empty " + AwaitedReadAllowFile + ", drop " + nameof(AwaitedReadTotalBudget)
            + " to 0 and delete this half of the assertion — or the scanner is broken, which would "
            + "make the ratchet pass on no evidence.");

        Assert.True(ScanContractSeams(root).Count > 0,
            "The mesh-contract seam scanner found NO Task-returning interface member under "
            + MeshContractProject + ", while " + SeamAllowFile + " lists some. That is a broken "
            + "scanner, not a clean contract.");
    }

    // ── Scanning ─────────────────────────────────────────────────────────────────────────────────

    private static Dictionary<string, int> ScanAwaitedReads(string root) =>
        SourceScan.SourceFiles(root, ScannedRoots)
            .Select(f => (Relative: SourceScan.Relative(root, f),
                          Count: CountAwaitedReadsIn(File.ReadAllText(f))))
            .Where(x => x.Count > 0)
            .ToDictionary(x => x.Relative, x => x.Count, StringComparer.Ordinal);

    private static Dictionary<string, int> ScanContractSeams(string root) =>
        SourceScan.SourceFiles(root, [MeshContractProject])
            .Select(f => (Relative: SourceScan.Relative(root, f),
                          Count: CountSeamMembersIn(File.ReadAllText(f))))
            .Where(x => x.Count > 0)
            .ToDictionary(x => x.Relative, x => x.Count, StringComparer.Ordinal);

    /// <summary>
    /// Counts <c>await</c>s whose expression touches the mesh. The expression is taken from the
    /// keyword to the next <c>;</c> — statement-scoped, so a mesh call in a LATER statement is not
    /// attributed to an earlier await. That cut is deliberately conservative: an await whose
    /// statement contains an inner <c>;</c> (a statement lambda) is truncated and may be missed, and
    /// a false NEGATIVE only fails to catch a site, whereas a false positive would red legitimate
    /// code and get the whole rule suppressed.
    /// </summary>
    internal static int CountAwaitedReadsIn(string source)
    {
        var masked = SourceScan.MaskCommentsAndStrings(source);
        var count = 0;
        foreach (Match match in AwaitPattern.Matches(masked))
        {
            var end = masked.IndexOf(';', match.Index);
            var expression = end < 0 ? masked[match.Index..] : masked[match.Index..end];
            if (MeshOperations.Any(op => expression.Contains(op, StringComparison.Ordinal)))
                count++;
        }
        return count;
    }

    /// <summary>Counts Task/ValueTask-returning members declared inside an interface body.</summary>
    internal static int CountSeamMembersIn(string source) =>
        SeamMembersIn(SourceScan.MaskCommentsAndStrings(source));

    private static int SeamMembersIn(string masked)
    {
        var count = 0;
        foreach (Match declaration in InterfaceDeclarationPattern.Matches(masked))
        {
            var open = masked.IndexOf('{', declaration.Index + declaration.Length);
            if (open < 0) continue;

            var depth = 0;
            var i = open;
            for (; i < masked.Length; i++)
            {
                if (masked[i] == '{') depth++;
                else if (masked[i] == '}' && --depth == 0) break;
            }

            count += SeamMemberPattern.Matches(masked[open..Math.Min(i, masked.Length)]).Count;
        }
        return count;
    }

    private void ReportStale(
        Dictionary<string, int> found, Dictionary<string, int> allowed, string allowFile, string budgetName)
    {
        foreach (var (file, budget) in allowed.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var count = found.GetValueOrDefault(file, 0);
            if (count < budget)
                output.WriteLine(
                    $"STALE (please tidy) in {allowFile}: {file} — {count} found, {budget} allowed. "
                    + $"{(count == 0 ? "Delete the line" : $"Lower it to {count}")} and lower "
                    + $"{budgetName} by {budget - count}.");
        }
    }
}
