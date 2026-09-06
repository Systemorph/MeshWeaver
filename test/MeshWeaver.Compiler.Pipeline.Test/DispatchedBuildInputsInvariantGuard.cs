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
/// <para>The failure mode is a write site that simply <b>does not mention the field</b> — invisible
/// on review, and it has now bitten this one invariant twice. #3390 measured SEVEN
/// <c>Pending</c> doors that never touched it (plus four that wrote an honest <c>null</c>) against
/// ONE that stamped; and the terminal half, closed first, still left two sites uncleared because
/// they spell the status as a ternary rather than as the literal the first matcher looked for.</para>
///
/// <para>So this guard is written as a <b>classifier over the assigned expression</b>, not a
/// substring search for a spelling, and it pins BOTH halves:</para>
/// <list type="number">
///   <item><b>One door.</b> A <c>CompilationStatus = …Pending</c> write may appear in exactly one
///     place in <c>src/</c> — <c>NodeTypeCompilationHelpers.DispatchPending</c>. That is what makes
///     stamping structural rather than a rule every future door has to remember: a thirteenth door
///     cannot be opened without either going through that function or reddening this test.</item>
///   <item><b>Every terminal write clears.</b> An initializer that assigns a terminal status
///     (<c>Ok</c> / <c>Error</c> / <c>Unavailable</c>, however spelled) must mention the field.</item>
/// </list>
///
/// <para><c>Compiling</c> is deliberately in NEITHER list: the Pending → Compiling transition is the
/// SAME compile continuing, so it must PRESERVE the stamp — which it does by not mentioning it.</para>
/// </summary>
public class DispatchedBuildInputsInvariantGuard
{
    private const string Subject = "src";

    /// <summary>The ONE sanctioned Pending door. Everything else must route through it.</summary>
    private const string TheDoor = "src/MeshWeaver.Compiler.Pipeline/NodeTypeCompilationHelpers.cs";

    // Floors, because a matcher that stops matching reports a clean tree — the failure this file
    // exists to prevent. Measured on the branch that closed #3390's stamping half.
    private const int KnownTerminalWrites = 6;
    private const int KnownStatusWrites = 12;

    [Fact]
    public void ExactlyOnePlaceInSrcMayFlipAStatusToPending()
    {
        var root = FindRepoRoot();
        var doors = ScanSrc(root)
            .Where(w => Mentions(w.Value, "Pending"))
            .ToList();

        Assert.True(
            doors.Count == 1 && doors[0].File == TheDoor,
            "A CompilationStatus = …Pending write exists outside the one sanctioned door. Every "
            + "dispatch must record WHAT it is for, or a release request arriving during it parks "
            + "and compiles a second time on the first compile's terminal write-back (#2544, "
            + "#3390). Do not add `DispatchedBuildInputs = …` here — call "
            + "`NodeTypeCompilationHelpers.DispatchPending(def, modulesHash | hub)` instead, which "
            + "flips the status and stamps the token in one place."
            + System.Environment.NewLine
            + $"  Expected exactly one, in {TheDoor}. Found {doors.Count}:"
            + System.Environment.NewLine
            + string.Join(System.Environment.NewLine, doors.Select(d => "  · " + d.Where)));
    }

    [Fact]
    public void EveryTerminalCompilationStatusWrite_ClearsTheInFlightStamp()
    {
        var root = FindRepoRoot();
        var all = ScanSrc(root);

        var terminal = all
            .Where(w => Mentions(w.Value, "Ok")
                     || Mentions(w.Value, "Error")
                     || Mentions(w.Value, "Unavailable"))
            .ToList();

        var offenders = terminal
            .Where(w => !w.Initializer.Contains("DispatchedBuildInputs", System.StringComparison.Ordinal))
            .Select(w => w.Where)
            .ToList();

        Assert.True(offenders.Count == 0,
            "A terminal CompilationStatus write does not clear DispatchedBuildInputs. The stamp "
            + "describes the compile currently IN FLIGHT; once the status is Ok, Error or "
            + "Unavailable there is none, so a stale token makes `non-null` mean \"in flight OR "
            + "finished long ago\" and IsSatisfiedByInFlightCompile can absorb a release request "
            + "against a compile that has already ended. Add `DispatchedBuildInputs = null` to the "
            + "initializer (#3390)."
            + System.Environment.NewLine
            + string.Join(System.Environment.NewLine, offenders.Select(o => "  · " + o)));

        Assert.True(terminal.Count >= KnownTerminalWrites,
            $"Expected at least {KnownTerminalWrites} terminal CompilationStatus writes under "
            + $"{Subject}/, saw {terminal.Count}. The matcher has gone blind, so this guard is "
            + "reporting a clean tree having checked nothing. Re-point it before trusting it.");

        Assert.True(all.Count >= KnownStatusWrites,
            $"Expected at least {KnownStatusWrites} CompilationStatus initializer writes under "
            + $"{Subject}/, saw {all.Count}. Same reason: a scan that matches nothing passes.");
    }

