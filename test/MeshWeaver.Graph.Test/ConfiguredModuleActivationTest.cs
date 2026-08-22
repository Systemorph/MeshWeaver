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
}
