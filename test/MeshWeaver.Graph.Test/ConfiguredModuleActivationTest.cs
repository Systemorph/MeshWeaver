#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using MeshWeaver.Mesh;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The <c>Modules:Assemblies</c> baseline rule, pinned on the pure half so it needs no filesystem
/// and no mesh — which matters because the situation it protects is exactly the one that is hardest
/// to stand up: a host whose image no longer ships something its configuration still lists.
///
/// <para>Voice is the reason this exists. <c>Memex.LocalMesh</c> stopped referencing
/// MeshWeaver.Speech and installs it as a module, so "the module is not there" changed from a
/// compile error into a runtime condition — and the only acceptable behaviour is to start without
/// voice and say so. On 3.0.0-rc5 the other answer took every portal down before anything
/// served.</para>
///
/// <para>🚨 That silence is safe ONLY for a module nobody declared required. The loud half is
/// <see cref="MeshBuilderModuleActivation.MissingRequired"/>, pinned below: once modules began
/// leaving the image for the registry, "skip quietly" alone would let a rollout drop a feature
/// with nothing failing.</para>
/// </summary>
public class ConfiguredModuleActivationTest
{
    private static string Resolve(string entry) => $"/app/{entry}";

    [Fact]
    public void APresentModule_IsInstalled_AndNothingIsSkipped()
    {
        var skips = new List<string>();

        var resolved = MeshBuilderModuleActivation.ResolveInstallable(
            ["MeshWeaver.Speech.dll"], Resolve, _ => true, skips.Add);

        Assert.Equal(["/app/MeshWeaver.Speech.dll"], resolved);
        Assert.Empty(skips);
    }

    [Fact]
    public void AListedButAbsentModule_IsSkippedLoudly_NeverThrown()
    {
        var skips = new List<string>();

        // No throw is the assertion: InstallAssemblies would do Assembly.LoadFrom and take the host
        // down, so the entry must be dropped BEFORE it gets there.
        var resolved = MeshBuilderModuleActivation.ResolveInstallable(
            ["MeshWeaver.Speech.dll"], Resolve, _ => false, skips.Add);

        Assert.Empty(resolved);
        var skip = Assert.Single(skips);
        // The line has to name the entry AND where it looked — a skip nobody can act on is noise.
        Assert.Contains("MeshWeaver.Speech.dll", skip);
        Assert.Contains("/app/MeshWeaver.Speech.dll", skip);
    }

    [Fact]
    public void OneAbsentEntry_DoesNotCostTheOthers()
    {
        var skips = new List<string>();

        var resolved = MeshBuilderModuleActivation.ResolveInstallable(
            ["Gone.dll", "MeshWeaver.Speech.dll"],
            Resolve,
            path => path.EndsWith("MeshWeaver.Speech.dll", StringComparison.Ordinal),
            skips.Add);

        Assert.Equal(["/app/MeshWeaver.Speech.dll"], resolved);
        Assert.Contains("Gone.dll", Assert.Single(skips));
    }

    [Fact]
    public void NoConfiguredModules_IsAQuietNoOp()
    {
        var skips = new List<string>();

        // Null (no Modules section at all) and blank entries are ordinary, not faults: a host that
        // installs nothing must not emit a diagnostic implying something went wrong.
        Assert.Empty(MeshBuilderModuleActivation.ResolveInstallable(null, Resolve, _ => true, skips.Add));
        Assert.Empty(MeshBuilderModuleActivation.ResolveInstallable(
            ["", "   ", null], Resolve, _ => true, skips.Add));
        Assert.Empty(skips);
    }

    // ───────── the LOUD half: Modules:Required ─────────

    private static IConfiguration Config(params string[] required)
        => new ConfigurationBuilder().AddInMemoryCollection(
            required.Select((entry, i) =>
                new KeyValuePair<string, string?>($"Modules:Required:{i}", entry))).Build();

    [Fact]
    public void ADeclaredRequiredModuleThatIsAbsent_IsReported()
    {
        var missing = MeshBuilderModuleActivation.MissingRequired(
            Config("MeshWeaver.Blazor.Radzen.dll"), Resolve, _ => false);

        // The whole point: this is the case that used to be a stderr line and a green rollout.
        Assert.Equal(["MeshWeaver.Blazor.Radzen.dll"], missing);
    }

    [Fact]
    public void ARequiredModuleThatIsPresent_IsNotReported()
    {
        Assert.Empty(MeshBuilderModuleActivation.MissingRequired(
            Config("MeshWeaver.Blazor.Radzen.dll"), Resolve, _ => true));
    }

    [Fact]
    public void DeclaringNothingRequired_IsInert()
    {
        // Today's deployments declare none, and they must behave exactly as they do now — a gate
        // that fires by default would fail every portal on the first boot after it ships.
        Assert.Empty(MeshBuilderModuleActivation.MissingRequired(Config(), Resolve, _ => false));
    }

