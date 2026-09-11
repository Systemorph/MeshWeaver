#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 <b>This repository pins a package it does not build with, and the pin is a security decision.</b>
/// <c>BlazorMonaco</c> ships the code editor's Blazor wrapper AND, inside its static web assets, a
/// whole prebuilt Monaco with a DOMPurify inlined into it. That inlined copy is what OWASP ZAP's
/// retire.js rule 10003 flagged as MeshWeaver#3378 — and neither Dependabot nor <c>dotnet build</c>
/// can see it, because it is a JavaScript library inside a NuGet package's assets rather than a
/// declared dependency.
///
/// <para><b>Why the guard lives HERE, in a repository with no editor in it.</b> Three facts compose
/// into a blind spot, and each one is individually reasonable:</para>
///
/// <list type="number">
///   <item>The pin lives in this repository's <c>Directory.Packages.props</c>.</item>
///   <item><b>No project in this repository references it</b> — the Blazor surface moved to
///   MeshWeaver.Plugins. So a bump here changes nothing this repository compiles, and core CI is
///   green by construction whatever the new version does.</item>
///   <item>MeshWeaver.Plugins has <b>no <c>Directory.Packages.props</c> of its own</b>. Its
///   <c>src/MeshWeaver.Blazor/MeshWeaver.Blazor.csproj</c> carries a VERSIONLESS
///   <c>&lt;PackageReference Include="BlazorMonaco" /&gt;</c>, so the version it restores is whatever
///   this file says.</item>
/// </list>
///
/// <para>Put together: <b>editing one line here changes what a different repository's editor is
/// built from, with nothing in this repository able to notice.</b> The satellite's own
/// <c>MonacoBundleGuard</c> does check the properties below — but it runs against the core commit
/// the satellite PINS (<c>MW_PLATFORM_REF</c>), so it reacts at the next pin move rather than at the
/// bump, which is the documented cross-repo lag (AGENTS.md: "the pin bump IS its integration test").
/// The existing <c>Satellite package pins (removal declared)</c> gate covers a pin being REMOVED;
/// a pin being BUMPED is uncovered, and this is that cover.</para>
///
/// <para><b>What the audited version was measured to contain</b> — against the live registry on
/// 2026-09-11, not read off a changelog: the NuGet flat-container index for <c>blazormonaco</c>
/// answered HTTP 200 with 3.5.0 as the newest release (no 3.6 exists), and
/// <c>blazormonaco.3.5.0.nupkg</c> (HTTP 200, 4,514,906 bytes) carries
/// <c>staticwebassets/lib/monaco-editor/min/vs/loader.js</c> declaring Monaco
/// <b>0.42.0-dev-20230906</b> and <c>editor.api-CalNCsUg.js</c> banner-stamped
/// <c>/*! @license DOMPurify 3.2.7</c>. Both are far below the scanner's floors. <b>So a bump is
/// not the fix and never was</b>, which is precisely why the portal builds its own bundle instead.</para>
///
/// <para>See <c>Doc/Architecture/SecurityScanning</c> for the finding, the remedy and the
/// end-to-end verification; <c>tools/monaco-editor/README.md</c> in MeshWeaver.Plugins for the build.</para>
/// </summary>
public class BlazorMonacoPinGuard
{
    /// <summary>
    /// The version whose bundled assets were audited for MeshWeaver#3378. Moving this is a
    /// deliberate act with a checklist, not a routine dependency bump — see
    /// <see cref="ThePinIsTheAuditedVersion"/> for what to re-verify.
    /// </summary>
    private const string AuditedVersion = "3.5.0";

    private const string PackageId = "BlazorMonaco";

    /// <summary>The directories that hold this repository's projects — never the whole tree, which
    /// contains sibling git worktrees under <c>.claude/</c>.</summary>
    private static readonly string[] ProjectRoots = ["src", "test", "tools", "samples", "memex"];

    private static string PropsPath() =>
        Path.Combine(FindRepoRoot(), "Directory.Packages.props");

    /// <summary>Every <c>PackageVersion</c> entry as (id, version).</summary>
    private static List<(string Id, string Version)> Pins() =>
        Regex.Matches(File.ReadAllText(PropsPath()),
                @"<PackageVersion\s+Include=""([^""]+)""\s+Version=""([^""]+)""")
            .Select(m => (m.Groups[1].Value, m.Groups[2].Value))
            .ToList();

    private static List<string> ProjectFiles() =>
        ProjectRoots
            .Select(r => Path.Combine(FindRepoRoot(), r))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.csproj", SearchOption.AllDirectories))
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();

