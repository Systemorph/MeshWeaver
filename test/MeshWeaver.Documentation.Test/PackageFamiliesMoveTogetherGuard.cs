#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 A package FAMILY moves together or not at all — every member on one base version.
///
/// <para>Some package families pin each other EXACTLY, so bumping one member and not the rest is
/// not a cosmetic inconsistency: it is an unbuildable graph. It happened three times in a single
/// dependency sweep (2026-08-31, #2870), each time hidden behind the previous one:</para>
///
/// <list type="number">
///   <item><b>Roslyn</b> — <c>Microsoft.CodeAnalysis.CSharp</c> went to 5.9.0 while
///   <c>.Features</c> and <c>.Scripting</c> stayed at 5.6.0. Those two pin CSharp/Common
///   <c>(= 5.6.0)</c>, so restore failed <c>NU1107</c> across five test projects.</item>
///   <item><b>NuGet.*</b> — five of six went to 7.9.0 and <c>NuGet.Versioning</c> stayed at 7.6.0.
///   <c>NuGet.Packaging 7.9.0</c> requires <c>Versioning &gt;= 7.9.0</c>, so the pin was a
///   DOWNGRADE: <c>NU1605</c>.</item>
///   <item><b>Aspire</b> — <c>Hosting</c> and <c>Hosting.PostgreSQL</c> went to 13.5.3 while the
///   other twelve stayed at 13.4.6. That one restored cleanly and was WORSE for it: 13.5.3 pulls
///   <c>JsonPatch.Net → JsonPointer.Net → Json.More.Net</c>, the json-everything family that
///   #1231 deliberately removed, now published under <c>OSMF-maintenance-fee</c>. A licence
///   change arrived as a side effect of a routine version sweep.</item>
/// </list>
///
/// <para>🚨 A FOURTH was found on 2026-09-11 and had been live for far longer, because no prefix
/// could relate its two members: <c>System.Reactive</c> sat at <c>6.1.0</c> while
/// <c>Microsoft.Reactive.Testing</c> — the same product, same version line — sat at <c>7.0.0</c>.
/// The testing package depends on <c>System.Reactive &gt;= 7.0.0</c>, so the eight test projects
/// referencing it resolved Rx 7 while all 19 <c>src/</c> projects resolved Rx 6. Nothing failed:
/// the suite that gates the platform was simply exercising a different Rx MAJOR from the one the
/// portals shipped. That is the one skew a test suite must never have, and it is why
/// <see cref="NamedFamilies"/> exists alongside the prefix list.</para>
///
/// <para><b>Why a guard rather than care.</b> Two of the three were caught by a build, but only
/// after a push — and the third was caught by nothing except a licence gate that had to be read
/// carefully, because a split family can restore perfectly well and simply bring different
/// transitive dependencies with it. Nobody reviewing a 27-package dependabot diff is going to
/// notice that one member of a family did not move.</para>
///
/// <para>🚨 <b>Base version, not exact string.</b> <c>Aspire.Hosting.Kubernetes</c> ships
/// PREVIEW-ONLY — there is no non-preview <c>13.4.6</c> — so it legitimately reads
/// <c>13.4.6-preview.1.26319.6</c> while its siblings read <c>13.4.6</c>. Requiring identical
/// strings would fail on a correct pin and teach the next person to delete the guard. The
/// invariant is that the family agrees on the version BEFORE the pre-release suffix.</para>
/// </summary>
public class PackageFamiliesMoveTogetherGuard
{
    /// <summary>
    /// The families whose members pin one another, so a split is a defect rather than a choice.
    /// Keyed by the prefix that identifies membership.
    /// </summary>
    private static readonly (string Prefix, string Why)[] Families =
    [
        ("Microsoft.CodeAnalysis.",
            "Roslyn: .Features and .Scripting pin .CSharp and .Common EXACTLY — a split is NU1107"),
        ("NuGet.",
            "the NuGet client: Packaging/Protocol/Resolver require Versioning at their own version — a split is NU1605"),
        ("Aspire.",
            "Aspire: a split restores cleanly but changes the TRANSITIVE set — 13.5.3 reintroduces the "
            + "json-everything family (OSMF-maintenance-fee) that #1231 removed"),
    ];

