#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The other half of the redirect contract: every place that MINTS a <c>?returnUrl=</c> must mint a
/// LOCAL path.
///
/// <para><b>Why a second guard.</b> <c>RedirectSinksUseOnePolicyGuard</c> pins the SINK side — a
/// redirect that consumes a request-supplied target routes it through the one shared policy. That
/// guard was green throughout #5074, and correctly so: nothing consumed an unvalidated target. The
/// defect was at the SOURCE. <c>/authorize</c> minted its own return target as
/// <c>{Scheme}://{Host}{Path}{QueryString}</c>, and because every sink downstream refuses a
/// non-local target — which is the sinks doing their job — the value was DISCARDED rather than
/// followed. The login page substitutes nothing for a target it will not keep, so the sign-in
/// completed and landed on <c>"/"</c> with the original request gone: no authorization code, and an
/// MCP client waiting for a completion that could not arrive.</para>
///
/// <para>So the two guards answer different questions. The sink guard asks "is an incoming target
/// validated?" — a security question. This one asks "is an OUTGOING target one that validation will
/// keep?" — a correctness question, whose failure mode is silent and looks like an expired
/// credential. Neither subsumes the other, and the first cannot see the second by construction.</para>
///
/// <para><b>How the rule is decided.</b> For each minting site the guard resolves the interpolated
/// value to its assignment IN THE SAME FILE and refuses one composed as an absolute URL (a scheme
/// separator, or the request's scheme read at all). A value that is an inbound parameter of the
/// enclosing method is a FORWARD, not a mint, and belongs to the sink guard. Anything the guard
/// cannot resolve to one of those two fails closed and says so — an unresolvable site is not a
/// clean one.</para>
/// </summary>
public class RedirectSourcesMintLocalTargetsGuard
{
    /// <summary>The names a redirect target travels under — the sink guard's list, unchanged.</summary>
    private static readonly string[] TargetNames =
        ["returnUrl", "returnTo", "returnPath", "redirectUrl", "redirectTo"];

    /// <summary>
    /// A MINT: the parameter name inside a string, immediately followed by an interpolation hole.
    /// <c>"?returnUrl={x}"</c> mints; <c>"?returnPath=/"</c> is a constant and mints nothing.
    /// </summary>
    private static readonly Regex MintSite = new(
        @"[?&](" + string.Join("|", TargetNames) + @")=\{([^{}]{1,200})\}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Identifiers in an interpolated expression, innermost-first is not needed — all are checked.</summary>
    private static readonly Regex Identifier = new(@"[A-Za-z_][A-Za-z0-9_]*", RegexOptions.Compiled);

    /// <summary>Names that are calls/types in the expression rather than the value being carried.</summary>
    private static readonly string[] NotAValue =
        ["Uri", "EscapeDataString", "ReturnUrlPolicy", "Sanitize", "string", "Join", "Format",
         "LocalUrl", "LocalOrRoot", "LocalOrNull", "InstanceConnectFlow"];

    private static IEnumerable<string> Roots()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        // Both trees that compose portal URLs: the auth/portal surface and the framework.
        foreach (var relative in new[] { Path.Combine("memex", "Memex.Portal.Shared"), "src" })
        {
            var root = Path.Combine(dir!.FullName, relative);
            Assert.True(Directory.Exists(root), $"{root} not found — update this guard's roots.");
            yield return root;
        }
    }

