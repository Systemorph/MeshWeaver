using System;
using System.IO;
using System.Text;
using MeshWeaver.PluginTester;
using Xunit;

namespace MeshWeaver.PluginTester.Test;

/// <summary>
/// 🚨 <b>The bake compiles against the same modules a portal does.</b> A module is published into
/// <c>modules/&lt;name&gt;/</c> and is therefore, by construction, NOT in
/// <c>TRUSTED_PLATFORM_ASSEMBLIES</c> — so it reaches a compile only by being composed in
/// (<c>CompileReferences.ComposeWithModules</c>). The portal does that from its installed-module
/// set; until this test the bake used the bare TPA baseline and could not resolve a single module
/// type.
///
/// <para><b>How that stayed invisible, and why it needed a test rather than care.</b> The two lanes
/// of this same tool disagreed silently. The GATE stands up a mesh, so it composed modules for
/// free and its <c>compile-check</c> went GREEN; only <c>publish-bake</c> went red — on the same
/// content, the same image and the same commit. A reader sees a green compile gate beside a red
/// bake and concludes the BAKE is broken, not that a reference is missing. Nothing in the failure
/// names a module: it arrives as <c>CS0246: The type or namespace name 'AiSettings' could not be
/// found</c>, which reads as a content defect.</para>
///
/// <para><b>What it cost, measured.</b> When the AI engine left the content surface to become a
/// module (#2276), five <c>Store/*</c> NodeTypes stopped resolving <c>AiSettings</c> in the bake
/// and nowhere else. No bundle was sealed for the new framework identity; every install then
/// CORRECTLY declined to self-update onto an image whose content had no bake, and the fleet sat
/// three published images behind with every component reporting success (#2563). The self-updater
/// was working perfectly — it was refusing an image the bake had never covered.</para>
///
/// <para><b>The control is the point.</b> The same tree is baked twice: once with the module set
/// composed, once with none. If the second did NOT fail, this test would be asserting nothing —
/// it would pass just as happily against the broken behaviour it exists to pin.</para>
/// </summary>
public class BakeCompilesAgainstModulesTest(ITestOutputHelper output)
{
    private const string IndexJson =
        """{"$type":"MeshNode","id":"ModuleBound","namespace":"","path":"ModuleBound","mainNode":"ModuleBound","name":"Module Bound","nodeType":"Space","state":"Active","content":{"$type":"PluginManifest","description":"Content that binds a module type."}}""";

    private const string TypeJson =
        """{"$type":"MeshNode","id":"Bound","namespace":"ModuleBound","path":"ModuleBound/Bound","mainNode":"ModuleBound/Bound","name":"Bound","nodeType":"NodeType","state":"Active","content":{"$type":"NodeTypeDefinition","description":"Binds a type that lives in a MODULE, not on the content surface.","configuration":"config => config.WithContentType<Bound>().AddDefaultLayoutAreas()","includeGlobalTypes":true}}""";

    /// <summary>
    /// Source that binds <c>MeshWeaver.AI.AiSettings</c> — a type in a module (<c>MeshWeaver.AI</c>
    /// left <c>ContentSurfaceAssemblies</c> in #2276), so it resolves ONLY through module
    /// composition. Real content does exactly this: <c>Store/Installer</c>'s Localizer merges a
    /// plugin's Skill folder into the viewer's <c>AiSettings</c>.
    /// </summary>
    private const string BoundSource =
        """
        using MeshWeaver.AI;

        public record Bound
        {
            public AiSettings Settings { get; init; } = new();
        }
        """;

    [Fact(Timeout = 300_000)]
    public void ContentBindingAModuleType_Bakes_AndCannotWithoutTheModule()
    {
        var repo = TempDirectory("mw-module-bound-repo");
        var withModules = TempDirectory("mw-module-bound-bake-with");
        var withoutModules = TempDirectory("mw-module-bound-bake-without");
        try
        {
            Write(repo, "ModuleBound/index.json", IndexJson);
            Write(repo, "ModuleBound/Bound.json", TypeJson);
            Write(repo, "ModuleBound/Bound/Source/Bound.cs", BoundSource);

            // ── The claim: composed modules ⇒ the module-bound type compiles. ──
            var composedLog = new StringWriter();
            var composed = TreeBake.Run(new TreeBake.Options
            {
                RepoRoot = repo,
                OutputDirectory = withModules,
                Output = composedLog,
                ModuleAssemblyPaths = TesterModules.ResolvedPaths(null),
            });
            output.WriteLine("── with modules ──");
            output.WriteLine(composedLog.ToString());

            Assert.Null(composed.FatalError);
            Assert.All(composed.Types, t => Assert.Null(t.Error));
            Assert.NotEmpty(composed.Types);

            // ── THE CONTROL. Without the modules the SAME tree must fail, and fail by not finding
            //    the type — otherwise this test cannot tell the fix from the regression. ──
            var bareLog = new StringWriter();
            var bare = TreeBake.Run(new TreeBake.Options
            {
                RepoRoot = repo,
                OutputDirectory = withoutModules,
                Output = bareLog,
                ModuleAssemblyPaths = [],
            });
            output.WriteLine("── without modules (control) ──");
            output.WriteLine(bareLog.ToString());

            var failure = Assert.Single(
                Array.FindAll(bare.Types.ToArray(), t => !t.Success));
            Assert.Contains("AiSettings", failure.Error);
        }
        finally
        {
            Cleanup(repo);
            Cleanup(withModules);
            Cleanup(withoutModules);
        }
    }

    /// <summary>
    /// The two lanes read ONE list. This is the property whose absence caused the outage: the gate
    /// activated a module the bake never referenced, so "the gate has it" said nothing about the
    /// bake — and the divergence was only observable as a red bake beside a green gate.
    /// </summary>
    [Fact]
    public void BothLanes_TakeTheirModulesFromOneList()
    {
        var external = new[] { "/mnt/modules/Acme.Widgets/Acme.Widgets.dll" };

        var entries = TesterModules.Entries(external);

        // The image-shipped set comes first and is never dropped when externals are supplied —
        // forgetting it is exactly how the bake would go back to resolving fewer types than the
        // portal that consumes its bundles.
        Assert.Equal(TesterModules.ImageShipped[0], entries[0]);
        Assert.Contains(external[0], entries);
        Assert.Equal(TesterModules.ImageShipped.Length + external.Length, entries.Count);

        // An absolute path survives resolution untouched, so a mounted module can never be
        // silently substituted by an image copy of the same name.
        Assert.Contains(external[0], TesterModules.ResolvedPaths(external));
    }

    private static string TempDirectory(string prefix) =>
        Path.Combine(Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N"));

    private static void Write(string root, string relative, string content)
    {
        var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void Cleanup(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        catch { /* best effort — a locked temp dir must not fail the test */ }
    }
}
