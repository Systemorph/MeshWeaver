#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// Every redirect target that comes from the request must go through <c>ReturnUrlPolicy.Sanitize</c>.
///
/// <para><b>Why a ratchet and not just tests.</b> <c>ReturnUrlPolicy</c> was already correct and
/// already covered — its theory pins <c>//evil</c>, <c>/\evil</c>, absolute URLs and
/// <c>javascript:</c>. The defect (#2302) was not the policy; it was that this assembly grew THREE
/// separate copies of the rule and two were wrong: <c>GitHubLoginEndpoints.SafeLocal</c> rejected
/// <c>//host</c> but accepted <c>/\host</c> (browsers normalise the backslash, so it still became a
/// protocol-relative external redirect), and <c>DevAuthController</c> validated nothing at all at
/// two sites. Testing the policy harder would not have found any of that.</para>
///
/// <para>So the invariant worth pinning is not "the policy is right" but "there is only one
/// policy, and every sink uses it".</para>
/// </summary>
public class RedirectSinksUseOnePolicyGuard
{
    /// <summary>The names a request-supplied redirect target travels under in this assembly.</summary>
    private static readonly string[] RequestTargetNames =
        ["returnUrl", "returnTo", "returnPath", "redirectUrl", "redirectTo"];

    /// <summary>An index-based protocol-relative test, e.g. <c>u[1] == '/'</c>.</summary>
    private static readonly Regex ProtocolRelativeIndexCheck =
        new(@"\[\s*1\s*\]\s*[=!]=\s*'[/\\\\]'", RegexOptions.Compiled);

    /// <summary>A call to the shared policy, or a helper that delegates to it by convention.</summary>
    private static readonly Regex SanitizerCall =
        new(@"\b(Sanitize|Safe[A-Za-z]*)\s*\(", RegexOptions.Compiled);

    private static string SharedRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var root = Path.Combine(dir!.FullName, "memex", "Memex.Portal.Shared");
        Assert.True(Directory.Exists(root), $"{root} not found — update this guard's path.");
        return root;
    }

    /// <summary>
    /// A redirect whose argument mentions a request-supplied value must name the policy in the same
    /// expression. Matching the ARGUMENT rather than the whole file is what makes this specific:
    /// a file may redirect to a constant and separately mention returnUrl elsewhere.
    /// </summary>
    [Fact]
    public void No_redirect_takes_a_request_value_without_the_shared_policy()
    {
        var offenders = new List<string>();
        var sink = new Regex(@"Redirect\(\s*([^;]{0,200}?)\)\s*;", RegexOptions.Compiled);

        foreach (var file in Directory.EnumerateFiles(SharedRoot(), "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in sink.Matches(text))
            {
                var arg = m.Groups[1].Value;
                // 🚨 Every name a request-supplied target travels under, not just the obvious two.
                // The first version knew only returnUrl/returnTo and therefore did not ratchet
                // GitHubConnectEndpoints, whose sink is called returnPath — and that one was a LIVE
                // open redirect (it accepted "//host"). A guard that names its subjects by
                // convention misses the sink that did not follow the convention.
                var fromRequest = RequestTargetNames.Any(
                    n => arg.Contains(n, StringComparison.OrdinalIgnoreCase));
                if (!fromRequest) continue;
                // 🚨 Look at the ENCLOSING REGION, not only the argument. Sanitizing into a local
                // and redirecting that is correct and common — EaConsentController does exactly
                // this, and an argument-only rule reported all four of its sites as open redirects
                // when none of them is. Flagging correct code is how a guard gets suppressed.
                // A string LITERAL is not a request value, however it is spelled. The first
                // version flagged Redirect("/connect/github?returnPath=/") — a constant that merely
                // mentions the parameter name.
                if (arg.TrimStart().StartsWith('"')) continue;

                var from = Math.Max(0, m.Index - 1500);
                var region = text[from..m.Index] + arg;
                // Match the CONVENTION (Sanitize / Safe*) rather than an enumerated list of helper
                // names. Enumerating them missed SafeReturn and would have missed the next one; the
                // "only one implementation" test below is what stops a Safe*-named impostor.
                if (SanitizerCall.IsMatch(region)) continue;
                offenders.Add($"{Path.GetFileName(file)}: Redirect({arg.Trim()})");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "These redirects take a request-supplied target without routing it through "
            + "ReturnUrlPolicy.Sanitize — that is an open redirect (#2302). Do not re-implement the "
            + "check: a hand-written copy already shipped that rejected \"//host\" but accepted "
            + "\"/\\host\", which browsers normalise back into a protocol-relative external URL.\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// …and every local-URL helper DELEGATES rather than re-implements.
    ///
    /// <para>🚨 This is the invariant, and the weaker forms of it are worth recording because I
    /// wrote both. Scanning for <c>StartsWith("//")</c> misses an index-based check
    /// (<c>u[1] == '/'</c>), which is how one copy was written. And allowing any call named
    /// <c>Safe*</c> to count as sanitisation is worse than useless: <c>GitHubConnectEndpoints</c>
    /// had a <c>SafeReturn</c> that tested only for a leading <c>/</c> and therefore passed
    /// <c>//evil.example</c> straight through to <c>Redirect</c> — a live open redirect wearing a
    /// reassuring name. A guard that trusts the name certifies the impostor.</para>
    ///
    /// <para>So: find every <c>Safe…</c>/<c>Sanitize…</c> helper outside the policy itself, and
    /// require its body to call <see cref="ReturnUrlPolicy.Sanitize"/>. Delegation is checkable;
    /// correctness of a hand-rolled copy is not.</para>
    /// </summary>
    [Fact]
    public void Every_local_url_helper_delegates_to_the_one_policy()
    {
        var decl = new Regex(
            // Names in the local-URL family only. A bare `Sanitize` is not one: UpdatePolicySettingsTab
            // has a markdown sanitiser by that name, and flagging it would be the kind of false
            // positive that gets a guard suppressed rather than fixed.
            @"(?:internal|private|public|protected)\s+static\s+string\??\s+((?:Safe|Sanitize)\w*(?:Return|Url|Local)\w*|SafeReturn)\s*\([^)]*\)",
            RegexOptions.Compiled);
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(SharedRoot(), "*.cs", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file).Equals("ReturnUrlPolicy.cs", StringComparison.Ordinal)) continue;
            var text = File.ReadAllText(file);
            foreach (Match m in decl.Matches(text))
            {
                // The body: from the declaration to the next declaration or 1200 chars, whichever
                // is nearer. Enough to see a delegating one-liner or a hand-rolled block.
                var bodyEnd = Math.Min(text.Length, m.Index + 1200);
                var body = text[m.Index..bodyEnd];
                if (body.Contains("ReturnUrlPolicy.Sanitize", StringComparison.Ordinal)) continue;
                offenders.Add($"{Path.GetFileName(file)}: {m.Groups[1].Value}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "These local-URL helpers do not delegate to ReturnUrlPolicy.Sanitize. A hand-rolled "
            + "copy is how three separate open redirects shipped (#2302) — one of them named "
            + "SafeReturn while accepting \"//evil.example\". Delegate; do not re-implement: "
            + string.Join(", ", offenders));
    }
}
