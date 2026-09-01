using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 <b>ZERO TOLERANCE — a permission check may never be swallowed into a verdict.</b>
///
/// <para>The banned shape is
/// <c>hub.CheckPermission(…).Catch&lt;bool, Exception&gt;(_ =&gt; Observable.Return(false))</c>,
/// and it is banned in <c>HubPermissionExtensions</c>'s own doc — "use
/// <c>CheckPermissionOutcome</c>, never this, whenever the answer decides what a user is SHOWN or
/// TOLD". A fold that FAULTS, or that terminates without emitting (#2742), reached NO verdict; the
/// catch turns it into the same <c>false</c> a real denial produces, and from there no caller can
/// recover the difference. The user is told they lack a permission they may well hold, the
/// degraded dependency is never reported as retryable, and nothing is logged — issues #974 (the
/// pipeline), #2742 (the silent terminal) and #2901 (the anonymous gate) are three instances of
/// this one shape.</para>
///
/// <para><b>Why a guard and not a review rule.</b> The rule was written down, in the XML doc of the
/// very method that replaces the shape — and the shape was then added twice AFTERWARDS, on the
/// anonymous read path, which is the surface where the lie is most expensive: a logged-out visitor
/// gets bounced to <c>/login</c> for a page that may be public. A prose rule beside the cure is
/// evidently not enough; a compile-time-cheap ratchet at ZERO is.</para>
///
/// <para><b>The swallow-to-<c>true</c> direction is banned too</b>, and it is the worse one: it is
/// a straight authorization bypass rather than a false denial.</para>
///
/// <para>🚨 <b>There is no allow file, on purpose.</b> The tree is at zero. Anything that genuinely
/// cannot reach a verdict has a name for that already — <c>PermissionCheckOutcome.Undetermined</c>
/// — so an exemption would only ever be a request to keep lying. The remaining violator in the
/// fleet lives in <b>MeshWeaver.Plugins</b> (<c>BlazorHostingExtensions.AllowContentRead</c>) and
/// is out of this repo's reach; this guard does not pretend otherwise, it just keeps core at zero.</para>
/// </summary>
public class PermissionSwallowRatchetGuard
{
    private static readonly string[] ScannedRoots = ["src", "memex", "samples"];

    /// <summary>
    /// A permission check and the swallow live in one statement, so the window is "from the call to
    /// the next <c>;</c>" rather than a fixed character count — a long fluent chain cannot outrun it
    /// and a following statement cannot be pulled in.
    /// </summary>
    private static readonly Regex CheckCall = new(@"\bCheckPermission\s*\(", RegexOptions.Compiled);

    [Fact]
    public void No_permission_check_is_swallowed_into_a_verdict()
    {
        var root = SourceScan.FindRepoRoot();
        var violations = new List<string>();
        var checkedCalls = 0;

        foreach (var file in SourceScan.SourceFiles(root, ScannedRoots))
        {
            var text = File.ReadAllText(file);
            if (!text.Contains("CheckPermission", StringComparison.Ordinal))
                continue;

            // Masked: this guard's own subject matter appears verbatim in doc comments all over the
            // security tree (AnonymousGate's XML doc quotes the banned expression), and an
            // unmasked scan would report every one of those as a violation.
            var masked = SourceScan.MaskCommentsAndStrings(text);
            violations.AddRange(Scan(SourceScan.Relative(root, file), masked, ref checkedCalls));
        }

        // 🚨 Anti-vacuity. A rename of CheckPermission, or a move of this test project, must fail
        // HERE rather than sail through having inspected nothing — the failure mode #2844 named.
        //
        // The floor is MEASURED, not guessed: the tree holds 17 CheckPermission INVOCATIONS across
        // src/, memex/ and samples/ after this change (an earlier 20 came from a raw grep that also
        // counted the two overload DECLARATIONS and the three sites this change converted, so it
        // was never a count of what the scan actually inspects). 15 leaves room for ordinary
        // conversion to CheckPermissionOutcome without leaving room for the failure this catches,
        // which does not shave a site or two — it collapses the scan to ~0.
        //
        // 🚨 So: moving this DOWN as sites convert is correct; moving it down to make a RED go away
        // is the defect. If it fails, count the invocations before touching the number.
        Assert.True(
            checkedCalls >= 15,
            $"expected at least 15 CheckPermission call sites to inspect, found {checkedCalls} — "
            + "this guard is no longer looking at what it was written to protect. Count the "
            + "invocations in src/, memex/ and samples/ before touching this floor.");

        Assert.True(
            violations.Count == 0,
            "a permission check must never be swallowed into a verdict (see HubPermissionExtensions."
            + "CheckPermissionOutcome). Violations:\n" + string.Join("\n", violations));
    }

