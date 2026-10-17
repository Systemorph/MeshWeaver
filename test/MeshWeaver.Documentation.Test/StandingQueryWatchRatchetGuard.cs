using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 A STANDING watch over a live mesh QUERY must survive a fault of that query. The query fan-in
/// terminates a live query on a stalled provider (<c>QueryProviderStalledException</c>) or a dropped
/// connection, and a watch whose only answer is an error arm that LOGS is then dead until the
/// process restarts — no further emission, and usually one log line as the only trace. Measured on
/// five sites in one sweep: the instance-secret catalog (every per-sender key refused until a
/// restart), the UI-contribution catalog, both event-subscription queries, and the image-closure
/// inbox.
///
/// <para><b>What the guard looks for.</b> A <c>.Subscribe(</c> statement whose receiver chain opens
/// a live query (<c>.Query&lt;</c>, <c>GetQuery(</c>, <c>ObserveQuery</c>) and is NOT one of the
/// shapes that are fine:</para>
/// <list type="bullet">
///   <item>a ONE-SHOT read — <c>Take</c>, <c>FirstAsync</c>, <c>LastAsync</c>,
///   <c>FirstOrDefaultAsync</c>, <c>SingleAsync</c> or <c>Timeout</c> anywhere in the chain, or
///   <c>ToList</c>/<c>ToArray</c> at its top level: a request/response read ends anyway;</item>
///   <item>a write (<c>.Update(</c> at the top level of the chain);</item>
///   <item>a recognised re-open: <c>ReopenOnTransientFault</c>, <c>SelfHealing</c>,
///   <c>RetryWhen</c>/<c>Retry</c> anywhere, or a <c>.Catch</c> at the top level of the chain (a
///   per-item <c>Catch</c> inside a <c>SelectMany</c> does NOT guard the watch, so it is not
///   counted);</item>
///   <item>a forwarding <c>.Subscribe(observer)</c> — the observer owns the fault;</item>
///   <item>an error arm that RELEASES or RE-OPENS (<c>Release…</c>, <c>Evict…</c>,
///   <c>TryRemove</c>/<c>Remove</c>, <c>Reopen…</c>, <c>ReEstablish…</c>, <c>Schedule…</c>) — the
///   "drop the chain, the next caller opens a fresh one" shape of <c>UserIdentityCache</c>.</item>
/// </list>
/// <para>A watch handed to <c>SubscribeHubWatcher</c> / <c>SubscribeWithReEstablish</c> has no
/// <c>.Subscribe(</c> of its own on the query, so it is not seen at all — which is the fix.</para>
///
/// <para>🚨 <b>What it does NOT see, deliberately.</b> It reads text, not symbols, and favours
/// precision: a query built in one statement and subscribed in another, a query returned from a
/// method and subscribed by its caller, a watch over <c>GetMeshNodeStream</c> or a change feed, and
/// a <c>Take</c> inside a per-item lambda of an otherwise standing chain are all MISSED. A green run
/// therefore means "no new watch of the shape this repository has measured", never "every standing
/// watch is guarded".</para>
///
/// <para>A RATCHET at <see cref="Baseline"/>: the count may only fall. The fix is the reuse helpers —
/// <c>ActivityControlPlaneExtensions.SubscribeHubWatcher</c> (Mesh.Contract) or
/// <c>StandingWatchRecovery.ReopenOnTransientFault</c> (Hosting) — never a new primitive, and never
/// raising the baseline.</para>
/// </summary>
public class StandingQueryWatchRatchetGuard
{
    private static readonly string[] ScannedRoots = ["src", "memex"];

    /// <summary>
    /// The two sites the scan finds at seeding are the portal's e-mail senders
    /// (<c>OutboundEmailSender</c>, <c>InvitationEmailSender</c>): a live query whose error arm only
    /// logs. MAY ONLY DECREASE — lower it in the change that converts one.
    /// </summary>
    private const int Baseline = 2;

    private static readonly Regex SubscribeCall = new(@"\.Subscribe\s*\(", RegexOptions.Compiled);
    private static readonly Regex LiveQuery = new(@"(\.Query\s*<|\bGetQuery\s*\(|\bObserveQuery\b)", RegexOptions.Compiled);
    private static readonly Regex OneShotAnywhere = new(
        @"\.(Take|FirstAsync|FirstOrDefaultAsync|LastAsync|LastOrDefaultAsync|SingleAsync|Timeout)\s*[<(]",
        RegexOptions.Compiled);
    private static readonly Regex ReopenAnywhere = new(
        @"(RetryWhen\s*\(|\.Retry\s*\(|ReopenOnTransientFault\s*\(|SelfHealing\s*\()", RegexOptions.Compiled);
    private static readonly Regex ErrorArmReleases = new(
        @"\b(TryRemove|Remove|Release|Evict|Reopen|ReEstablish|Establish|Schedule|Retry)\w*\s*\(", RegexOptions.Compiled);
    private static readonly HashSet<string> TopLevelExemptions = ["Update", "Catch", "ToList", "ToArray"];