    /// <summary>
    /// 🚨 The matcher sees every shape it claims to — without this, both assertions above pass on a
    /// scan that silently matched nothing, or on one that cannot tell a ternary terminal write from
    /// a Pending flip. Each row below is a shape that EXISTS in <c>src/</c>.
    /// </summary>
    [Fact]
    public void TheClassifier_SeesEveryShapeItClaimsTo()
    {
        // The Pending door, and a cleared / uncleared terminal pair.
        var door = Single("return def with\n{\n    CompilationStatus = CompilationStatus.Pending,\n"
                          + "    DispatchedBuildInputs = t,\n};");
        Mentions(door.Value, "Pending").Should().BeTrue();
        door.Initializer.Should().Contain("DispatchedBuildInputs");

        var cleared = Single("return def with\n{\n    DispatchedBuildInputs = null,\n"
                             + "    CompilationStatus = CompilationStatus.Ok,\n};");
        Mentions(cleared.Value, "Ok").Should().BeTrue();
        cleared.Initializer.Should().Contain("DispatchedBuildInputs");

        var uncleared = Single("return def with\n{\n    CompilationStatus = CompilationStatus.Ok,\n"
                               + "    CompilationError = null,\n};");
        Mentions(uncleared.Value, "Ok").Should().BeTrue();
        uncleared.Initializer.Should().NotContain("DispatchedBuildInputs",
            "the guard's whole job is telling these two apart");

        // 🚨 The shape the FIRST matcher was blind to: a terminal status written as a ternary, so
        // the literal "CompilationStatus = CompilationStatus.Error" never appears. Both branches
        // must be seen, or ApplyCompileFailure goes unchecked again.
        var ternary = Single(
            "return def with\n{\n    CompilationStatus = IsAvailabilityNonVerdict(e)\n"
            + "        ? CompilationStatus.Unavailable\n        : CompilationStatus.Error,\n"
            + "    CompilationError = null,\n};");
        Mentions(ternary.Value, "Unavailable").Should().BeTrue();
        Mentions(ternary.Value, "Error").Should().BeTrue();
        Mentions(ternary.Value, "Pending").Should().BeFalse(
            "a ternary between two terminal states is not a dispatch");

        // Compiling is in neither list — the transition continues the SAME compile, so preserving
        // the stamp (by not mentioning it) is correct and must not be reported.
        var compiling = Single(
            "return def with\n{\n    CompilationStatus = CompilationStatus.Compiling,\n};");
        Mentions(compiling.Value, "Pending").Should().BeFalse();
        Mentions(compiling.Value, "Ok").Should().BeFalse();
        Mentions(compiling.Value, "Error").Should().BeFalse();

        // A projection copying an arbitrary value is neither a dispatch nor a terminal write.
        var copy = Single("new Snapshot\n{\n    CompilationStatus = definition.CompilationStatus,\n};");
        Mentions(copy.Value, "Pending").Should().BeFalse();
        Mentions(copy.Value, "Ok").Should().BeFalse();

        // A comparison is not a write, and neither is prose in a comment.
        Classify("if (def.CompilationStatus == CompilationStatus.Pending) return curr;")
            .Should().BeEmpty();
        Classify("{\n    // flips CompilationStatus = Pending so the watcher fires\n}")
            .Should().BeEmpty();
    }

    private static StatusWrite Single(string code)
    {
        var writes = Classify(code);
        Assert.True(writes.Count == 1,
            $"the classifier should see exactly one write in this fixture, saw {writes.Count} — "
            + "it has gone blind (or double-counts), which makes every assertion above vacuous");
        return writes[0];
    }

