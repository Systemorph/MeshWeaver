using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MeshWeaver.Compiler;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 <b>No MSBuild property CD's two image legs pass may move a COMPILED version attribute</b>
/// (#3022, second pass) — the general form of the invariant
/// <see cref="CompiledVersionAttributesIgnoreVersionOverrideGuard"/> guards for one property.
///
/// <para><b>Why the narrow guard is not enough.</b> That one asserts a single remembered switch,
/// <c>-p:Version=</c>, because that is the switch that broke. But the defect class is wider and
/// has nothing to do with the NAME <c>Version</c>: <c>main-cd.yml</c> publishes
/// <c>memex-portal-ai</c> and <c>mw-plugin-test</c> from the SAME commit in two different jobs,
/// with two different sets of global <c>-p:</c> properties. A global property reaches every
/// project in the graph, core's included. So ANY property one leg passes and the other does not —
/// or passes with a different value — that reaches <c>InformationalVersion</c>,
/// <c>AssemblyVersion</c> or <c>FileVersion</c> forks the two images' reference assemblies, hence
/// all 25 shared <c>meshweaver-surface.manifest</c> hashes, hence the framework build identity the
/// bake is published under. The bundles then sit at an address no portal resolves: INERT, #1814's
/// shape, every pod recompiling the mesh at boot with the whole pipeline green.</para>
///
/// <para><b>Why it reads the workflow instead of a hard-coded list.</b> <c>-p:Version=</c> was
/// added to the portal leg on 2026-09-01 for #2555 (so the image reports its own build) and
/// nothing existed that could notice. A list written here would go stale the same way — the next
/// property added to one leg would be outside it, and the guard would pass having checked the
/// wrong set. Reading the two publish commands out of <c>main-cd.yml</c> makes the guard FOLLOW
/// CD: a new <c>-p:</c> on either leg is covered the moment it lands, with no second place to
/// remember.</para>
///
/// <para>See <c>Doc/Architecture/BakeIdentityMismatch</c>.</para>
/// </summary>
public class CdImageLegPropertiesDoNotForkTheIdentityGuard
{
    private const string Workflow = ".github/workflows/main-cd.yml";

    /// <summary>The portal image leg: the deployment image, and the one that passes <c>-p:Version=</c>.</summary>
    private const string PortalPublishCommand =
        "dotnet publish plugins-repo/src/Memex.Portal.Distributed/Memex.Portal.Distributed.csproj";

    /// <summary>The tester/bake image leg: the host the bake runs in, and the one that must resolve the SAME identity.</summary>
    private const string TesterPublishCommand =
        "dotnet publish tools/MeshWeaver.PluginTester/MeshWeaver.PluginTester.csproj";

    /// <summary>
    /// <c>CIRun</c> is excluded, and only <c>CIRun</c>. BOTH legs pass <c>-p:CIRun=true</c>, so it
    /// is symmetric by construction and cannot fork them; and it is the one property that is
    /// SUPPOSED to change <c>InformationalVersion</c> — it selects the commit-deterministic CI
    /// branch over the local-dev one (see the note in Directory.Build.props). Probing it would red
    /// this guard for the behaviour the design requires. Every other property is fair game.
    /// </summary>
    private static readonly string[] NotProbed = ["CIRun"];

    private const string ProbeProject = "src/MeshWeaver.ShortGuid/MeshWeaver.ShortGuid.csproj";

    private const string ProbeAssembly = "MeshWeaver.ShortGuid";

    /// <summary>
    /// The value every probed property is set to. A version-shaped string that
    /// <c>Directory.Build.props</c> could never produce itself, so its appearance in a compiled
    /// attribute is unambiguous — and one no path-valued property will make MSBuild choke on,
    /// because evaluation resolves no imports from these.
    /// </summary>
    private const string ProbeValue = "9.9.9-guard.ci.424242";

    /// <summary>The three properties that become attributes inside the emitted (and reference) assembly.</summary>
    private static readonly string[] CompiledAttributes =
        ["InformationalVersion", "AssemblyVersion", "FileVersion"];