    [Fact]
    public void NoNewStandingQueryWatchDiesOnItsFirstFault()
    {
        var root = SourceScan.FindRepoRoot();
        var sites = Scan(root, ScannedRoots);

        Assert.True(sites.Count <= Baseline,
            $"🚨 Unguarded standing query watches rose to {sites.Count} (baseline {Baseline}). A live "
            + "query subscribed with an error arm that only logs is DEAD after its first fault (a "
            + "stalled provider, a dropped connection) until the process restarts. Hand the query to "
            + "ActivityControlPlaneExtensions.SubscribeHubWatcher (it re-opens on a bounded cadence and "
            + "ends with the hub), or compose StandingWatchRecovery.ReopenOnTransientFault. Never "
            + "raise the baseline.\nSites:\n"
            + string.Join("\n", sites.Select(s => "  " + s)));
    }

    [Fact]
    public void TheBaselineStaysCloseToTheTree()
    {
        var root = SourceScan.FindRepoRoot();
        var count = Scan(root, ScannedRoots).Count;
        Assert.True(Baseline - count <= 0,
            $"The tree holds {count} unguarded standing query watches but Baseline is {Baseline}. "
            + "Lower the baseline to the tree in the change that converted them.");
    }

    /// <summary>
    /// Proven over a planted tree with the REAL scan: the pre-fix shapes this guard exists for are
    /// found, and every shape that is fine — a one-shot read, a write, a forwarding observer, a
    /// re-opening helper, a releasing error arm — is not.
    /// </summary>
    [Fact]
    public void TheScannerSeesWhatItClaimsTo()
    {
        var dir = Directory.CreateTempSubdirectory("standing-query-watch-selftest");
        try
        {
            var s = Directory.CreateDirectory(Path.Combine(dir.FullName, "src")).FullName;
            void W(string name, string body) => File.WriteAllText(Path.Combine(s, name), body);

            // The pre-fix catalog shape (InstanceSecretCatalog / UiContributionCatalog).
            W("CatalogLogsOnly.cs", "class A { void M() { subscription = access\n"
                + "  .RunAsSystem(() => mesh.Query<MeshNode>(MeshQueryRequest.FromQuery(Q)))\n"
                + "  .Subscribe(OnChange, ex =>\n  {\n    Faulted = true;\n    logger.LogError(ex, \"dead\");\n  }); } }");
            // The pre-fix runner shape: a synced workspace query.
            W("RunnerGetQuery.cs", "class B { void M() { sub = AsSystem(() => hub.GetWorkspace().GetQuery(\"id\", q))\n"
                + "  .Subscribe(nodes => { Handle(nodes); }, ex => logger.LogWarning(ex, \"failed\")); } }");
            // The pre-fix inbox shape: a per-item Catch inside the SelectMany does not guard the watch.
            W("InboxPerItemCatch.cs", "class C { void M() { subs.Add(hub.GetQuery(\"inbox\", q)\n"
                + "  .SelectMany(nodes => nodes.ToArray())\n"
                + "  .Select(n => Drain(n).Catch((Exception ex) => Observable.Return(1)))\n"
                + "  .Concat()\n  .Subscribe(_ => { }, ex => logger.LogError(ex, \"dead\"))); } }");
            W("BareLambda.cs", "class D { void M() { mesh.Query<MeshNode>(r).Subscribe(c => Handle(c)); } }");

            W("OneShotRead.cs", "class E { void M() { mesh.Query<MeshNode>(r).Where(c => c.ChangeType == Initial)\n"
                + "  .Take(1).Subscribe(c => Handle(c), ex => Log(ex)); } }");
            W("OneShotFirst.cs", "class F { void M() { AsSystem(() => mesh.Query<MeshNode>(r).FirstAsync())\n"
                + "  .Subscribe(c => Handle(c), ex => Log(ex)); } }");
            W("Forwarding.cs", "class G { IDisposable M(IObserver<int> observer) { return mesh.Query<MeshNode>(r).Subscribe(observer); } }");
            W("ReleasingArm.cs", "class H { void M() { subscription.Disposable = mesh.Query<MeshNode>(r)\n"
                + "  .Subscribe(c => Apply(c), ex => { logger.LogWarning(ex, \"x\"); ReleaseSubscription(subscription); }); } }");
            W("Reopened.cs", "class I { void M() { sub = StandingWatchRecovery.ReopenOnTransientFault(() => mesh.Query<MeshNode>(r), 5, b, s)\n"
                + "  .Subscribe(c => Apply(c), ex => Log(ex)); } }");
            W("TopLevelCatch.cs", "class J { void M() { sub = mesh.Query<MeshNode>(r).Catch((Exception ex) => Fallback())\n"
                + "  .Subscribe(c => Apply(c), ex => Log(ex)); } }");
            W("HubWatcher.cs", "class K { void M() { sub = ActivityControlPlaneExtensions.SubscribeHubWatcher(hub,\n"
                + "  () => mesh.Query<MeshNode>(r), OnChange, logger, \"catalog\"); } }");
            W("NodeStreamWrite.cs", "class L { void M() { hub.GetQuery(\"x\", q); stream.Update(n => n).Subscribe(_ => { }, ex => Log(ex)); } }");
            W("Prose.cs", "// never mesh.Query<MeshNode>(r).Subscribe(c => f(c), ex => Log(ex))\nclass N { }");

            var found = Scan(dir.FullName, ["src"]).Select(p => Path.GetFileName(p.Split(':')[0])).ToHashSet();

            Assert.Contains("CatalogLogsOnly.cs", found);
            Assert.Contains("RunnerGetQuery.cs", found);
            Assert.Contains("InboxPerItemCatch.cs", found);
            Assert.Contains("BareLambda.cs", found);
            Assert.DoesNotContain("OneShotRead.cs", found);
            Assert.DoesNotContain("OneShotFirst.cs", found);
            Assert.DoesNotContain("Forwarding.cs", found);
            Assert.DoesNotContain("ReleasingArm.cs", found);
            Assert.DoesNotContain("Reopened.cs", found);
            Assert.DoesNotContain("TopLevelCatch.cs", found);
            Assert.DoesNotContain("HubWatcher.cs", found);
            Assert.DoesNotContain("NodeStreamWrite.cs", found);
            Assert.DoesNotContain("Prose.cs", found);
        }
        finally { dir.Delete(recursive: true); }
    }