    /// <summary>
    /// Families whose members share no common prefix, so the list above cannot express them.
    /// 🚨 Rx is the reason this second shape exists: <c>System.Reactive</c> and
    /// <c>Microsoft.Reactive.Testing</c> are one product with one version line and two vendors'
    /// worth of naming, and the prefix scanner is blind to that by construction.
    /// </summary>
    private static readonly (string Name, string[] Ids, string Why)[] NamedFamilies =
    [
        ("Rx.NET",
            ["System.Reactive", "Microsoft.Reactive.Testing"],
            "Rx.NET: Microsoft.Reactive.Testing depends on System.Reactive AT ITS OWN VERSION, and "
            + "with CentralPackageTransitivePinningEnabled unset the higher of the two wins — but "
            + "only in the projects that reference the testing package. A split therefore restores "
            + "cleanly and silently runs the TEST SUITE on a different Rx major from the one src/ "
            + "compiles and the portals ship"),
    ];

    private static string PropsPath() =>
        Path.Combine(FindRepoRoot(), "Directory.Packages.props");

    /// <summary>Every <c>PackageVersion</c> entry as (id, version).</summary>
    private static List<(string Id, string Version)> Pins() =>
        Regex.Matches(File.ReadAllText(PropsPath()),
                @"<PackageVersion\s+Include=""([^""]+)""\s+Version=""([^""]+)""")
            .Select(m => (m.Groups[1].Value, m.Groups[2].Value))
            .ToList();

    /// <summary>The version with any pre-release suffix removed — 13.4.6-preview.1 → 13.4.6.</summary>
    private static string BaseVersion(string v)
    {
        var cut = v.IndexOf('-');
        return cut < 0 ? v : v[..cut];
    }