    /// <summary>
    /// 🚨 The control arm, and it runs FIRST. Everything below rests on having actually found the
    /// two publish commands and read their switches; a parser that silently matched nothing would
    /// probe the empty set and pass having measured NOTHING — the shape AGENTS.md calls a gate
    /// that tests its own inputs. So: both commands must be found, each must carry a plausible
    /// number of properties, both must carry the one property they are known to share
    /// (<c>CIRun</c> — that is what proves these are the platform's own publish commands and not
    /// two arbitrary lines), and the union must contain <c>Version</c>, the property whose
    /// asymmetry caused #3022.
    /// </summary>
    [Fact]
    public void TheTwoImageLegsPublishCommandsAreFoundAndCarryProperties()
    {
        var portal = PropertiesOf(PortalPublishCommand);
        var tester = PropertiesOf(TesterPublishCommand);

        Assert.True(portal.Count >= 4,
            $"only {portal.Count} `-p:` propert(ies) parsed off the portal publish command in {Workflow}. "
            + "The guard below would then probe almost nothing and pass vacuously.");
        Assert.True(tester.Count >= 4,
            $"only {tester.Count} `-p:` propert(ies) parsed off the tester publish command in {Workflow}. "
            + "The guard below would then probe almost nothing and pass vacuously.");

        Assert.Contains("CIRun", portal);
        Assert.Contains("CIRun", tester);

        Assert.True(portal.Contains("Version") || tester.Contains("Version"),
            "neither image leg passes -p:Version= any more. If that switch was deliberately removed "
            + "the #3022 exposure is gone and this assertion should go with it — but silently losing "
            + "it from the parse is how a guard stops covering its subject, so it fails here first.");
    }

    /// <summary>
    /// The measurement. Evaluate the probe project bare, then again with EVERY property either
    /// image leg passes (bar <see cref="NotProbed"/>) set at once, and require the three compiled
    /// attributes to be byte-identical. Applying them together keeps the green path to two
    /// evaluations; on a failure the properties are re-probed one at a time so the message names
    /// the culprit rather than the set.
    /// </summary>
    [Fact]
    public void NoPropertyEitherImageLegPassesMovesACompiledVersionAttribute()
    {
        var probed = ProbedProperties();
        Assert.NotEmpty(probed);

        var bare = Evaluate();
        var all = Evaluate([.. probed.Select(name => $"-p:{name}={ProbeValue}")]);

        // 🚨 Non-empty in BOTH, asserted before equality. The #3022 regression evaluated the three
        // properties to the EMPTY STRING (the SDK fills them in a later target, from $(Version)),
        // so a bare equality check would have compared "" with "" and passed while shipping the
        // defect.
        foreach (var name in CompiledAttributes)
        {
            Assert.False(string.IsNullOrWhiteSpace(bare[name]),
                $"{name} evaluated empty for {ProbeProject} with no CD properties applied — the root "
                + "Directory.Build.props no longer sets it, so the SDK derives it from $(Version), "
                + "the run-numbered string (#3022).");
            Assert.False(string.IsNullOrWhiteSpace(all[name]),
                $"{name} evaluated empty for {ProbeProject} once CD's image-leg properties were "
                + "applied. That is the #3022 regression: the PropertyGroup deriving the compiled "
                + "version attributes is conditional on one of them again.");
        }

        var drifted = CompiledAttributes
            .Where(name => !string.Equals(bare[name], all[name], StringComparison.Ordinal))
            .ToList();
        if (drifted.Count == 0)
            return;

        // Name the property, not just the set: "one of ten switches did it" is a starting point,
        // and the point of a guard is to hand over the finish line.
        var culprits = new List<string>();
        foreach (var name in probed)
        {
            var one = Evaluate($"-p:{name}={ProbeValue}");
            culprits.AddRange(CompiledAttributes
                .Where(attribute => !string.Equals(bare[attribute], one[attribute], StringComparison.Ordinal))
                .Select(attribute => $"-p:{name}= moves {attribute}: '{bare[attribute]}' -> '{one[attribute]}'"));
        }

        Assert.Fail(
            "A property `main-cd.yml` passes to an image publish reaches a COMPILED version "
            + $"attribute of {ProbeProject}:\n  "
            + string.Join("\n  ", culprits.Count > 0 ? culprits : drifted)
            + "\n\nThese attributes are emitted into the assembly and therefore into its REFERENCE "
            + "assembly, which meshweaver-surface.manifest hashes into the framework build identity. "
            + "CD publishes memex-portal-ai and mw-plugin-test from ONE commit with DIFFERENT "
            + "property sets, so any dependence here makes the two images resolve different "
            + "identities and every published bake INERT (#3022, #1814). The compiled attributes "
            + "must be a function of $(PlatformVersion) and the commit, and of nothing else; a "
            + "caller who genuinely needs a different assembly version asks for it by name "
            + "(-p:AssemblyVersion= / -p:FileVersion= / -p:InformationalVersion=).");
    }

