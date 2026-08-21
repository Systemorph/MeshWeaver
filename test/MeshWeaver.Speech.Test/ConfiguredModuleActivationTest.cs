#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Speech.Test;

/// <summary>
/// The <c>Modules:Assemblies</c> baseline rule, pinned on the pure half so it needs no filesystem
/// and no mesh — which matters because the situation it protects is exactly the one that is hardest
/// to stand up: a host whose image no longer ships something its configuration still lists.
///
/// <para>Voice is the reason this exists here. <c>Memex.LocalMesh</c> stopped referencing
/// MeshWeaver.Speech and now installs it as a module, so "the module is not there" changed from a
/// compile error into a runtime condition — and the only acceptable behaviour is to start without
/// voice and say so. On 3.0.0-rc5 the other answer took every portal down before anything
/// served.</para>
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
}