    /// <summary>
    /// The ONE split-detection routine. Both family shapes call it and so does the negative
    /// control, so the control exercises the real code path rather than a copy of it — a guard
    /// tested through a replica of its own logic is not tested at all.
    /// Returns null when the family agrees, or the rendered offence when it is split.
    /// </summary>
    private static string? SplitOffence(
        string label, IReadOnlyList<(string Id, string Version)> members, string why)
    {
        if (members.Count < 2) return null;

        var byBase = members
            .GroupBy(p => BaseVersion(p.Version), StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ToList();

        if (byBase.Count == 1) return null;

        var detail = string.Join("\n      ", byBase.Select(g =>
            $"{g.Key}: {string.Join(", ", g.Select(p => p.Id))}"));
        return $"{label} is SPLIT across {byBase.Count} base versions — {why}\n      {detail}";
    }

    [Fact]
    public void EveryPinnedFamilyAgreesOnOneBaseVersion()
    {
        var pins = Pins();
        var offenders = new List<string>();

        foreach (var (prefix, why) in Families)
        {
            var members = pins.Where(p => p.Id.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            if (SplitOffence($"{prefix}*", members, why) is { } offence) offenders.Add(offence);
        }

        foreach (var (name, ids, why) in NamedFamilies)
        {
            var members = pins.Where(p => ids.Contains(p.Id, StringComparer.Ordinal)).ToList();
            if (SplitOffence(name, members, why) is { } offence) offenders.Add(offence);
        }

        Assert.True(offenders.Count == 0,
            "a package family must move together or not at all. A split either fails restore "
            + "(NU1107/NU1605) or — worse — restores cleanly while silently changing the transitive "
            + "set, which is how a LICENCE change arrives inside a routine version bump:\n\n  "
            + string.Join("\n\n  ", offenders));
    }

    /// <summary>
    /// 🚨 The scanner must find the families it claims to police. A prefix that matches nothing
    /// makes the test above pass vacuously — the same defect it exists to prevent.
    /// </summary>
    [Fact]
    public void TheScannerSeesEveryFamilyItPolices()
    {
        var pins = Pins();
        Assert.True(pins.Count > 100, $"expected the full CPM pin list; saw {pins.Count}");

        foreach (var (prefix, _) in Families)
        {
            var n = pins.Count(p => p.Id.StartsWith(prefix, StringComparison.Ordinal));
            Assert.True(n >= 2,
                $"family '{prefix}*' matched {n} pin(s) — a family the guard cannot see is a family "
                + "it is not policing. Remove the entry or fix the prefix.");
        }

        // A named family is spelled out id by id, so a RENAMED or DROPPED member makes it shrink
        // silently rather than fail. Every id must still be a pin.
        foreach (var (name, ids, _) in NamedFamilies)
        {
            var missing = ids.Where(id => !pins.Any(p => string.Equals(p.Id, id, StringComparison.Ordinal)))
                .ToList();
            Assert.True(missing.Count == 0,
                $"family '{name}' names {string.Join(", ", missing)}, which is not pinned in "
                + "Directory.Packages.props. A member the guard cannot see is a member it is not "
                + "policing — fix the id or drop it from the family.");
            Assert.True(ids.Length >= 2,
                $"family '{name}' lists {ids.Length} member(s) — a family of one polices nothing.");
        }
    }

    /// <summary>
    /// 🚨 The negative control. A guard that has never been seen to fail is not a guard, and this
    /// one's whole point is that it fires on a shape the PREFIX scanner is blind to — which is
    /// exactly how the Rx split survived: <c>System.Reactive</c> sat at 6.1.0 next to
    /// <c>Microsoft.Reactive.Testing</c> 7.0.0, no prefix related them, and both repos stayed green
    /// while the suite ran a different Rx major from the one that shipped.
    /// </summary>
    [Fact]
    public void ANamedFamilySplitIsDetected()
    {
        var (name, ids, why) = NamedFamilies.Single(f => f.Name == "Rx.NET");

        // The exact pairing that was live in this repository until 2026-09-11.
        var split = new List<(string Id, string Version)>
        {
            ("System.Reactive", "6.1.0"),
            ("Microsoft.Reactive.Testing", "7.0.0"),
        };
        var offence = SplitOffence(name, split, why);
        Assert.NotNull(offence);
        Assert.Contains("6.1.0", offence);
        Assert.Contains("7.0.0", offence);
        Assert.Contains("System.Reactive", offence);

        // The same routine must stay SILENT when the family agrees — otherwise it would "detect"
        // every family and the red above would mean nothing.
        Assert.Null(SplitOffence(name,
            [("System.Reactive", "7.0.0"), ("Microsoft.Reactive.Testing", "7.0.0")], why));

        // …and a one-member family polices nothing, so it must not be reported either.
        Assert.Null(SplitOffence(name, [("System.Reactive", "7.0.0")], why));

        // Deliberately NOT asserted here: that the LIVE pins agree. That is
        // EveryPinnedFamilyAgreesOnOneBaseVersion's job, and duplicating it would make one real
        // split report as two failures — noise that obscures which check actually found it.
        _ = ids;
    }

    /// <summary>The pre-release trim is the whole reason a legitimate preview-only member passes.</summary>
    [Fact]
    public void BaseVersionIgnoresThePreReleaseSuffix()
    {
        Assert.Equal("13.4.6", BaseVersion("13.4.6-preview.1.26319.6"));
        Assert.Equal("13.4.6", BaseVersion("13.4.6"));
        Assert.Equal("5.9.0", BaseVersion("5.9.0-beta.1"));
        // And it must NOT collapse genuinely different versions.
        Assert.NotEqual(BaseVersion("13.5.3"), BaseVersion("13.4.6"));
    }

    /// <summary>
    /// 🚨 Anchors on the SOLUTION file, not on Directory.Packages.props. There are two of the
    /// latter — the root one with ~150 pins, and test/Directory.Packages.props with 6 — and
    /// walking up from the test binary reaches the test one FIRST. The guard's first version did
    /// exactly that and policed six pins while believing it policed the tree; its own
    /// scanner-sees-what-it-claims test is what caught it ("expected the full CPM pin list; saw 6").
    /// </summary>
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx")))
            dir = dir.Parent;
        Assert.True(dir is not null, "could not locate the repository root (no MeshWeaver.slnx found)");
        return dir!.FullName;
    }
}
