using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 A bounded fan-out is spelled <c>MergeBounded(n)</c>, never Rx's bare <c>Merge(n)</c>.
///
/// <para>Rx's <c>Merge(maxConcurrent)</c> subscribes the next QUEUED inner from inside the previous
/// inner's <c>OnCompleted</c>, so inners that complete synchronously during <c>Subscribe</c> — a
/// request posted by a hub already at <c>ShutDown</c>, a <c>.Catch(… =&gt; Observable.Return(…))</c>,
/// a warm cache — recurse once per queued inner. On memex-cloud (pod <c>…-v4txp</c>, 2026-09-24
/// 06:56:01Z) an export of ~2,200 nodes queued that many lookups behind a <c>Merge(16)</c> during a
/// roll and the recursion ended in an uncatchable <c>StackOverflowException</c> that killed the
/// process. <c>BoundedMergeExtensions.MergeBounded</c> is the same operator with a stack-safe dequeue;
/// <c>BoundedMergeStackDepthTest</c> pins the difference.</para>
///
/// <para><b>What it recognises.</b> A bare <c>Merge</c> whose bound is an integer literal or a name
/// that says it is a bound (<c>BatchSize</c>, <c>…Concurrency</c>, <c>…Parallel…</c>), in both the
/// extension form <c>x.Merge(n)</c> and the static form <c>Observable.Merge(xs, n)</c>. A bound held
/// in a variable with an unrelated name is not recognised — the check can only be sure of what the
/// text says, so it is a floor, not a proof. Comments and strings are masked, so prose that names
/// the operator is not a finding.</para>
/// </summary>
public class MergeBoundedRatchetGuard
{
    private static readonly ImmutableArray<string> ScannedRoots = ["src", "memex"];

    private const string BoundName = @"(?:\d+|[A-Za-z_][\w.]*(?i:batch|concurren|parallel|fanout|fan_out)[\w]*)";

    private static readonly Regex ExtensionForm =
        new(@"\.Merge\(\s*" + BoundName + @"\s*\)", RegexOptions.Compiled);

    private static readonly Regex StaticForm =
        new(@"\bObservable\.Merge\(\s*[^,()]+,\s*" + BoundName + @"\s*\)", RegexOptions.Compiled);

    [Fact]
    public void EveryBoundedFanOutIsStackSafe()
    {
        var root = SourceScan.FindRepoRoot();
        var offenders = SourceScan.SourceFiles(root, ScannedRoots)
            .Select(f => (Path: SourceScan.Relative(root, f), Count: CountIn(f)))
            .Where(x => x.Count > 0)
            .ToList();

        Assert.True(offenders.Count == 0,
            "🚨 A bare Rx Merge(maxConcurrent) subscribes the next queued inner from inside the previous "
            + "inner's OnCompleted: inners that complete synchronously (a request from a hub at ShutDown, "
            + "a .Catch(→ Return), a warm cache) recurse once per queued inner until the stack overflows — "
            + "an uncatchable StackOverflowException that kills the process (memex-cloud, 2026-09-24). "
            + "Use MergeBounded(n) from MeshWeaver.Messaging instead:\n"
            + string.Join("\n", offenders.Select(x => $"  {x.Path} ({x.Count})")));
    }

    /// <summary>Proven over a planted tree, running the REAL scan.</summary>
    [Fact]
    public void TheScannerSeesWhatItClaimsTo()
    {
        var dir = Directory.CreateTempSubdirectory("merge-bounded-selftest");
        try
        {
            var s = Directory.CreateDirectory(Path.Combine(dir.FullName, "src")).FullName;
            void W(string name, string body) => File.WriteAllText(Path.Combine(s, name), body);

            W("Literal.cs", "class A { void M() { xs.ToObservable().Merge(8).ToList(); } }");
            W("Named.cs", "class B { void M() { xs.Merge(NodeCopyHelper.DefaultBatchSize); } }");
            W("Wrapped.cs", "class C { void M() { xs\n   .Select(Work)\n   .Merge(\n      MaxConcurrency); } }");
            W("Static.cs", "class D { void M() { Observable.Merge(perPath, PreValidateFanOutConcurrency); } }");
            W("Bounded.cs", "class E { void M() { xs.MergeBounded(8); } }");
            W("Streams.cs", "class F { void M() { a.Merge(b); Observable.Merge(a, b); } }");
            W("Prose.cs", "// never write .Merge(8) over synchronous inners\nclass G { string s = \".Merge(8)\"; }");

            var found = SourceScan.SourceFiles(dir.FullName, ["src"])
                .Select(f => (Name: Path.GetFileName(f), Count: CountIn(f)))
                .Where(x => x.Count > 0)
                .ToDictionary(x => x.Name, x => x.Count);

            Assert.True(found.ContainsKey("Literal.cs"), "a literal bound must be found");
            Assert.True(found.ContainsKey("Named.cs"), "a named batch-size bound must be found");
            Assert.True(found.ContainsKey("Wrapped.cs"), "🚨 real call sites wrap — the bound can sit on its own line");
            Assert.True(found.ContainsKey("Static.cs"), "the static Observable.Merge(xs, n) form must be found");
            Assert.False(found.ContainsKey("Bounded.cs"), "MergeBounded is the fix, not a finding");
            Assert.False(found.ContainsKey("Streams.cs"), "merging two streams is not a bounded fan-out");
            Assert.False(found.ContainsKey("Prose.cs"), "comments and strings are not code");
        }
        finally { dir.Delete(recursive: true); }
    }

    private static int CountIn(string file)
    {
        var code = SourceScan.MaskCommentsAndStrings(File.ReadAllText(file));
        return ExtensionForm.Matches(code).Count + StaticForm.Matches(code).Count;
    }
}
