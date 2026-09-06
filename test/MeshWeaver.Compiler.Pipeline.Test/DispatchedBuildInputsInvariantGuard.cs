using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b><c>DispatchedBuildInputs</c> non-null means a compile is IN FLIGHT — and nothing else.</b>
///
/// <para>The stamp records which inputs the in-flight compile was dispatched for, so a release
/// request arriving mid-compile can be ABSORBED when that compile will produce byte-for-byte what
/// the request asks for (#2544, <c>IsSatisfiedByInFlightCompile</c>). Its whole value is that it
/// describes something currently running.</para>
///
/// <para>No terminal write cleared it (#3390): a definition that reached <c>Ok</c> or <c>Error</c>
/// kept the token of the compile that had already finished, so a non-null value meant "in flight OR
/// finished at some point". Today the <c>Pending</c>/<c>Compiling</c> gate ahead of the absorb read
/// hides that, which makes it a latent trap rather than a live bug — the kind that surfaces the day
/// somebody reads the field without checking status first, and absorbs a release request against a
/// compile that ended minutes ago.</para>
///
/// <para>This guard pins the writing half mechanically, because the failure is a write site that
/// simply does not mention the field — invisible on review, and exactly how the six unstamped
/// <c>Pending</c> doors in #3390 came about.</para>
/// </summary>
public class DispatchedBuildInputsInvariantGuard
{
    private const string Subject = "src/MeshWeaver.Compiler.Pipeline";

    // Terminal write sites found on origin/main a60b1e6db. A floor, because a matcher that stops
    // matching reports a clean tree — the failure this file exists to prevent.
    private const int KnownTerminalWrites = 5;

    private static readonly string[] TerminalWrites =
    [
        "CompilationStatus = CompilationStatus.Ok",
        "CompilationStatus = CompilationStatus.Error",
    ];

    [Fact]
    public void EveryTerminalCompilationStatusWrite_ClearsTheInFlightStamp()
    {
        var root = FindRepoRoot();
        var dir = Path.Combine(root, Subject.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(Directory.Exists(dir),
            $"{Subject} is gone — this guard now checks nothing. Point it at the code's new home.");

        var offenders = new List<string>();
        var found = 0;

        foreach (var path in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            var code = File.ReadAllText(path);
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');

            foreach (var write in TerminalWrites)
            {
                for (var i = code.IndexOf(write, System.StringComparison.Ordinal); i >= 0;
                     i = code.IndexOf(write, i + 1, System.StringComparison.Ordinal))
                {
                    var block = EnclosingInitializer(code, i);
                    if (block is null)
                        continue;   // not inside an object initializer — e.g. a comparison

                    found++;
                    if (!block.Contains("DispatchedBuildInputs", System.StringComparison.Ordinal))
                        offenders.Add($"{relative}:{LineOf(code, i)} — {write}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "A terminal CompilationStatus write does not clear DispatchedBuildInputs. The stamp "
            + "describes the compile currently IN FLIGHT; once the status is Ok or Error there is "
            + "none, so a stale token makes `non-null` mean \"in flight OR finished long ago\" and "
            + "IsSatisfiedByInFlightCompile can absorb a release request against a compile that has "
            + "already ended. Add `DispatchedBuildInputs = null` to the initializer (#3390)."
            + System.Environment.NewLine
            + string.Join(System.Environment.NewLine, offenders.Select(o => "  · " + o)));

        Assert.True(found >= KnownTerminalWrites,
            $"Expected at least {KnownTerminalWrites} terminal CompilationStatus writes under "
            + $"{Subject}, saw {found}. The matcher has gone blind, so this guard is reporting a "
            + "clean tree having checked nothing. Re-point it before trusting it.");
    }

    /// <summary>The matcher sees both shapes it claims to — without this, every assertion above
    /// passes on a scan that silently matched nothing.</summary>
    [Fact]
    public void TheMatcher_SeesACleared_AndAnUnclearedInitializer()
    {
        const string cleared =
            "return def with\n{\n    DispatchedBuildInputs = null,\n    CompilationStatus = CompilationStatus.Ok,\n};";
        const string uncleared =
            "return def with\n{\n    CompilationStatus = CompilationStatus.Ok,\n    CompilationError = null,\n};";

        var i1 = cleared.IndexOf("CompilationStatus = CompilationStatus.Ok", System.StringComparison.Ordinal);
        var i2 = uncleared.IndexOf("CompilationStatus = CompilationStatus.Ok", System.StringComparison.Ordinal);

        Assert.Contains("DispatchedBuildInputs", EnclosingInitializer(cleared, i1)!);
        Assert.DoesNotContain("DispatchedBuildInputs", EnclosingInitializer(uncleared, i2)!);
    }

    // The braces enclosing an object initializer: walk back to the nearest UNMATCHED '{', then
    // forward to its partner. A write split across lines is the normal shape here, so a line-wise
    // read would miss precisely the sites that matter.
    private static string? EnclosingInitializer(string code, int index)
    {
        var depth = 0;
        var open = -1;
        for (var i = index; i >= 0; i--)
        {
            if (code[i] == '}') depth++;
            else if (code[i] == '{')
            {
                if (depth == 0) { open = i; break; }
                depth--;
            }
        }
        if (open < 0) return null;

        depth = 0;
        for (var i = open; i < code.Length; i++)
        {
            if (code[i] == '{') depth++;
            else if (code[i] == '}' && --depth == 0)
                return code[open..(i + 1)];
        }
        return null;
    }

    private static int LineOf(string text, int index) => text.AsSpan(0, index).Count('\n') + 1;

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx")))
            dir = dir.Parent;
        Assert.True(dir is not null, "Could not locate the repository root (MeshWeaver.slnx).");
        return dir!.FullName;
    }
}