    /// <summary>
    /// 🚨 The scanner's own test — a zero-tolerance guard whose scan has a blind spot reports green
    /// over the very thing it forbids, and "no violations found" then means nothing.
    ///
    /// <para>The blind spot this pins is a real one, caught in review of this guard: taking the
    /// statement as "up to the next <c>;</c>" truncates at a semicolon inside a BLOCK LAMBDA, so a
    /// swallow written after one — <c>.Select(x =&gt; { Log(); return x; }).Catch(…)</c> — fell
    /// outside the window and was missed. <see cref="StatementEnd"/> now counts nesting, and this
    /// test is the difference between believing that and knowing it.</para>
    /// </summary>
    [Fact]
    public void TheScannerSeesAViolationHiddenBehindABlockLambda()
    {
        var checkedCalls = 0;

        // The exact shape that escaped the naive window: two semicolons at depth > 0 before .Catch.
        const string Hidden = """
            var x = hub.CheckPermission(path, userId, Permission.Read)
                .Select(v => { Log(v); return v; })
                .Catch<bool, Exception>(_ => Observable.Return(false));
            """;
        Assert.Single(Scan("planted/Hidden.cs", Hidden, ref checkedCalls));

        // The plain shape, as a floor: if this stopped matching, the test above could pass for the
        // wrong reason.
        const string Plain = """
            var x = hub.CheckPermission(path, userId, Permission.Read)
                .Catch<bool, Exception>(_ => Observable.Return(false));
            """;
        Assert.Single(Scan("planted/Plain.cs", Plain, ref checkedCalls));

        // 🚨 The over-reach control. A scanner that flags everything is as useless as one that
        // flags nothing, and it is the easier mistake to make when widening a window: the CURE
        // must not read as the offence, and a Catch belonging to the NEXT statement must not be
        // dragged into this one.
        const string Cure = """
            var x = hub.CheckPermissionOutcome(path, userId, Permission.Read).Take(1);
            var y = somethingElse.Catch<bool, Exception>(_ => Observable.Return(false));
            """;
        Assert.Empty(Scan("planted/Cure.cs", Cure, ref checkedCalls));

        const string Clean = """
            var x = hub.CheckPermission(path, userId, Permission.Read).Take(1);
            var y = other.Catch<bool, Exception>(_ => Observable.Return(false));
            """;
        Assert.Empty(Scan("planted/Clean.cs", Clean, ref checkedCalls));

        // 🚨 A DECLARATION is not a call site. Widening the window made this the worst match in
        // the tree: a signature is followed by a block, so the window ran past the whole method
        // and imported a LATER member's Catch. Both CheckPermission overloads were reported that
        // way before IsDeclaration; without this case the widening would have traded one blind
        // spot for a false positive on the very file that defines the cure.
        const string Declaration = """
            public static IObservable<bool> CheckPermission(
                this IMessageHub hub, string nodePath, Permission permission)
            {
                if (permission == Permission.None)
                    return Observable.Return(true);
                return hub.GetEffectivePermissions(nodePath).Select(p => p.HasFlag(permission));
            }

            private static IObservable<bool> Elsewhere(IObservable<bool> other) =>
                other.Catch<bool, Exception>(_ => Observable.Return(false));
            """;
        Assert.Empty(Scan("planted/Declaration.cs", Declaration, ref checkedCalls));
    }

