#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 <b>The prod synthetic probe may name HOSTS, never a deployment's installed content</b> (#3578).
///
/// <para><b>What it cost.</b> The probe's matrix paired <c>memex.systemorph.com</c> with
/// <c>/api/content/AgenticPrimer/content/og.png</c> — an asset of a package that is not installed on
/// that portal and never was. From 2026-08-31 it failed every 15 minutes, 59 of its last 60 runs,
/// against a completely healthy portal. A check that is red in an empty room is worse than no check:
/// after a day nobody reads it, and every real failure it would have caught arrives wearing the
/// colour everyone has learned to ignore. That is the same defect as a gate that skips.</para>
///
/// <para><b>Why it was broken by construction.</b> A per-deployment install decision is not a
/// property of the platform, and this repository cannot see which portal has what — the deployment
/// inventory is deliberately private (<c>chart-drift.yml</c>: "do NOT move the overlays into this
/// public repo to dodge the credential — that would publish the deployment inventory"). So a public
/// workflow hard-coding what a named portal serves encodes a private fact it has no way to keep
/// current. It could only ever drift, and when it drifted it blamed the portal.</para>
///
/// <para><b>Why a guard rather than care.</b> The next wrong target will not look wrong. It will
/// look exactly like a target that used to work — a plausible path, green on the day it is written,
/// and silently a per-deployment fact. Review cannot see the difference; this can.</para>
///
/// <para>The rule is narrow and mechanical: every <c>/api/content/…</c> path in the workflow must
/// either live under the platform-shipped <c>Doc/</c> tree (an <c>EmbeddedResource</c> in
/// MeshWeaver.Documentation, published on the content route by <c>AddDocumentation</c>, readable
/// anonymously via <c>Doc/_Policy</c> — present on every deployment with no install decision) or be
/// the deliberate impossible-path negative control. Anything else is a package, a space or a course.
/// Full reasoning: <c>Doc/Architecture/SyntheticProbeTargets</c>.</para>
/// </summary>
public class SyntheticProbeNamesNoInstalledContentGuard
{
    private const string Workflow = ".github/workflows/prod-synthetic-probe.yml";

    /// <summary>The one tree this repository ships itself, so a target under it cannot drift.</summary>
    private const string PlatformPrefix = "/api/content/Doc/";

    /// <summary>
    /// The negative control. It must NOT resolve — that is its entire job — so it is exempt from the
    /// platform-prefix rule and instead has to be visibly impossible.
    /// </summary>
    private const string AbsentMarker = "__probe_absent__";

    private static string WorkflowBody()
    {
        var path = Path.Combine(SourceScan.FindRepoRoot(), Workflow);

        // 🚨 THE DENOMINATOR. A guard whose subject was renamed or deleted, and whose own path was
        // not, passes having checked nothing — the failure mode this repo keeps re-learning. Assert
        // the file is there before asserting anything about it.
        Assert.True(File.Exists(path),
            $"{Workflow} does not exist. This guard is the only thing standing between the prod "
            + "probe and another eight-day red for a true statement (#3578), so a rename must "
            + "re-point it deliberately rather than leaving it green over an absent file.");

        return File.ReadAllText(path);
    }

    [Fact]
    public void EveryContentTarget_IsPlatformShipped_OrTheNegativeControl()
    {
        var body = WorkflowBody();

        // Paths as they appear in the file: an env value, a URL fragment, or prose in a comment.
        // Comments are deliberately INCLUDED — the old targets are named in this file's own header
        // as the thing not to do, and this assertion has to be able to tell that apart from a live
        // one, which it does by requiring the citation to sit on a comment line.
        var matches = Regex.Matches(body, @"/api/content/[^\s""'`,)]+");
        Assert.True(matches.Count > 0,
            $"{Workflow} names no /api/content path at all. The probe exists to exercise the "
            + "cross-silo content hop; if that assertion is gone, this guard is checking nothing.");

        var lines = body.Split('\n');
        var offenders = new List<string>();

        foreach (Match m in matches)
        {
            var value = m.Value;
            if (value.StartsWith(PlatformPrefix, StringComparison.Ordinal)) continue;
            if (value.Contains(AbsentMarker, StringComparison.Ordinal)) continue;

            // A comment may cite a historical target; executable YAML may not.
            var lineNumber = body.Take(m.Index).Count(c => c == '\n');
            var line = lines[lineNumber].TrimStart();
            if (line.StartsWith("#", StringComparison.Ordinal)) continue;

            offenders.Add($"line {lineNumber + 1}: {value}");
        }

        Assert.True(offenders.Count == 0,
            $"{Workflow} asserts content that is not shipped by the platform:\n  "
            + string.Join("\n  ", offenders)
            + $"\n\nA target must start with '{PlatformPrefix}' (shipped in the portal image, so it "
            + "cannot drift with an install decision) or be the impossible-path negative control. "
            + "A package, space or course path is a per-deployment install decision, which this "
            + "repository cannot see and must not encode — see Doc/Architecture/SyntheticProbeTargets.");
    }

    [Fact]
    public void TheMatrix_NamesHostsOnly()
    {
        var body = WorkflowBody();

        var matrix = Regex.Match(body, @"^\s*portal:\s*$(?<entries>(\s*\n\s*-\s.+)+)", RegexOptions.Multiline);
        Assert.True(matrix.Success,
            $"{Workflow} no longer declares a 'portal:' matrix list. The probe used to pair each "
            + "portal with a per-deployment asset in that matrix (#3578); if its shape changed, "
            + "re-point this guard rather than deleting it.");

        var entries = matrix.Groups["entries"].Value
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("-", StringComparison.Ordinal))
            .Select(l => l.TrimStart('-').Trim())
            .ToList();

        Assert.True(entries.Count > 0, "the 'portal:' matrix is empty — the probe would run against nothing.");

        var contentShaped = entries.Where(e => e.Contains('/', StringComparison.Ordinal)).ToList();

        Assert.True(contentShaped.Count == 0,
            "the prod probe's matrix must name HOSTS only; these entries carry a path:\n  "
            + string.Join("\n  ", contentShaped)
            + "\n\nA path in the matrix is how #3578 happened — it pairs a host with content this "
            + "repository cannot verify the host has. What a deployment serves is read from the "
            + "deployment at probe time (/sitemap.xml), never declared here.");
    }

    /// <summary>
    /// The two assertions that make a red readable are easy to delete by accident, because removing
    /// either leaves a probe that still passes on a healthy portal. Without the control, "asset
    /// missing" and "route down" are the same evidence — the wrong inference #3578 was filed on.
    /// Without the sitemap read, the probe stops holding a portal to its own declaration and the
    /// only way back to per-portal coverage is a hard-coded target.
    /// </summary>
    [Fact]
    public void TheNegativeControl_AndTheDeploymentsOwnDeclaration_AreBothStillAsserted()
    {
        var body = WorkflowBody();

        Assert.Contains(AbsentMarker, body, StringComparison.Ordinal);
        Assert.Contains("sitemap.xml", body, StringComparison.Ordinal);

        // An empty declaration must be LOUD. A loop over an empty list exits 0 having asserted
        // nothing, and GitHub paints that green — so the count is asserted before it is used.
        Assert.True(
            Regex.IsMatch(body, @"count""?\s*-eq\s*0") || Regex.IsMatch(body, @"\$count""\s*-eq\s*0"),
            $"{Workflow} no longer fails on a sitemap that declares ZERO roots. An empty "
            + "denominator makes every sample loop a no-op that exits 0 having checked nothing, "
            + "which is the 'green on no evidence' shape AGENTS.md forbids.");
    }
}