    /// <summary>
    /// The probed project's assembly must be part of the canonical content surface — the set whose
    /// per-assembly surface hashes ARE the framework build identity. If it ever leaves that set the
    /// guard would still measure a real MSBuild evaluation, but no longer one that can fork a bake
    /// address, so it must be re-pointed rather than quietly kept.
    /// </summary>
    [Fact]
    public void ProbedAssemblyIsPartOfTheContentSurface()
        => Assert.Contains(ProbeAssembly, FrameworkBuildIdentity.ContentSurfaceAssemblies);

    /// <summary>The union of both legs' properties, minus <see cref="NotProbed"/>.</summary>
    private static IReadOnlyList<string> ProbedProperties()
        => PropertiesOf(PortalPublishCommand)
            .Concat(PropertiesOf(TesterPublishCommand))
            .Where(name => !NotProbed.Contains(name, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static IReadOnlyDictionary<string, string> Evaluate(params string[] extraArguments)
        => MsBuildPropertyProbe.Evaluate(ProbeProject, CompiledAttributes, extraArguments);

    private static readonly Regex PropertySwitch = new(@"-p:([A-Za-z_][A-Za-z0-9_]*)=", RegexOptions.Compiled);

    /// <summary>
    /// Reads the <c>-p:</c> property NAMES off one publish command in <see cref="Workflow"/>.
    ///
    /// <para>Deliberately a text scan and not a YAML parse: the two commands are written in two
    /// different YAML scalar styles (the portal leg is a backslash-continued shell <c>if</c> inside
    /// a literal block, the tester leg a folded scalar), and what matters is the switches the shell
    /// finally sees, which is the text either way. The continuation rule is exact rather than
    /// heuristic — a trimmed line whose first character is <c>-</c> and whose second is NOT a space
    /// is a command-line option; <c>- name:</c> and <c>- uses:</c> (YAML sequence entries) always
    /// have the space, and the portal leg's terminating <c>2&gt;&amp;1 | tee …</c> starts with
    /// neither. A miscount cannot pass silently: the control-arm test above asserts what this must
    /// find.</para>
    /// </summary>
    private static IReadOnlyList<string> PropertiesOf(string publishCommand)
    {
        var path = Path.Combine(MsBuildPropertyProbe.FindRepoRoot(),
            Workflow.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"{Workflow} does not exist — this guard reads CD's publish commands out of it.");

        var lines = File.ReadAllLines(path);
        var start = Array.FindIndex(lines, line => line.Contains(publishCommand, StringComparison.Ordinal));
        Assert.True(start >= 0,
            $"could not find `{publishCommand}` in {Workflow}. Either the project moved or the leg was "
            + "renamed; re-point this guard at the command that publishes that image. It must not be "
            + "left matching nothing — a guard whose subject moved and whose anchor did not passes "
            + "having checked nothing.");

        var names = new List<string>();
        for (var i = start; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (i > start && !(trimmed.Length >= 2 && trimmed[0] == '-' && trimmed[1] != ' '))
                break;
            foreach (Match match in PropertySwitch.Matches(trimmed))
            {
                var name = match.Groups[1].Value;
                if (!names.Contains(name, StringComparer.Ordinal))
                    names.Add(name);
            }
        }

        return names;
    }
}