    /// <summary>
    /// Every banned swallow attached to a <c>CheckPermission</c> call in <paramref name="masked"/>.
    /// <paramref name="checkedCalls"/> accumulates the call sites actually inspected, which is what
    /// the anti-vacuity floor asserts on.
    /// </summary>
    private static List<string> Scan(string relative, string masked, ref int checkedCalls)
    {
        var found = new List<string>();
        foreach (Match call in CheckCall.Matches(masked))
        {
            // CheckPermissionOutcome is the CURE — never the offence.
            if (masked.AsSpan(call.Index).StartsWith("CheckPermissionOutcome"))
                continue;

            // 🚨 A DECLARATION is not a call site, and it is the one match the widened window
            // handles worst: a signature is followed by a BLOCK, so the first depth-0 `;` lands
            // somewhere in a LATER member and drags that member's `.Catch` in. Both overloads of
            // CheckPermission were reported this way. Detected on the text before the match on its
            // own line, which carries the modifier for every declaration in this tree.
            if (IsDeclaration(masked, call.Index))
                continue;

            checkedCalls++;

            var statement = masked[call.Index..StatementEnd(masked, call.Index)];
            if (!statement.Contains("Catch", StringComparison.Ordinal))
                continue;

            var verdict = statement.Contains("Observable.Return(false)", StringComparison.Ordinal)
                ? "false"
                : statement.Contains("Observable.Return(true)", StringComparison.Ordinal)
                    ? "true"
                    : null;
            if (verdict is null)
                continue;

            var line = masked.Take(call.Index).Count(c => c == '\n') + 1;
            found.Add(
                $"{relative}:{line} — CheckPermission(...) "
                + $".Catch(… Observable.Return({verdict})). A fold that reached no verdict is "
                + "not a verdict: use hub.CheckPermissionOutcome(...) and branch on "
                + "PermissionCheckOutcome.IsUndetermined "
                + (verdict == "false"
                    ? "(a fault reported as a denial tells an entitled user to request rights they hold)."
                    : "(a fault reported as a GRANT is an authorization bypass)."));
        }

        return found;
    }

    /// <summary>
    /// The end of the statement that starts at <paramref name="from"/> — the first <c>;</c> at
    /// nesting depth ZERO.
    ///
    /// <para>🚨 Depth matters, and getting it wrong fails SILENTLY in the safe-looking direction.
    /// A plain <c>IndexOf(';')</c> stops at a semicolon inside a block lambda
    /// (<c>.Select(v =&gt; { Log(v); return v; })</c>), so the window closes BEFORE the
    /// <c>.Catch(…)</c> and the violation is never seen — a zero-tolerance guard reporting green
    /// over the exact shape it exists to forbid. The text is already masked, so string and comment
    /// brackets cannot skew the count.</para>
    ///
    /// <para>A depth that goes NEGATIVE means the enclosing member closed before any statement
    /// terminator did (an expression-bodied member inside a block); that ends the window too,
    /// rather than running on into the next member and importing its <c>.Catch</c>.</para>
    /// </summary>
    private static int StatementEnd(string masked, int from)
    {
        var depth = 0;
        for (var i = from; i < masked.Length; i++)
        {
            switch (masked[i])
            {
                // A `{` at depth zero opens a BLOCK, never a continuation of this expression, so
                // the statement is over — belt to IsDeclaration's braces.
                case '{' when depth == 0:
                    return i;
                case '(' or '{' or '[':
                    depth++;
                    break;
                case ')' or '}' or ']':
                    if (--depth < 0)
                        return i;
                    break;
                case ';' when depth == 0:
                    return i;
            }
        }

        return masked.Length;
    }

    /// <summary>
    /// True when the match at <paramref name="index"/> is a method DECLARATION rather than a call.
    /// Every declaration in this tree carries an access modifier ahead of the name on its own line,
    /// including the multi-line signatures.
    /// </summary>
    private static bool IsDeclaration(string masked, int index)
    {
        var lineStart = masked.LastIndexOf('\n', Math.Max(index - 1, 0)) + 1;
        var prefix = masked[lineStart..index];
        return prefix.Contains("public ", StringComparison.Ordinal)
               || prefix.Contains("private ", StringComparison.Ordinal)
               || prefix.Contains("internal ", StringComparison.Ordinal)
               || prefix.Contains("protected ", StringComparison.Ordinal);
    }
}
