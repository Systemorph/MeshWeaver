using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// A read that ESTABLISHES an identity must not be gated on that identity.
///
/// <para><c>workspace.GetQuery</c> re-applies per-user RLS at the consumer
/// (<c>SyncedQueryDataSourceExtensions.WrapWithPerUserRls</c>), and that filter resolves
/// permissions through <c>PermissionEvaluator</c> — which reads <c>AccessAssignment</c> nodes. So a
/// pre-identity read left unwrapped re-enters the permission fold from the request path and asks
/// the identity being built whether it may learn what it is. Seeing System, the wrap short-circuits
/// to the raw upstream: "no per-user filter, no service resolution, no recursion".</para>
///
/// <para>🚨 <b>Why a guard and not a review rule.</b> The filter can only ever REMOVE grants a
/// viewer legitimately holds, and every failure it causes is SILENT and plausible: a shorter role
/// list looks like fewer roles, a filtered <c>User</c> node looks like "no such account" (and gets
/// a signed-in user bounced to /onboarding to make a second one), a filtered invitation looks like
/// one that was never issued. Nothing throws and nothing is logged, so the only moment this is
/// cheap to catch is the moment it is written.</para>
///
/// <para>The scope is deliberately narrow — these are the reads that run BEFORE a viewer has a mesh
/// identity. It is NOT "every auth query must be System": <c>ApiTokenService.GetTokensForUser</c>
/// answers "show me MY tokens" for an established viewer, and per-user filtering there is the
/// feature, not the bug.</para>
/// </summary>
public class IdentityReadsRunAsSystemGuard
{
    /// <summary>
    /// Files whose reads are pre-identity. A null method set means EVERY <c>GetQuery</c> in the
    /// file must be a System read — true of the onboarding middleware, whose whole job is resolving
    /// a viewer before one exists. Naming methods instead restricts the rule to those.
    /// </summary>
    private static readonly (string File, string[]? Methods)[] PreIdentityReads =
    [
        ("memex/Memex.Portal.Shared/Authentication/OnboardingMiddleware.cs", null),
        ("memex/Memex.Portal.Shared/Authentication/InvitationService.cs", ["FindPendingInvitation"]),
    ];

    [Fact]
    public void Pre_identity_reads_run_as_system()
    {
        var root = SourceScan.FindRepoRoot();
        var violations = new List<string>();
        var checkedCalls = 0;

        foreach (var (relative, methods) in PreIdentityReads)
        {
            var path = Path.Combine(root, relative);
            Assert.True(File.Exists(path), $"guarded file no longer exists — update this guard: {relative}");

            // Masked, so a doc comment mentioning GetQuery (this file's subject matter is full of
            // them) cannot be mistaken for a call.
            var masked = SourceScan.MaskCommentsAndStrings(File.ReadAllText(path));

            // Member starts at 4-space indentation — enough to bracket a method in these files.
            var memberStarts = Regex
                .Matches(masked, @"\n    (?:public|internal|private|protected)[^\n]*", RegexOptions.None)
                .Select(m => (m.Index, Text: m.Value))
                .ToList();

            foreach (Match call in Regex.Matches(masked, @"\bGetQuery\s*\("))
            {
                var owner = memberStarts.LastOrDefault(m => m.Index < call.Index);
                var ownerName = MethodName(owner.Text) ?? "(unknown)";
                if (methods is not null && !methods.Contains(ownerName, StringComparer.Ordinal))
                    continue;

                checkedCalls++;
                var body = masked[owner.Index..call.Index];
                if (!body.Contains("RunAsSystem", StringComparison.Ordinal))
                    violations.Add(
                        $"{relative}: {ownerName} reads via GetQuery without RunAsSystem "
                        + $"(line {masked.Take(call.Index).Count(c => c == '\n') + 1})");
            }
        }

        // Anti-vacuity: a refactor that renames GetQuery or reshapes these files must fail here
        // rather than silently check nothing and stay green.
        Assert.True(
            checkedCalls >= 3,
            $"expected at least 3 pre-identity GetQuery calls to check, found {checkedCalls} — "
            + "the guard is no longer looking at anything it was written to protect");

        Assert.True(
            violations.Count == 0,
            "A read that establishes an identity ran under that identity. Wrap it in "
            + "AccessService.RunAsSystem(() => …) — never Observable.Using(() => "
            + "ImpersonateAsSystem(), …), whose store and restore land on different threads and "
            + "leave the subscriber latched.\n  " + string.Join("\n  ", violations));
    }

    private static string? MethodName(string? declaration)
    {
        if (declaration is null) return null;
        var m = Regex.Match(declaration, @"(\w+)\s*\(");
        return m.Success ? m.Groups[1].Value : null;
    }
}