    // ── The scanner ─────────────────────────────────────────────────────────────────────────────

    private sealed record StatusWrite(string File, int Line, string Value, string Initializer)
    {
        public string Where => $"{File}:{Line} — CompilationStatus = {Collapse(Value)}";
        private static string Collapse(string v) =>
            string.Join(' ', v.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries));
    }

    private static List<StatusWrite> ScanSrc(string root)
    {
        var dir = Path.Combine(root, Subject);
        Assert.True(Directory.Exists(dir),
            $"{Subject}/ is gone — this guard now checks nothing. Point it at the code's new home.");

        var found = new List<StatusWrite>();
        foreach (var path in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (relative.Contains("/obj/", System.StringComparison.Ordinal)
                || relative.Contains("/bin/", System.StringComparison.Ordinal))
                continue;

            var code = File.ReadAllText(path);
            if (!code.Contains("CompilationStatus", System.StringComparison.Ordinal))
                continue;

            foreach (var write in Classify(code))
                found.Add(write with { File = relative });
        }
        return found;
    }

    /// <summary>Every <c>CompilationStatus = &lt;expr&gt;</c> assignment that sits inside an object
    /// initializer, with the assigned expression and the enclosing initializer.</summary>
    private static List<StatusWrite> Classify(string code)
    {
        const string Property = "CompilationStatus";
        var writes = new List<StatusWrite>();

        for (var i = code.IndexOf(Property, System.StringComparison.Ordinal); i >= 0;
             i = code.IndexOf(Property, i + 1, System.StringComparison.Ordinal))
        {
            // A qualified read (`def.CompilationStatus`, `CompilationStatus.Ok`) is not the
            // property being assigned in an initializer.
            if (i > 0 && (code[i - 1] == '.' || char.IsLetterOrDigit(code[i - 1]) || code[i - 1] == '_'))
                continue;
            var after = i + Property.Length;
            if (after < code.Length && (char.IsLetterOrDigit(code[after]) || code[after] == '_' || code[after] == '.'))
                continue;

            // …followed by a single '=' (never '==', '=>' or '>=').
            var j = after;
            while (j < code.Length && (code[j] == ' ' || code[j] == '\t' || code[j] == '\r' || code[j] == '\n')) j++;
            if (j >= code.Length || code[j] != '=') continue;
            if (j + 1 < code.Length && (code[j + 1] == '=' || code[j + 1] == '>')) continue;
            if (j > 0 && (code[j - 1] == '!' || code[j - 1] == '<' || code[j - 1] == '>')) continue;

            if (IsInsideComment(code, i)) continue;

            var value = ReadValue(code, j + 1);
            if (value is null) continue;

            var initializer = EnclosingInitializer(code, i);
            if (initializer is null) continue;   // not inside an object initializer

            writes.Add(new StatusWrite("(inline)", LineOf(code, i), value, initializer));
        }
        return writes;
    }

    /// <summary>The assigned expression: everything up to the ',' or '}' that closes this member,
    /// at the nesting depth the value started at — so a ternary spanning lines is read whole.</summary>
    private static string? ReadValue(string code, int start)
    {
        var depth = 0;
        for (var i = start; i < code.Length; i++)
        {
            var c = code[i];
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']') depth--;
            else if (c == '}')
            {
                if (depth == 0) return code[start..i];
                depth--;
            }
            else if (c == ',' && depth == 0) return code[start..i];
            else if (c == ';' && depth == 0) return code[start..i];
        }
        return null;
    }

    /// <summary>Whether the assigned expression names a given <see cref="CompilationStatus"/>
    /// member — both branches of a ternary count, since either may be committed.</summary>
    private static bool Mentions(string value, string member) =>
        value.Contains("CompilationStatus." + member, System.StringComparison.Ordinal);

    private static bool IsInsideComment(string code, int index)
    {
        var lineStart = code.LastIndexOf('\n', System.Math.Max(0, index - 1)) + 1;
        var line = code[lineStart..index];
        return line.Contains("//", System.StringComparison.Ordinal)
            || line.Contains("///", System.StringComparison.Ordinal)
            || line.TrimStart().StartsWith('*');
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
