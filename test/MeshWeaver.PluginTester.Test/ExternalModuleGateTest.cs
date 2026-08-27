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
    public void ExternalModules_AreCarriedOnTheGateOptions_VerbatimAndInOrder()
    {
        // Named for what it actually asserts (Copilot review): the OPTION carries the paths. That
        // they then join BOTH activation lists is covered where the behaviour lives — the
        // Modules:Required half by MissingRequired_NamesAnAbsentExternalModule below, and the
        // end-to-end fold by the gate suites that boot a mesh with the engine module activated.
        var options = new GateOptions
        {
            RepoRoot = ".",
            ExternalModules =
            [
                "/mnt/build/MeshWeaver.Widget/MeshWeaver.Widget.dll",
                "/mnt/build/MeshWeaver.Gadget/MeshWeaver.Gadget.dll",
            ],
        };

        // Verbatim: a path the caller mounted must reach the loader unchanged — rewriting one is
        // how a mounted module gets silently replaced by a same-named copy in the image.
        Assert.Equal(
            [
                "/mnt/build/MeshWeaver.Widget/MeshWeaver.Widget.dll",
                "/mnt/build/MeshWeaver.Gadget/MeshWeaver.Gadget.dll",
            ],
            options.ExternalModules);
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
