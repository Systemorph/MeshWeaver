using System;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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
/// <para><b>How that stayed invisible.</b> The two lanes of this same tool disagreed silently. The
/// GATE stands up a mesh, so it composed modules for free and its <c>compile-check</c> went GREEN;
/// only <c>publish-bake</c> went red — on the same content, the same image and the same commit.
/// Nothing in the failure names a module: it arrives as <c>CS0246: The type or namespace name
/// 'AiSettings' could not be found</c>, which reads as a content defect. When the AI engine became
/// a module (#2276), five <c>Store/*</c> NodeTypes stopped resolving it in the bake and nowhere
/// else; no bundle was sealed for the new framework identity, every install then CORRECTLY declined
/// to self-update, and the fleet sat three published images behind with every component reporting
/// success (#2563).</para>
///
/// <para><b>The probe module is built here, on purpose.</b> This test used to bind the AI engine,
/// which this repo shipped as an image module — and then the engine LEFT the repo (#2276) and the
/// test asserted nothing. Compiling a throwaway assembly instead means the guard depends on no
/// particular module existing, so it cannot rot the same way twice.</para>
/// </summary>
public class BakeCompilesAgainstModulesTest(ITestOutputHelper output)
{
    private const string IndexJson =
        """{"$type":"MeshNode","id":"ModuleBound","namespace":"","path":"ModuleBound","mainNode":"ModuleBound","name":"Module Bound","nodeType":"Space","state":"Active","content":{"$type":"PluginManifest","description":"Content that binds a module type."}}""";

    private const string TypeJson =
        """{"$type":"MeshNode","id":"Bound","namespace":"ModuleBound","path":"ModuleBound/Bound","mainNode":"ModuleBound/Bound","name":"Bound","nodeType":"NodeType","state":"Active","content":{"$type":"NodeTypeDefinition","description":"Binds a type that lives in a MODULE, not on the content surface.","configuration":"config => config.WithContentType<Bound>().AddDefaultLayoutAreas()","includeGlobalTypes":true}}""";

    /// <summary>Content that binds a type which exists ONLY in the probe module.</summary>
    private const string BoundSource =
        """
        using Probe.Module;

        public record Bound
        {
            public ProbeSetting Setting { get; init; } = new();
        }
        """;

    /// <summary>The probe module's own source — deliberately tiny and dependency-free.</summary>
    private const string ProbeModuleSource =
        """
        namespace Probe.Module;

        public record ProbeSetting
        {
            public string Value { get; init; } = string.Empty;
        }
        """;

    [Fact(Timeout = 300_000)]
    public void ContentBindingAModuleType_Bakes_AndCannotWithoutTheModule()
    {
        var repo = TempDirectory("mw-module-bound-repo");
        var moduleDir = TempDirectory("mw-module-bound-module");
        var withModules = TempDirectory("mw-module-bound-bake-with");
        var withoutModules = TempDirectory("mw-module-bound-bake-without");
        try
        {
            Write(repo, "ModuleBound/index.json", IndexJson);
            Write(repo, "ModuleBound/Bound.json", TypeJson);
            Write(repo, "ModuleBound/Bound/Source/Bound.cs", BoundSource);
            var modulePath = EmitProbeModule(moduleDir);
            output.WriteLine($"probe module: {modulePath}");

            // ── The claim: a composed module ⇒ content binding its type compiles. ──
            var composedLog = new StringWriter();
            var composed = TreeBake.Run(new TreeBake.Options
            {
                RepoRoot = repo,
                OutputDirectory = withModules,
                Output = composedLog,
                ModuleAssemblyPaths = [modulePath],
            });
            output.WriteLine("── with the module ──");
            output.WriteLine(composedLog.ToString());

            Assert.Null(composed.FatalError);
            Assert.NotEmpty(composed.Types);
            Assert.All(composed.Types, t => Assert.Null(t.Error));

            // ── THE CONTROL. The SAME tree with no modules must FAIL, and fail by not finding the
            //    type — otherwise this test cannot tell the fix from the regression it pins. ──
            var bareLog = new StringWriter();
            var bare = TreeBake.Run(new TreeBake.Options
            {
                RepoRoot = repo,
                OutputDirectory = withoutModules,
                Output = bareLog,
                ModuleAssemblyPaths = [],
            });
            output.WriteLine("── without the module (control) ──");
            output.WriteLine(bareLog.ToString());

            var failure = Assert.Single(bare.Types.Where(t => !t.Success));
            // CS0246 on the namespace AND on the type — the module vanished wholesale, which is exactly
            // how this failure reads in the wild: a content defect, naming no module.
            Assert.Contains("ProbeSetting", failure.Error);
            Assert.Contains("could not be found", failure.Error);
        }
        finally
        {
            Cleanup(repo);
            Cleanup(moduleDir);
            Cleanup(withModules);
            Cleanup(withoutModules);
        }
    }

    /// <summary>
    /// The two lanes read ONE list — the property whose absence caused the outage: the gate
    /// activated a module the bake never referenced, so "the gate has it" said nothing about the
    /// bake, and the divergence was only visible as a red bake beside a green gate.
    /// </summary>
    [Fact]
    public void BothLanes_TakeTheirModulesFromOneList()
    {
        var external = new[] { "/mnt/modules/Acme.Widgets/Acme.Widgets.dll" };

        var entries = TesterModules.Entries(external);

        // Whatever this image ships comes FIRST and is never dropped when externals are supplied —
        // forgetting it is how the bake would go back to resolving fewer types than the portal that
        // consumes its bundles. (ImageShipped is empty since the AI engine left this repo, so today
        // that prefix is zero-length; the ordering contract is asserted either way.)
        Assert.Equal(
            TesterModules.ImageShipped.Concat(external),
            entries);
        Assert.Equal(TesterModules.ImageShipped.Length + external.Length, entries.Count);

        // An absolute path survives resolution untouched, so a mounted module can never be silently
        // substituted by an image copy of the same name.
        Assert.Contains(external[0], TesterModules.ResolvedPaths(external));
    }

    /// <summary>
    /// Compiles <see cref="ProbeModuleSource"/> to a real assembly on disk — a stand-in for a
    /// module published under <c>modules/&lt;name&gt;/</c>, i.e. a file the compiler can reference
    /// but which is NOT in this process's <c>TRUSTED_PLATFORM_ASSEMBLIES</c>.
    /// </summary>
    private static string EmitProbeModule(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "Probe.Module.dll");
        var compilation = CSharpCompilation.Create(
            "Probe.Module",
            [CSharpSyntaxTree.ParseText(ProbeModuleSource)],
            CompileReferencesForProbe(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var emit = compilation.Emit(path);
        Assert.True(
            emit.Success,
            "the probe module did not compile: "
            + string.Join("; ", emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return path;
    }

    private static MetadataReference[] CompileReferencesForProbe() =>
    [
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        .. ((AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty)
            .Split(Path.PathSeparator)
            .Where(p => !string.IsNullOrEmpty(p) && File.Exists(p))
            .Where(p => Path.GetFileName(p) is "System.Runtime.dll" or "netstandard.dll")
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))),
    ];

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
