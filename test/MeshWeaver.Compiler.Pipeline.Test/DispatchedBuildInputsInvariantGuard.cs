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
///   <item><b>Readable, or refused.</b> The two rules below decide what a write commits from the
///     enum members its expression NAMES, so an expression that names none —
///     <c>CompilationStatus = status</c>, where <c>status</c> was computed above — would slip past
///     both while committing <c>Pending</c>. That is the shape door 5 (the sources watcher's
///     parked auto-retry) actually had before #3390, and it is why that door was one of the seven
///     that never stamped. An unclassifiable expression is therefore a FAILURE naming the site,
///     not a pass.</item>
///   <item><b>One door.</b> A <c>CompilationStatus = …Pending</c> write may appear in exactly one
///     place in <c>src/</c> — <c>NodeTypeCompilationHelpers.DispatchPending</c>. Together with the
///     rule above, that makes stamping structural rather than a rule every future door has to
///     remember: a thirteenth door cannot be opened without going through that function or
///     reddening this test.</item>
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
    // exists to prevent. Measured on the branch that closed #3390's stamping half, over REAL code
    // only: 11 `CompilationStatus = …` initializer writes, 7 of them terminal.
    //
    // 🚨 These numbers were 12 and 6 for about ten minutes, padded by log templates the scanner
    // had not yet learned to blank ("flipping CompilationStatus=Pending to rebuild" and friends).
    // A floor met by prose is a floor that cannot fail — the exact defect a floor exists to catch,
    // one level up. Re-measure them from a blanked scan, never from the raw grep count.
    private const int KnownTerminalWrites = 7;
    private const int KnownStatusWrites = 11;

    /// <summary>
    /// 🚨 <b>FAIL CLOSED on an expression the classifier cannot read.</b>
    ///
    /// <para>The other two assertions decide what a write is from the enum members its expression
    /// NAMES. That is only sound if every expression names them — a write through an opaque local
    /// (<c>CompilationStatus = status</c>, where <c>status</c> was computed above) commits
    /// <c>Pending</c> while naming nothing, so it would slip past the one-door assertion and past
    /// the terminal one, silently. This is not hypothetical: it is exactly the shape door 5 (the
    /// sources watcher's parked auto-retry) had before #3390, and it is why that door was one of
    /// the seven that never touched the stamp.</para>
    ///
    /// <para>So an unclassifiable expression is a FAILURE naming the site, not a pass. Two shapes
    /// are readable and everything else is refused: an expression whose <c>CompilationStatus</c>
    /// values are all written as <c>CompilationStatus.&lt;Member&gt;</c> literals (a ternary
    /// between two of them included), and a plain copy of another object's status
    /// (<c>x.CompilationStatus</c>), which transfers a status some door already produced rather
    /// than creating a dispatch.</para>
    /// </summary>
    [Fact]
    public void NoCompilationStatusIsWrittenThroughAnExpressionThisGuardCannotRead()
    {
        var root = FindRepoRoot();
        var opaque = ScanSrc(root).Where(w => !IsReadable(w.Value)).ToList();

        Assert.True(opaque.Count == 0,
            "A CompilationStatus write assigns an expression this guard cannot classify, so it "
            + "cannot tell whether that write commits Pending — and a Pending flip that does not "
            + "stamp DispatchedBuildInputs is #3390. Write the enum members literally "
            + "(`CompilationStatus.Ok`, a ternary between two of them), or — if this is a dispatch "
            + "— call `NodeTypeCompilationHelpers.DispatchPending(def, modulesHash | hub)`, which "
            + "flips the status and stamps the token in one place."
            + System.Environment.NewLine
            + string.Join(System.Environment.NewLine, opaque.Select(o => "  · " + o.Where)));
    }

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

        // 🚨 A PRESERVE, not a door (#3583's delivery fix). The expression names Pending and
        // Compiling only in the PATTERN; the values it can actually commit are the status the node
        // already had, and Unavailable. Matching bare text called this a second Pending door and
        // reddened a PR whose line dispatches nothing — so the classifier must read value
        // positions. Unavailable is still seen, which keeps the terminal rule applying to it.
        var preserve = Single(
            "return refused with\n{\n"
            + "    CompilationStatus = def.CompilationStatus is CompilationStatus.Pending or CompilationStatus.Compiling\n"
            + "        ? def.CompilationStatus\n        : CompilationStatus.Unavailable,\n"
            + "    DispatchedBuildInputs = null,\n};");
        Mentions(preserve.Value, "Pending").Should().BeFalse(
            "a pattern test is not a value the expression can commit — this preserves a status a door already produced");
        Mentions(preserve.Value, "Compiling").Should().BeFalse(
            "same reason, and Compiling is not a dispatch either");
        Mentions(preserve.Value, "Unavailable").Should().BeTrue(
            "the terminal it CAN commit is still seen, so the clears-the-stamp rule still applies to it");
        IsReadable(preserve.Value).Should().BeTrue(
            "it names members literally, so it must not trip the fail-closed assertion");

        // …and the narrowing must not blind the door itself: a literal write in a VALUE position,
        // with the same members present in a pattern beforehand, is still caught.
        var doorAfterPattern = Single(
            "return def with\n{\n"
            + "    CompilationStatus = def.CompilationStatus is CompilationStatus.Ok\n"
            + "        ? CompilationStatus.Pending\n        : CompilationStatus.Pending,\n};");
        Mentions(doorAfterPattern.Value, "Pending").Should().BeTrue(
            "🚨 the positive control for the value-position rule: stripping pattern operands must not hide a real dispatch");

        // Compiling is in neither list — the transition continues the SAME compile, so preserving
        // the stamp (by not mentioning it) is correct and must not be reported.
        var compiling = Single(
            "return def with\n{\n    CompilationStatus = CompilationStatus.Compiling,\n};");
        Mentions(compiling.Value, "Pending").Should().BeFalse();
        Mentions(compiling.Value, "Ok").Should().BeFalse();
        Mentions(compiling.Value, "Error").Should().BeFalse();

        // A projection copying an arbitrary value is neither a dispatch nor a terminal write —
        // and it IS readable, so it must not trip the fail-closed assertion.
        var copy = Single("new Snapshot\n{\n    CompilationStatus = definition.CompilationStatus,\n};");
        Mentions(copy.Value, "Pending").Should().BeFalse();
        Mentions(copy.Value, "Ok").Should().BeFalse();
        IsReadable(copy.Value).Should().BeTrue("a plain status copy is not a dispatch");

        // 🚨 The shape the member-naming rule is BLIND to, and therefore the shape that must fail
        // closed: a status computed above and assigned through a local. It commits Pending while
        // naming nothing — exactly how door 5 (the sources watcher's parked auto-retry) looked
        // before #3390, which is why it was one of the seven that never stamped.
        var opaque = Single(
            "var status = live ? def.CompilationStatus : CompilationStatus.Pending;\n"
            + "return curr with\n{\n    CompilationStatus = status\n};");
        Mentions(opaque.Value, "Pending").Should().BeFalse(
            "this is the blind spot — the assignment names no member at all");
        IsReadable(opaque.Value).Should().BeFalse(
            "so it must be REFUSED rather than read as benign, or a new door opens silently");

        // …and a method call is the same case.
        IsReadable(" StatusFor(def) ").Should().BeFalse();
        // Readable shapes stay readable.
        IsReadable(" CompilationStatus.Compiling ").Should().BeTrue();
        IsReadable("\n    a ? CompilationStatus.Unavailable\n      : CompilationStatus.Error")
            .Should().BeTrue();

        // A comparison is not a write, and neither is prose in a comment.
        Classify("if (def.CompilationStatus == CompilationStatus.Pending) return curr;")
            .Should().BeEmpty();
        Classify("{\n    // flips CompilationStatus = Pending so the watcher fires\n}")
            .Should().BeEmpty();

        // 🚨 …and neither is prose in a LOG TEMPLATE. This file's messages say things like
        // "flipping CompilationStatus=Pending to rebuild", and eight of them were reported as
        // unreadable writes before the scanner learned to blank strings. Left unblanked they do
        // worse than produce noise: a brace inside one derails the initializer walk, and they pad
        // the floors below so a scan that stopped seeing real code still looks busy.
        Classify("logger.LogInformation(\n    \"flipping CompilationStatus = Pending to rebuild\");")
            .Should().BeEmpty();
        Classify("var s = @\"CompilationStatus = Pending {and a brace}\";").Should().BeEmpty();
        Classify("var s = $\"\"\"\nCompilationStatus = CompilationStatus.Pending\n\"\"\";")
            .Should().BeEmpty();

        // Blanking preserves offsets, so reported line numbers still point at the real line.
        Single("var s = \"CompilationStatus = Pending\";\nreturn def with\n{\n"
               + "    CompilationStatus = CompilationStatus.Ok,\n    DispatchedBuildInputs = null,\n};")
            .Line.Should().Be(4);
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
    private static List<StatusWrite> Classify(string source)
    {
        const string Property = "CompilationStatus";
        // 🚨 Comments and string literals FIRST, and blanked in place rather than skipped: this
        // file's log templates are full of prose like "flipping CompilationStatus=Pending to
        // rebuild", and a brace inside one would also derail the initializer walk. Blanking
        // preserves every index and line number, so the reports still point at real lines.
        var code = Blank(source);
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
    /// <summary>
    /// Whether the expression can COMMIT <paramref name="member"/> — i.e. names it in a VALUE
    /// position, not merely in a pattern test.
    ///
    /// <para>🚨 Pattern positions are stripped first, and that distinction is the whole point.
    /// A pass-through such as
    /// <c>def.CompilationStatus is CompilationStatus.Pending or CompilationStatus.Compiling
    /// ? def.CompilationStatus : CompilationStatus.Unavailable</c>
    /// PRESERVES a status a door already produced and otherwise commits <c>Unavailable</c>. It can
    /// never write <c>Pending</c> — that member appears only after <c>is</c>/<c>or</c>. Matching on
    /// the bare text called it a second Pending door and reddened #3583's delivery fix over an
    /// expression that dispatches nothing.</para>
    ///
    /// <para>🚨 This NARROWS the match, so it could in principle blind the guard. Two things stop
    /// that, both already here: <see cref="KnownStatusWrites"/> / <see cref="KnownTerminalWrites"/>
    /// fail the run if the scan stops seeing the writes it is supposed to see, and
    /// <see cref="TheClassifier_SeesEveryShapeItClaimsTo"/> pins a literal write in a value
    /// position as still caught. Stripping is confined to text directly after a pattern keyword;
    /// an assignment, a ternary arm and a plain initializer are untouched.</para>
    /// </summary>
    private static bool Mentions(string value, string member) =>
        ValuePositionsOnly(value).Contains("CompilationStatus." + member, System.StringComparison.Ordinal);

    /// <summary>The expression with every <c>is</c>/<c>or</c>/<c>and</c>/<c>not</c> pattern operand
    /// blanked, so only what the expression can actually COMMIT remains.</summary>
    private static string ValuePositionsOnly(string value) =>
        PatternOperand.Replace(value, " ");

    private static readonly System.Text.RegularExpressions.Regex PatternOperand =
        new(@"\b(?:is|or|and|not)\s+CompilationStatus\.[A-Za-z_]+",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly string[] Members =
        ["Pending", "Compiling", "Ok", "Error", "Unavailable"];

    /// <summary>
    /// Whether this guard can say what the expression commits. Two readable shapes:
    /// it names <see cref="CompilationStatus"/> members literally, or it is a plain copy of
    /// another object's status. Everything else — an opaque local, a method call, a ternary over
    /// locals — is refused rather than assumed benign; see
    /// <see cref="NoCompilationStatusIsWrittenThroughAnExpressionThisGuardCannotRead"/>.
    /// </summary>
    private static bool IsReadable(string value)
    {
        if (Members.Any(m => Mentions(value, m))) return true;

        // A copy: `x.CompilationStatus`, `x.Y.CompilationStatus`, `((T)x).CompilationStatus` — a
        // dotted path and nothing else. It transfers a status a door already produced; it is not
        // itself a dispatch.
        var v = value.Trim().TrimEnd(',');
        if (!v.EndsWith(".CompilationStatus", System.StringComparison.Ordinal)) return false;
        return v.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '?' or '!' or '(' or ')');
    }

    /// <summary>
    /// Every comment, string and char literal replaced by spaces (newlines kept), so indices and
    /// line numbers are unchanged and only real code is classified. Handles line and block
    /// comments, ordinary, verbatim (<c>@"…"</c>, doubled quotes) and raw (<c>"""…"""</c>) strings.
    /// </summary>
    private static string Blank(string code)
    {
        var sb = new System.Text.StringBuilder(code.Length);
        var i = 0;
        void Skip(char c) => sb.Append(c == '\n' ? '\n' : ' ');

        while (i < code.Length)
        {
            var c = code[i];

            if (c == '/' && i + 1 < code.Length && code[i + 1] == '/')
            {
                while (i < code.Length && code[i] != '\n') { sb.Append(' '); i++; }
                continue;
            }
            if (c == '/' && i + 1 < code.Length && code[i + 1] == '*')
            {
                sb.Append("  "); i += 2;
                while (i < code.Length
                       && !(code[i] == '*' && i + 1 < code.Length && code[i + 1] == '/'))
                { Skip(code[i]); i++; }
                if (i < code.Length) { sb.Append("  "); i += 2; }
                continue;
            }
            if (c == '\'')
            {
                sb.Append(' '); i++;
                while (i < code.Length && code[i] != '\'')
                {
                    if (code[i] == '\\') { sb.Append(' '); i++; if (i < code.Length) { sb.Append(' '); i++; } continue; }
                    Skip(code[i]); i++;
                }
                if (i < code.Length) { sb.Append(' '); i++; }
                continue;
            }
            if (c == '"')
            {
                var open = 0;
                while (i + open < code.Length && code[i + open] == '"') open++;

                if (open >= 3)   // raw string
                {
                    for (var k = 0; k < open; k++) sb.Append(' ');
                    i += open;
                    while (i < code.Length)
                    {
                        if (code[i] == '"')
                        {
                            var run = 0;
                            while (i + run < code.Length && code[i + run] == '"') run++;
                            for (var k = 0; k < run; k++) sb.Append(' ');
                            i += run;
                            if (run >= open) break;
                            continue;
                        }
                        Skip(code[i]); i++;
                    }
                    continue;
                }

                var verbatim = false;
                for (var k = i - 1; k >= 0 && (code[k] == '$' || code[k] == '@'); k--)
                    if (code[k] == '@') verbatim = true;

                sb.Append(' '); i++;
                if (verbatim)
                {
                    while (i < code.Length)
                    {
                        if (code[i] == '"')
                        {
                            if (i + 1 < code.Length && code[i + 1] == '"') { sb.Append("  "); i += 2; continue; }
                            sb.Append(' '); i++; break;
                        }
                        Skip(code[i]); i++;
                    }
                }
                else
                {
                    while (i < code.Length && code[i] != '"' && code[i] != '\n')
                    {
                        if (code[i] == '\\') { sb.Append(' '); i++; if (i < code.Length) { sb.Append(' '); i++; } continue; }
                        sb.Append(' '); i++;
                    }
                    if (i < code.Length && code[i] == '"') { sb.Append(' '); i++; }
                }
                continue;
            }

            sb.Append(c); i++;
        }
        return sb.ToString();
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
