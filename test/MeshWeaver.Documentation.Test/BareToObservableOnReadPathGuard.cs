using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// Governance guard (#2377): no file on the storage / query read path may call the parameterless
/// <c>IEnumerable.ToObservable()</c>. Use <c>ToInlineObservable()</c>
/// (<c>MeshWeaver.Mesh.Services.InlineObservableExtensions</c>).
///
/// <para><b>Why a guard rather than care.</b> The two spellings are one word apart, the wrong one is
/// what IntelliSense offers first, and the failure it produces is <b>completely silent</b>: no
/// exception, no completion, no log line. Nothing about the call site looks wrong, and nothing in a
/// normal test run reveals it — the defect needs a cold process, a real CPU budget and the whole
/// assembly to show up at all. That is exactly the shape a text guard exists for.</para>
///
/// <para><b>The mechanism.</b> Rx defaults <c>ToObservable()</c> to <c>SchedulerDefaults.Iteration</c>
/// = <c>CurrentThreadScheduler</c>, which does not mean "run it here now". It keeps a
/// <c>[ThreadStatic] bool</c> for "a trampoline is already running on this thread", and while that is
/// set <c>Schedule</c> only ENQUEUES, leaving the item for whoever owns the outer trampoline to
/// drain. Rx opens such trampolines on every operator subscription, and a <c>Task</c> completed from
/// inside an Rx pipeline (a <c>.ToTask()</c>, an <c>AsyncSubject</c>) resumes its awaiter INLINE
/// inside one — the captured stack for #2377 had the hub's own <c>MessageService.DrainOne</c> pump at
/// the bottom. A read subscribed from such a frame that then blocks on its first result deadlocks
/// against its own queued iteration: the query's <c>Initial</c> is never emitted, and in the portal a
/// live children listing silently stays empty forever.</para>
///
/// <para>🚨 The fix for a failure here is never to suppress it, and never to add an exemption: call
/// <c>ToInlineObservable()</c>. It is <c>ImmediateScheduler</c>, which carries no ambient per-thread
/// state and (per <c>InlineObservableExtensionsTest</c>) still iterates a long sequence without
/// growing the stack. If a read genuinely must not run inline, it does I/O and belongs on
/// <c>IIoPool</c> — not on the caller's trampoline.</para>
/// </summary>
public class BareToObservableOnReadPathGuard
{
    /// <summary>
    /// The storage / query read path: everything that participates in producing a query's snapshot
    /// or an <c>IStorageAdapter</c> read. These are the files whose contract is "synchronous on the
    /// subscribing thread"; elsewhere in the tree a deferred iteration is merely a scheduling choice.
    /// </summary>
    private static readonly string[] ScannedRoots =
    [
        "src/MeshWeaver.Hosting/Persistence",
        // The storage backends (Sqlite, PostgreSql, Cosmos, Snowflake) moved to MeshWeaver.Plugins;
        // the same guard scans them there (Memex.Hosts.Test.BareToObservableOnReadPathGuard).
    ];

    /// <summary>
    /// Individual read-path files that sit inside a project whose OTHER files legitimately use the
    /// parameterless overload (a <c>Task.ToObservable()</c> bridge, or a deferred write fan-out
    /// where scheduling is a free choice). Naming the file rather than widening
    /// <see cref="ScannedRoots"/> keeps the ratchet at zero without inventing exemptions inside it.
    ///
    /// <para><c>SyncedQueryDataSourceExtensions</c> is the per-user RLS filter every synced-query
    /// emission passes through — #2087, the residual of #2377 that the storage-root sweep could not
    /// see. It re-emits a snapshot on the DELIVERY thread of an upstream emission, which is exactly
    /// the "already inside somebody's trampoline" frame the mechanism below strands on.</para>
    /// </summary>
    private static readonly string[] ScannedFiles =
    [
        "src/MeshWeaver.Graph.Contract/SyncedQueryDataSourceExtensions.cs",
    ];

    /// <summary>
    /// The parameterless call only. <c>ToObservable(someScheduler)</c> is an explicit, reviewed
    /// choice and is left alone.
    /// </summary>
    private static readonly Regex Bare = new(@"\.ToObservable\s*\(\s*\)", RegexOptions.Compiled);

    /// <summary>Zero occurrences — there is no seeded inventory, because there are none left.</summary>
    [Fact]
    public void NoReadPathFile_UsesTheParameterlessToObservable()
    {
        var root = SourceScan.FindRepoRoot();
        var offenders = new List<string>();

        // 🚨 A guard must never pass on no evidence. SourceFiles() silently drops a root that does
        // not exist, so a renamed project or a typo here would turn this into a green check that
        // scanned nothing — the exact skip-trapdoor shape AGENTS.md bans for CI gates. Assert the
        // roots resolve, and that the scan actually read files, before believing the count of zero.
        foreach (var scanned in ScannedRoots)
            Assert.True(Directory.Exists(Path.Combine(root, scanned)),
                $"Scanned root '{scanned}' does not exist — this guard would scan nothing and pass. "
                + "Update ScannedRoots to match the tree; never delete the root to make it green.");

        // Same reason, per FILE: a moved or renamed file would silently drop out of the scan and
        // leave a green tick over a call site nobody is checking any more.
        foreach (var scanned in ScannedFiles)
            Assert.True(File.Exists(Path.Combine(root, scanned)),
                $"Scanned file '{scanned}' does not exist — this guard would stop checking it and "
                + "still pass. Point ScannedFiles at where the file moved to; never delete the "
                + "entry to make it green.");

        var files = SourceScan.SourceFiles(root, ScannedRoots)
            .Concat(ScannedFiles.Select(f => Path.Combine(root, f)))
            .ToList();
        Assert.True(files.Count > 20,
            $"Only {files.Count} files were scanned across the read path — too few to be the real "
            + "tree, so a pass here would mean nothing.");

        foreach (var file in files)
        {
            // Mask first: several of these files DISCUSS the banned call in their remarks, and
            // naming the problem is the opposite of committing it.
            var code = SourceScan.MaskCommentsAndStrings(File.ReadAllText(file));
            foreach (Match m in Bare.Matches(code))
            {
                var line = code.Take(m.Index).Count(c => c == '\n') + 1;
                offenders.Add($"{SourceScan.Relative(root, file)}:{line}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Parameterless IEnumerable.ToObservable() on the storage/query read path — this defers "
            + "the iteration to the CALLER's Rx trampoline, where it can be queued and never run "
            + "(#2377: no error, no completion, a live listing that stays empty forever). Use "
            + "ToInlineObservable(). Offenders:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }
}
