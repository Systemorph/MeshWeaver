using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 <b>NO CREATE PATH MINTS <c>CreatedDate</c> FROM A BARE <c>DateTimeOffset.UtcNow</c></b>
/// (<see href="https://github.com/Systemorph/MeshWeaver/issues/4506">#4506</see>).
///
/// <para><b>What this protects.</b> <c>CreatedDate</c> is the token the create rollback
/// (<c>CompensateFailedCreate</c>, #638/#4503) compares to decide whether a row is the one this
/// request wrote — an IN-MEMORY value against the same value AFTER a round trip through a column. A
/// <c>DateTimeOffset</c> tick is 100 ns; PostgreSQL <c>timestamptz</c> holds MICROSECONDS. A stamp
/// minted from a raw <c>UtcNow</c> therefore comes back from its own row DIFFERENT, the check reads
/// that as "somebody else's node", and the rollback stands down on the row it just wrote — the
/// unrecoverable ghost #638 exists to prevent. The mint goes through
/// <c>MeshNode.StorageStableNow()</c> / <c>MeshNode.StorageStable(…)</c> instead.</para>
///
/// <para>🚨 <b>Why a source guard and not three more behavioural tests</b> (Copilot review, PR
/// #4511). There are THREE mint sites — the singular create, the bulk create, and
/// <c>PackageInstaller</c>'s direct write — and a revert at any one of them re-arms the defect while
/// every test aimed at the other two stays green. Two of the three are covered behaviourally by
/// <c>RollbackLineageSurvivesTheStoresTimestampResolutionTest</c>; the third is a LOCAL FUNCTION
/// inside a package install, in a project that has no test project at all, so pinning it
/// behaviourally would mean standing up a whole install fixture for a mint whose value no rollback
/// ever reads. This guard covers all three — and every site added later, which no per-site test
/// can — by checking the one property that actually matters: nothing assigns <c>CreatedDate</c> from
/// a raw <c>UtcNow</c>.</para>
///
/// <para>There is deliberately NO allow file. A stamp that cannot survive its own row is not a debt
/// to be budgeted; it is the defect.</para>
/// </summary>
public class CreateStampsAreStorageStableRatchetGuard
{
    /// <summary>The roots whose <c>CreatedDate</c> assignments are the framework's own.</summary>
    private static readonly string[] ScannedRoots = ["src"];

    /// <summary>
    /// A timestamp local and what it was minted from — <c>var now = DateTimeOffset.UtcNow;</c> or
    /// <c>var now = MeshNode.StorageStableNow();</c>.
    ///
    /// <para>🚨 Both are captured, not just the clock one, because the guard resolves a name to its
    /// NEAREST PRECEDING declaration. One file holds several create paths, each declaring its own
    /// <c>now</c>; a file-wide name set flagged a correct site merely because an unrelated method
    /// further up read the clock — a false positive measured while building this guard.</para>
    /// </summary>
    private static readonly Regex TimestampLocal = new(
        @"\b(?:var|DateTimeOffset|DateTime)\s+(\w+)\s*=\s*"
        + @"(Date(?:Time|TimeOffset)\.UtcNow|MeshNode\.StorageStableNow\(\))\s*;",
        RegexOptions.Compiled);

    /// <summary>
    /// Every assignment TO the property. <c>(?!=)</c> is load-bearing: without it the comparison in
    /// <c>CreatedDate = n.CreatedDate == default ? …</c> matched a second time and reported a
    /// mangled duplicate of the same site.
    /// </summary>
    private static readonly Regex CreatedDateAssignment = new(
        @"\bCreatedDate\s*=(?!=)\s*", RegexOptions.Compiled);

    [Fact]
    public void NoCreatePathStampsCreatedDateFromARawUtcNow()
    {
        var root = SourceScan.FindRepoRoot();
        var failures = new List<string>();

        foreach (var file in SourceScan.SourceFiles(root, ScannedRoots))
        {
            // Masked: a doc comment explaining the defect necessarily SPELLS
            // "DateTimeOffset.UtcNow", and this very repo now carries several. A scanner that read
            // comments would fail on the explanation of the rule it enforces.
            var code = SourceScan.MaskCommentsAndStrings(File.ReadAllText(file));
            var offenders = Offenders(code).ToArray();
            if (offenders.Length == 0)
                continue;

            var relative = SourceScan.Relative(root, file);
            failures.AddRange(offenders.Select(o =>
                $"  {relative}({LineOf(code, o.Offset)}): CreatedDate = {o.Rhs.Trim()}"));
        }

        Assert.True(failures.Count == 0,
            "A create path stamps CreatedDate from a raw clock read. That value cannot survive its "
            + "own row (PostgreSQL timestamptz holds microseconds; a DateTimeOffset tick is 100 ns), "
            + "so the create rollback compares its own stamp against a truncation of itself, decides "
            + "the row is somebody else's, and leaves the partially-created node behind — #4506.\n"
            + "Mint through MeshNode.StorageStableNow(), and floor a caller-supplied stamp with "
            + "MeshNode.StorageStable(...). There is no allow file for this on purpose.\n"
            + string.Join("\n", failures));
    }

