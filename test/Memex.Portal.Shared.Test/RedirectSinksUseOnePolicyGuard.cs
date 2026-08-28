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
                var fromRequest = arg.Contains("returnUrl", StringComparison.OrdinalIgnoreCase)
                                  || arg.Contains("returnTo", StringComparison.OrdinalIgnoreCase);
                if (!fromRequest) continue;
                // 🚨 Look at the ENCLOSING REGION, not only the argument. Sanitizing into a local
                // and redirecting that is correct and common — EaConsentController does exactly
                // this, and an argument-only rule reported all four of its sites as open redirects
                // when none of them is. Flagging correct code is how a guard gets suppressed.
                var from = Math.Max(0, m.Index - 1500);
                var region = text[from..m.Index] + arg;
                if (region.Contains("Sanitize", StringComparison.Ordinal)
                    || region.Contains("SafeLocal", StringComparison.Ordinal)
                    || region.Contains("SafeReturnUrl", StringComparison.Ordinal)) continue;
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
    /// …and only ONE implementation of the rule exists. A second copy is how the last bypass got in,
    /// so a new hand-rolled local-URL check fails here even if its own logic happens to be right.
    /// </summary>
    [Fact]
    public void Only_ReturnUrlPolicy_implements_the_local_url_rule()
    {
        var copies = Directory
            .EnumerateFiles(SharedRoot(), "*.cs", SearchOption.AllDirectories)
            .Where(f => !Path.GetFileName(f).Equals("ReturnUrlPolicy.cs", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains("StartsWith(\"//\"", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(
            copies.Count == 0,
            "A second implementation of the protocol-relative check has appeared. There must be "
            + "exactly one (ReturnUrlPolicy.Sanitize) — the duplicate in GitHubLoginEndpoints was "
            + "subtly wrong for two years' worth of copies (#2302). Files: "
            + string.Join(", ", copies));
    }
}