    [Fact]
    public void Every_minted_return_target_is_a_local_path()
    {
        var offenders = new List<string>();
        var sitesSeen = 0;

        foreach (var root in Roots())
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in MintSite.Matches(text))
            {
                // 🚨 Prose, not code. Both connect endpoints DOCUMENT their routes as
                // `GET /connect/github?returnPath={path}` in an XML comment, where `{path}` is a
                // placeholder for the reader and names no C# value at all — the guard's fail-closed
                // branch reported it, correctly, as something it could not resolve. A comment mints
                // nothing, so the subject is the code line.
                if (IsInAComment(text, m.Index))
                    continue;

                sitesSeen++;
                var expression = m.Groups[2].Value;
                var name = Path.GetFileName(file);

                var carried = Identifier.Matches(expression)
                    .Select(i => i.Value)
                    .Where(i => !NotAValue.Contains(i, StringComparer.Ordinal))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();

                if (carried.Length == 0)
                {
                    offenders.Add(
                        $"{name}: {m.Value} — the guard could not tell what value this carries.");
                    continue;
                }

                var verdicts = carried.Select(id => Classify(text, id, m.Index)).ToArray();
                if (verdicts.Any(v => v.Absolute))
                    offenders.Add(
                        $"{name}: {m.Value} — carries {verdicts.First(v => v.Absolute).Detail}, "
                        + "which is an ABSOLUTE url.");
                else if (verdicts.All(v => !v.Resolved))
                    offenders.Add(
                        $"{name}: {m.Value} — no assignment or parameter found for "
                        + $"{string.Join("/", carried)}; the guard fails closed rather than "
                        + "assuming it is local.");
            }
        }

        // 🚨 The guard must have a subject. A refactor that renames these parameters, or moves the
        // portal surface out of this repo, would otherwise leave it green having checked nothing.
        Assert.True(sitesSeen > 0,
            "No returnUrl/returnPath minting site was found in either root. Either the roots above "
            + "are stale or the parameter names changed — a guard with no subject passes vacuously.");

        Assert.True(
            offenders.Count == 0,
            "These sites hand a redirect target onward that the shared local-only policy will NOT "
            + "keep. Every sink refuses a non-local target (correctly — that is the open-redirect "
            + "defence), so such a value is DISCARDED rather than followed: the user completes the "
            + "flow and lands on \"/\" with the original request gone, which reads as the flow "
            + "silently not working rather than as a rejected redirect (#5074). Mint "
            + "{Request.Path}{Request.QueryString}, never {Request.Scheme}://{Request.Host}… — the "
            + "login page and the endpoint are the same origin, so the absolute form carries no "
            + "information and costs the whole flow.\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// True when the offset sits on a line whose code has not started — a <c>//</c> / <c>///</c>
    /// line comment, or a line inside a block comment. Deliberately line-based rather than a real
    /// lexer: the only thing it has to separate is a documented ROUTE from a composed URL.
    /// </summary>
    private static bool IsInAComment(string text, int index)
    {
        var lineStart = text.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
        var prefix = text[lineStart..index].TrimStart();
        return prefix.StartsWith("//", StringComparison.Ordinal)
               || prefix.StartsWith("*", StringComparison.Ordinal)
               || prefix.StartsWith("/*", StringComparison.Ordinal);
    }

    /// <summary>What a carried identifier resolves to, looking only at the file it is used in.</summary>
    private readonly record struct Verdict(bool Resolved, bool Absolute, string Detail);

    /// <summary>
    /// Resolves <paramref name="id"/> to its nearest preceding assignment, or to an inbound
    /// parameter of the enclosing method. An assignment whose right-hand side composes a scheme and
    /// host is ABSOLUTE; a parameter is a forwarded inbound value (the sink guard's subject).
    /// </summary>
    private static Verdict Classify(string text, string id, int useIndex)
    {
        var before = text[..useIndex];

        var assignment = new Regex(
            @"\b(?:var|string\??)\s+" + Regex.Escape(id) + @"\s*=\s*([^;]{0,400});",
            RegexOptions.Compiled);
        var assignments = assignment.Matches(before);
        if (assignments.Count > 0)
        {
            var rhs = assignments[^1].Groups[1].Value;
            // Either spelling of "this is a whole URL": the separator itself, or the request's
            // scheme, which in this codebase is only ever read to build one.
            var absolute = rhs.Contains("://", StringComparison.Ordinal)
                           || rhs.Contains("Request.Scheme", StringComparison.Ordinal);
            return new Verdict(true, absolute, absolute ? $"`{id}` = {rhs.Trim()}" : $"`{id}`");
        }

        // A parameter of the enclosing declaration — a forwarded inbound value, governed by the
        // sink guard where it lands. Looked for in the nearest preceding parameter list.
        var parameter = new Regex(
            @"\(\s*(?:[^()]{0,600}?[,\s])?(?:\[[^\]]{0,80}\]\s*)?[A-Za-z_][A-Za-z0-9_.<>?\[\]]*\s+"
            + Regex.Escape(id) + @"\s*[,)=]",
            RegexOptions.Compiled);
        return parameter.IsMatch(before)
            ? new Verdict(true, false, $"`{id}` (inbound parameter)")
            : new Verdict(false, false, $"`{id}`");
    }
}