    /// <summary>
    /// 🚨 THE NEGATIVE CONTROL, and it is not optional: a guard whose subject moved while its
    /// detector did not passes having checked nothing. This runs the SAME detector over the exact
    /// shape the fix removed and asserts it is caught — so a green verdict above means "the tree is
    /// clean", never "the regex stopped matching".
    /// </summary>
    [Fact]
    public void TheDetectorCatchesTheShapeTheFixRemoved()
    {
        const string reverted = """
            var now = DateTimeOffset.UtcNow;
            var newNode = node with
            {
                State = MeshNodeState.Active,
                CreatedDate = node.CreatedDate == default ? now : node.CreatedDate,
                LastModified = now,
            };
            """;
        // ONE offender, not two: `CreatedDate = n.CreatedDate == default` used to match a second
        // time on the COMPARISON and report a mangled duplicate of the same site.
        Assert.Single(Offenders(reverted));

        const string inline = "var n = node with { CreatedDate = DateTimeOffset.UtcNow };";
        Assert.NotEmpty(Offenders(inline));

        // …and does NOT fire on the shapes that are correct, or the guard would block the fix it
        // exists to protect.
        const string fixedShape = """
            var now = MeshNode.StorageStableNow();
            var newNode = node with
            {
                CreatedDate = node.CreatedDate == default ? now : MeshNode.StorageStable(node.CreatedDate),
            };
            """;
        Assert.Empty(Offenders(fixedShape));

        const string propagation = "var n = node with { CreatedDate = existing.CreatedDate };";
        Assert.Empty(Offenders(propagation));

        // 🚨 AND IT MUST NOT CONDEMN A CORRECT SITE FOR A SIBLING'S CLOCK READ. One file holds
        // several create paths; an earlier `var now = DateTimeOffset.UtcNow;` in an unrelated method
        // made a file-wide name set flag the correct site below. Measured while building this guard,
        // and pinned here — a guard that reds for the wrong reason gets softened, and then it guards
        // nothing.
        const string twoMethodsOneFile = """
            void Elsewhere()
            {
                var now = DateTimeOffset.UtcNow;
                var touched = node with { LastModified = now };
            }
            void TheCreatePath()
            {
                var now = MeshNode.StorageStableNow();
                var created = node with { CreatedDate = node.CreatedDate == default ? now : now };
            }
            """;
        Assert.Empty(Offenders(twoMethodsOneFile));
    }

    /// <summary>
    /// Every <c>CreatedDate</c> assignment whose right-hand side reads the clock — directly, or
    /// through a local this file minted from it.
    /// </summary>
    private static IEnumerable<(int Offset, string Rhs)> Offenders(string code)
    {
        var declarations = TimestampLocal.Matches(code)
            .Select(m => (
                Name: m.Groups[1].Value,
                Offset: m.Index,
                IsClockRead: m.Groups[2].Value.EndsWith("UtcNow", StringComparison.Ordinal)))
            .ToArray();

        foreach (Match assignment in CreatedDateAssignment.Matches(code))
        {
            var rhs = RightHandSide(code, assignment.Index + assignment.Length);

            if (rhs.Contains("DateTimeOffset.UtcNow", StringComparison.Ordinal)
                || rhs.Contains("DateTime.UtcNow", StringComparison.Ordinal))
            {
                yield return (assignment.Index, rhs);
                continue;
            }

            // Resolve each name the right-hand side mentions to its NEAREST PRECEDING declaration —
            // the one C# itself would bind — so a sibling method's clock read cannot condemn this
            // site, and this site's own clock read cannot hide behind a later correct one.
            var bound = declarations
                .Where(d => d.Offset < assignment.Index)
                .Where(d => Regex.IsMatch(rhs, $@"\b{Regex.Escape(d.Name)}\b"))
                .GroupBy(d => d.Name, StringComparer.Ordinal)
                .Select(g => g.OrderBy(d => d.Offset).Last());

            if (bound.Any(d => d.IsClockRead))
                yield return (assignment.Index, rhs);
        }
    }

    /// <summary>
    /// The initializer's value, read to the terminator at nesting depth zero — a ternary spanning
    /// three lines is one right-hand side, and taking only the first line would miss the very shape
    /// both create paths are written in.
    /// </summary>
    private static string RightHandSide(string code, int start)
    {
        var depth = 0;
        for (var i = start; i < code.Length; i++)
        {
            var c = code[i];
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}')
            {
                if (depth == 0) return code[start..i];
                depth--;
            }
            else if (depth == 0 && c is ',' or ';') return code[start..i];
        }
        return code[start..];
    }

    private static int LineOf(string code, int offset) =>
        code.Take(offset).Count(c => c == '\n') + 1;
}