    /// <summary>
    /// 🚨 The tripwire. It fires on ANY change to the pin, because there is no version of this
    /// package whose bundled Monaco can be trusted without being looked at, and nothing else in
    /// this repository will look.
    /// </summary>
    [Fact]
    public void ThePinIsTheAuditedVersion()
    {
        var pinned = Pins().Where(p => p.Id == PackageId).Select(p => p.Version).ToList();

        Assert.True(pinned.Count == 1,
            $"expected exactly one {PackageId} pin in Directory.Packages.props; saw {pinned.Count}");

        Assert.True(pinned[0] == AuditedVersion,
            $"""
             {PackageId} is pinned at {pinned[0]}, audited at {AuditedVersion} (MeshWeaver#3378).

             This is not a routine bump. No project in THIS repository references {PackageId}, so
             this repository's build and CI cannot tell you anything about the new version — but
             MeshWeaver.Plugins consumes this pin VERSIONLESS (src/MeshWeaver.Blazor/*.csproj), so
             the portal's editor is now built from it.

             Before moving this line, check the new package's static web assets and record what you
             found on Doc/Architecture/SecurityScanning:

               1. Does it still inline a DOMPurify below the retire.js floor (3.4.13)?
                  unzip -p <pkg>.nupkg 'staticwebassets/lib/monaco-editor/min/vs/editor.api-*.js' \
                    | grep -o '@license DOMPurify [0-9.]*'
                  If yes, the portal must go on building its own bundle (tools/monaco-editor in
                  MeshWeaver.Plugins) — the package bump is NOT a remedy for #3378.

               2. Does its jsInterop.js still only DECLARE the AMD path and never resolve it?
                  MeshWeaver#3617 stopped PUBLISHING lib/monaco-editor/** on exactly that premise.
                  A version that starts loading from that path 404s every editor at first use with
                  a green build. MonacoBundleGuard (MeshWeaver.Plugins) asserts this — make its
                  run part of the bump, do not wait for the next MW_PLATFORM_REF move.

               3. Does BlazorMonaco still drive the editor through window.monaco only? That is what
                  lets our own bundle satisfy it.

             Then update AuditedVersion here in the same change.
             """);
    }

    /// <summary>
    /// 🚨 THE PREMISE OF THIS GUARD'S EXISTENCE. The pin is invisible to this repository's build
    /// only for as long as nothing here references the package. If a project ever does, the compiler
    /// and CI start covering some of what the message above asks a human to check by hand — and the
    /// guard's reasoning, which says they cannot, becomes wrong in a way that reads as reassuring.
    /// So the premise is asserted rather than remembered.
    /// </summary>
    [Fact]
    public void NoProjectInThisRepositoryReferencesTheEditorPackage()
    {
        var referencing = ProjectFiles()
            .Where(p => Regex.IsMatch(File.ReadAllText(p),
                $@"<PackageReference\s+[^>]*Include\s*=\s*""{PackageId}"""))
            .Select(p => Path.GetRelativePath(FindRepoRoot(), p))
            .ToList();

        Assert.True(referencing.Count == 0,
            $"""
             {string.Join(", ", referencing)} now reference(s) {PackageId}.

             This guard's failure message tells the next person that this repository's build cannot
             see a {PackageId} bump. That is true only while this list is empty. Re-read
             ThePinIsTheAuditedVersion and Doc/Architecture/SecurityScanning and say what the build
             now covers — and note that a project here referencing the package will also PUBLISH its
             static web assets, which is the stale-published-asset problem MeshWeaver#3617 closed on
             the portal side.
             """);
    }

    /// <summary>
    /// 🚨 A guard that cannot find its subject must fail, not pass. The pin regex is attribute-order
    /// sensitive and the project scan depends on a directory layout; either going quiet would make
    /// both facts above vacuous — the same shape of defect they exist to prevent.
    /// </summary>
    [Fact]
    public void TheGuardSeesTheThingsItPolices()
    {
        var pins = Pins();
        Assert.True(pins.Count > 100, $"expected the full CPM pin list; saw {pins.Count}");
        Assert.Contains(pins, p => p.Id == PackageId);

        var projects = ProjectFiles();
        Assert.True(projects.Count > 30,
            $"expected this repository's projects; saw {projects.Count} .csproj files. A scan that "
            + "finds (almost) nothing would report 'nothing references BlazorMonaco' having looked "
            + "nowhere.");
        // And the scan must be able to SEE a PackageReference at all — otherwise "no matches" is
        // indistinguishable from a broken matcher.
        Assert.Contains(projects, p => Regex.IsMatch(File.ReadAllText(p), @"<PackageReference\s"));
    }

    /// <summary>
    /// 🚨 Anchors on the SOLUTION file, not on Directory.Packages.props. There are two of the
    /// latter — the root one, and test/Directory.Packages.props with 6 pins — and walking up from
    /// the test binary reaches the test one FIRST.
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