    [Fact]
    public void RequiredIsINDEPENDENT_OfWhatIsListedUnderAssemblies()
    {
        // A module can be required without being in the baseline list (the store installed it) and
        // listed without being required (an optional pack). Neither key implies the other, so the
        // rule reads Modules:Required and nothing else.
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Modules:Assemblies:0"] = "SomethingElse.dll",
            ["Modules:Required:0"] = "MeshWeaver.Speech.dll",
        }).Build();

        Assert.Equal(["MeshWeaver.Speech.dll"],
            MeshBuilderModuleActivation.MissingRequired(config, Resolve, _ => false));
    }

    // ───────── the SILENT half: an override that REPLACES a requirement ─────────

    /// <summary>
    /// The layering a deployment actually has: the image's own appsettings baseline first, then the
    /// container environment the ConfigMap supplies. Two providers, in that order — which is the
    /// whole mechanism, since the later one wins per KEY and a key here is an array INDEX.
    /// </summary>
    private static IConfiguration ImagePlusOverlay(
        IEnumerable<string> baseline, IDictionary<string, string?> overlay)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(baseline.Select((entry, i) =>
                new KeyValuePair<string, string?>($"Modules:Required:{i}", entry)))
            .AddInMemoryCollection(overlay)
            .Build();

    /// <summary>The image's list as it stands: seven entries, Social at index 5.</summary>
    private static readonly string[] ImageBaseline =
    [
        "MeshWeaver.Blazor.Radzen.dll",
        "MeshWeaver.Blazor.Analysis.dll",
        "MeshWeaver.Blazor.EntityViews.dll",
        "MeshWeaver.Blazor.GoogleMaps.dll",
        "MeshWeaver.Speech.dll",
        "MeshWeaver.Social.dll",
        "MeshWeaver.AI.dll",
    ];

    [Fact]
    public void AnOverlayEntryThatOverwritesABaselineRequirement_IsReported()
    {
        // The literal Memex#131 shape: the overlay restates 0..4 and then adds MCP "at the end" —
        // except index 5 is not the end, it is MeshWeaver.Social.dll. Nothing else in the system
        // can see this: Social is not MISSING, it is no longer REQUIRED.
        var config = ImagePlusOverlay(ImageBaseline, new Dictionary<string, string?>
        {
            ["Modules:Required:0"] = "MeshWeaver.Blazor.Radzen.dll",
            ["Modules:Required:1"] = "MeshWeaver.Blazor.Analysis.dll",
            ["Modules:Required:2"] = "MeshWeaver.Blazor.EntityViews.dll",
            ["Modules:Required:3"] = "MeshWeaver.Blazor.GoogleMaps.dll",
            ["Modules:Required:4"] = "MeshWeaver.Speech.dll",
            ["Modules:Required:5"] = "MeshWeaver.Mcp.dll",
        });

        Assert.Equal(["MeshWeaver.Social.dll"], MeshBuilderModuleActivation.ShadowedRequired(config));

        // And the guard it is NOT: every entry still resolves, so the loud half says nothing.
        Assert.Empty(MeshBuilderModuleActivation.MissingRequired(config, Resolve, _ => true));
    }

    [Fact]
    public void AnOverlayEntryPastTheBaseline_AddsWithoutReplacing()
    {
        // The fix, pinned: index 7 is the first free slot, so MCP is required IN ADDITION.
        var config = ImagePlusOverlay(ImageBaseline, new Dictionary<string, string?>
        {
            ["Modules:Required:7"] = "MeshWeaver.Mcp.dll",
        });

        Assert.Empty(MeshBuilderModuleActivation.ShadowedRequired(config));
    }

    [Fact]
    public void BlankingAnEntry_IsNotAShadow()
    {
        // Blanking is the SANCTIONED way to drop a requirement a deployment cannot satisfy — it was
        // the only remedy the 2026-08-23 rollouts had. Reporting it would make the check noise on
        // every install that legitimately opts out.
        var config = ImagePlusOverlay(ImageBaseline, new Dictionary<string, string?>
        {
            ["Modules:Required:0"] = "",
            ["Modules:Required:5"] = "",
        });

        Assert.Empty(MeshBuilderModuleActivation.ShadowedRequired(config));
    }

    [Fact]
    public void ReorderingTheList_IsNotAShadow()
    {
        // Every baseline module is still required, at a different index. Nothing was lost, so
        // nothing is reported — the check is about the SET, not the positions.
        var config = ImagePlusOverlay(ImageBaseline, new Dictionary<string, string?>
        {
            ["Modules:Required:0"] = "MeshWeaver.Social.dll",
            ["Modules:Required:5"] = "MeshWeaver.Blazor.Radzen.dll",
        });

        Assert.Empty(MeshBuilderModuleActivation.ShadowedRequired(config));
    }

    [Fact]
    public void AnUnlayeredConfiguration_HasNothingToShadow()
    {
        // One source, no overrides — today's default, and it must stay silent.
        Assert.Empty(MeshBuilderModuleActivation.ShadowedRequired(Config(ImageBaseline)));
    }
}
