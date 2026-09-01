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

            foreach (Match call in CheckCall.Matches(masked))
            {
                // CheckPermissionOutcome is the CURE — never the offence.
                if (masked.AsSpan(call.Index).StartsWith("CheckPermissionOutcome"))
                    continue;

                checkedCalls++;

                var end = masked.IndexOf(';', call.Index);
                var statement = end < 0 ? masked[call.Index..] : masked[call.Index..end];
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
                violations.Add(
                    $"{SourceScan.Relative(root, file)}:{line} — CheckPermission(...) "
                    + $".Catch(… Observable.Return({verdict})). A fold that reached no verdict is "
                    + "not a verdict: use hub.CheckPermissionOutcome(...) and branch on "
                    + "PermissionCheckOutcome.IsUndetermined "
                    + (verdict == "false"
                        ? "(a fault reported as a denial tells an entitled user to request rights they hold)."
                        : "(a fault reported as a GRANT is an authorization bypass)."));
            }
        }

        // 🚨 Anti-vacuity. A rename of CheckPermission, or a move of this test project, must fail
        // HERE rather than sail through having inspected nothing — the failure mode #2844 named.
        Assert.True(
            checkedCalls >= 20,
            $"expected at least 20 CheckPermission call sites to inspect, found {checkedCalls} — "
            + "this guard is no longer looking at what it was written to protect. Fix the scan; "
            + "never lower this floor to make it green.");

        Assert.True(
            violations.Count == 0,
            "a permission check must never be swallowed into a verdict (see HubPermissionExtensions."
            + "CheckPermissionOutcome). Violations:\n" + string.Join("\n", violations));
    }
}