    /// <summary>The unguarded standing query watches under <paramref name="roots"/>, as <c>path:line</c>.</summary>
    private static List<string> Scan(string root, IEnumerable<string> roots) =>
        SourceScan.SourceFiles(root, roots)
            .SelectMany(file => SitesIn(file).Select(line => $"{SourceScan.Relative(root, file)}:{line}"))
            .ToList();

    private static IEnumerable<int> SitesIn(string file)
    {
        var text = SourceScan.MaskCommentsAndStrings(File.ReadAllText(file));
        foreach (Match m in SubscribeCall.Matches(text))
        {
            var chain = text[StatementStart(text, m.Index)..m.Index];
            if (!LiveQuery.IsMatch(chain) || OneShotAnywhere.IsMatch(chain) || ReopenAnywhere.IsMatch(chain))
                continue;
            if (TopLevelCalls(chain).Any(TopLevelExemptions.Contains))
                continue;
            var args = Arguments(text, m.Index + m.Length - 1);
            if (args.Count == 1 && args[0].Length > 0 && !args[0].Contains("=>"))
                continue;   // Subscribe(observer): the observer owns the fault
            if (args.Count >= 2 && ErrorArmReleases.IsMatch(args[1]))
                continue;   // the error arm drops the chain so the next caller re-opens it
            yield return text[..m.Index].Count(c => c == '\n') + 1;
        }
    }

    /// <summary>Walks back from <paramref name="pos"/> to the start of the enclosing statement or argument.</summary>
    private static int StatementStart(string text, int pos)
    {
        var depth = 0;
        for (var i = pos - 1; i >= 0; i--)
        {
            var c = text[i];
            if (c is ')' or ']' or '}') depth++;
            else if (c is '(' or '[' or '{')
            {
                if (depth == 0) return i + 1;
                depth--;
            }
            else if (c == ';' && depth == 0) return i + 1;
        }
        return 0;
    }

    /// <summary>The names of the calls chained at the TOP level of <paramref name="chain"/> (not inside any lambda or argument).</summary>
    private static IEnumerable<string> TopLevelCalls(string chain)
    {
        var depth = 0;
        for (var i = 0; i < chain.Length; i++)
        {
            var c = chain[i];
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth--;
            else if (c == '.' && depth == 0)
            {
                var name = Regex.Match(chain[(i + 1)..], @"^\w+");
                if (name.Success) yield return name.Value;
            }
        }
    }

    /// <summary>The top-level arguments of the call whose '(' is at <paramref name="openParen"/>.</summary>
    private static List<string> Arguments(string text, int openParen)
    {
        var args = new List<string>();
        var depth = 0;
        var start = openParen + 1;
        for (var i = openParen + 1; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}')
            {
                if (depth == 0)
                {
                    args.Add(text[start..i].Trim());
                    return args;
                }
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                args.Add(text[start..i].Trim());
                start = i + 1;
            }
        }
        return args;
    }
}
