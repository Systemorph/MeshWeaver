#pragma warning disable CS1591

using System;
using System.IO;
using System.Linq;
using MeshWeaver.Mesh;
using Microsoft.Extensions.Configuration;
using MeshWeaver.PluginTester;
using Xunit;

namespace MeshWeaver.PluginTester.Test;

/// <summary>
/// The external-module seam (<c>--module</c>): the gate must be able to activate a module built
/// OUTSIDE its own image.
///
/// <para>🚨 Why this exists. The gate judges a node repo's content, and that content may declare
/// node types a MODULE provides. While a module's source lives in the platform repo the tester
/// lands it from its own closure lane; once the source moves to a node repo, the platform image
/// cannot build it and the ONLY build of those bytes is the one the node repo's CI just produced.
/// Without this seam, no module's source can ever leave the platform repo.</para>
///
/// <para>The two properties that make it trustworthy, both asserted here: an absolute path is used
/// EXACTLY as given (no probing, so an image copy can never silently substitute), and a path that
/// does not exist is REFUSED rather than skipped — a gate that quietly ran without a module it was
/// told to load would refuse every install needing that module's types and blame the content.</para>
/// </summary>
public class ExternalModuleGateTest
{
    [Fact]
    public void AnAbsoluteModulePath_ResolvesExactlyAsGiven_NeverProbed()
    {
        // The resolver is what makes a mounted module usable: absolute paths pass through
        // untouched. If this ever starts probing, a mounted module could be silently replaced by a
        // same-named copy in the image — the #2223 shadowing class, one level down.
        var mounted = Path.Combine(Path.GetTempPath(), "mw-ext-" + Guid.NewGuid().ToString("N"),
            "MeshWeaver.Widget", "MeshWeaver.Widget.dll");

        var resolved = MeshBuilder.ResolveModulePath(mounted);

        Assert.Equal(mounted, resolved);
    }

    [Fact]
    public void ExternalModules_JoinBothActivationLists_SoAnAbsentOneIsLoudNotSkipped()
    {
        // Every external module is listed under Modules:Assemblies (activation) AND
        // Modules:Required (the loud half). The pairing is the point: a listed-but-absent module is
        // SKIPPED by design — right for a feature a deployment can live without, wrong for a module
        // the gate was explicitly told to load, which is why the gate also requires it.
        var options = new GateOptions
        {
            RepoRoot = ".",
            ExternalModules = ["/mnt/build/MeshWeaver.Widget/MeshWeaver.Widget.dll"],
        };

        Assert.Single(options.ExternalModules);
        Assert.Equal("/mnt/build/MeshWeaver.Widget/MeshWeaver.Widget.dll", options.ExternalModules[0]);
    }

    [Fact]
    public void MissingRequired_NamesAnAbsentExternalModule()
    {
        // The gate's own input check (MeshBuilderModuleActivation.MissingRequired) is what turns an
        // absent module into a RED run before the mesh boots. A gate never tests its own inputs.
        var absent = Path.Combine(Path.GetTempPath(), "mw-absent-" + Guid.NewGuid().ToString("N"),
            "MeshWeaver.Widget.dll");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new System.Collections.Generic.Dictionary<string, string?>
            {
                ["Modules:Required:0"] = absent,
            })
            .Build();

        var missing = MeshBuilderModuleActivation.MissingRequired(
            configuration, MeshBuilder.ResolveModulePath, File.Exists);

        Assert.Equal(absent, Assert.Single(missing));
    }
}
