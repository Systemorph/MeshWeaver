using System;
using System.IO;
using System.Linq;
using System.Text;
using MeshWeaver.Mesh;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using MeshWeaver.PluginTester;
using Xunit;

namespace MeshWeaver.PluginTester.Test;

/// <summary>
/// The compiler-driven bake writes the forward dependency edges it computed to
/// <see cref="DependencyEdgesFile.FileName"/> beside the bundles — the graph's transport to a build
/// step with no mesh (<c>Doc/Architecture/DependencyGraph</c> → <i>Where it runs</i>). Before this
/// the bake computed every edge set and dropped it.
///
/// <para>The tree: <c>Pkg/User</c> binds a type <c>Pkg/Lib</c> owns (through a <c>shared=</c>
/// source query) and a type in a module; <c>Pkg/Lib</c> binds nothing; <c>Pkg/Broken</c> does not
/// compile. The negative controls are that the edge does not run backwards (<c>Lib → User</c>), that
/// a type with no module use records no module edge, and that a failed type is listed as FAILED —
/// never as a type with an empty edge set.</para>
/// </summary>
public class BakeWritesDependencyEdgesTest(ITestOutputHelper output)
{
    private const string IndexJson =
        """{"$type":"MeshNode","id":"Pkg","namespace":"","path":"Pkg","mainNode":"Pkg","name":"Pkg","nodeType":"Space","state":"Active","content":{"$type":"PluginManifest","description":"Edge probe."}}""";

    private const string LibJson =
        """{"$type":"MeshNode","id":"Lib","namespace":"Pkg","path":"Pkg/Lib","mainNode":"Pkg/Lib","name":"Lib","nodeType":"NodeType","state":"Active","content":{"$type":"NodeTypeDefinition","description":"Owns LibThing.","configuration":"config => config.WithContentType<LibThing>().AddDefaultLayoutAreas()","includeGlobalTypes":true}}""";

    private const string UserJson =
        """{"$type":"MeshNode","id":"User","namespace":"Pkg","path":"Pkg/User","mainNode":"Pkg/User","name":"User","nodeType":"NodeType","state":"Active","content":{"$type":"NodeTypeDefinition","description":"Binds LibThing and a module type.","configuration":"config => config.WithContentType<UserThing>().AddDefaultLayoutAreas()","includeGlobalTypes":true,"sources":["namespace:Source scope:subtree","shared=@Pkg/Lib/Source"]}}""";

    private const string BrokenJson =
        """{"$type":"MeshNode","id":"Broken","namespace":"Pkg","path":"Pkg/Broken","mainNode":"Pkg/Broken","name":"Broken","nodeType":"NodeType","state":"Active","content":{"$type":"NodeTypeDefinition","description":"Does not compile.","configuration":"config => config.WithContentType<BrokenThing>().AddDefaultLayoutAreas()","includeGlobalTypes":true}}""";

    private const string LibSource =
        """
        public record LibThing
        {
            public string Value { get; init; } = string.Empty;
        }
        """;

    private const string UserSource =
        """
        public record UserThing
        {
            public LibThing Lib { get; init; } = new();
            public Probe.Edges.ProbeSetting Setting { get; init; } = new();
        }
        """;

    private const string BrokenSource =
        """
        public record BrokenThing
        {
            public MissingType Nope { get; init; } = null!;
        }
        """;

    private const string ProbeModuleSource =
        """
        namespace Probe.Edges;

        public record ProbeSetting
        {
            public string Value { get; init; } = string.Empty;
        }
        """;

    [Fact(Timeout = 300_000)]
    public void TheBake_WritesEveryAttemptedTypesEdges_AndNamesTheOnesItCouldNotRead()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var repo = TempDirectory("mw-edges-repo");
        var moduleDir = TempDirectory("mw-edges-module");
        var bake = TempDirectory("mw-edges-bake");
        try
        {
            Write(repo, "Pkg/index.json", IndexJson);
            Write(repo, "Pkg/Lib.json", LibJson);
            Write(repo, "Pkg/Lib/Source/LibThing.cs", LibSource);
            Write(repo, "Pkg/User.json", UserJson);
            Write(repo, "Pkg/User/Source/UserThing.cs", UserSource);
            Write(repo, "Pkg/Broken.json", BrokenJson);
            Write(repo, "Pkg/Broken/Source/BrokenThing.cs", BrokenSource);
            var modulePath = EmitProbeModule(moduleDir);

            var log = new StringWriter();
            var report = TreeBake.Run(new TreeBake.Options
            {
                RepoRoot = repo,
                OutputDirectory = bake,
                Output = log,
                SourceSha = "0123456789abcdef0123456789abcdef01234567",
                ModuleAssemblyPaths = [modulePath],
            });
            output.WriteLine(log.ToString());
            Assert.Null(report.FatalError);

            var path = Path.Combine(bake, DependencyEdgesFile.FileName);
            Assert.True(File.Exists(path), $"the bake wrote no {DependencyEdgesFile.FileName}");
            var json = File.ReadAllText(path);
            output.WriteLine(json);
            var document = DependencyEdgesFile.Deserialize(json);
            Assert.NotNull(document);
            Assert.Equal(DependencyEdgesFile.Schema, document.Schema);
            Assert.Equal(report.FrameworkIdentity, document.FrameworkIdentity);

            // Every attempted type appears exactly once — as a set, or as failed.
            Assert.Equal(["Pkg/Lib", "Pkg/User"], document.Sets.Select(s => s.Source).ToArray());
            Assert.Equal(["Pkg/Broken"], document.Failed.ToArray());

            var user = document.Sets.Single(s => s.Source == "Pkg/User");
            var lib = document.Sets.Single(s => s.Source == "Pkg/Lib");

            // The claim: Roslyn's edges, carried whole, each with the identity of its build.
            Assert.Contains(user.Edges, e => e.Kind == DependencyEdgeKinds.NodeType && e.Target == "Pkg/Lib");
            Assert.Contains(user.Edges, e => e.Kind == DependencyEdgeKinds.Module && e.Target == "Probe.Edges");
            Assert.All(document.Sets, s =>
            {
                Assert.False(string.IsNullOrEmpty(s.BuildMvid), $"{s.Source} carries no build identity");
                Assert.Equal("0123456789abcdef0123456789abcdef01234567", s.Commit);
            });

            // Negative controls: the edge does not run backwards, and a type that uses no module
            // records no module edge.
            Assert.DoesNotContain(lib.Edges, e => e.Target == "Pkg/User");
            Assert.DoesNotContain(lib.Edges, e => e.Kind == DependencyEdgeKinds.Module);
        }
        finally
        {
            Cleanup(repo);
            Cleanup(moduleDir);
            Cleanup(bake);
        }
    }

    /// <summary>A schema this reader does not know is refused, never read as an empty graph.</summary>
    [Fact]
    public void AnUnknownSchema_IsRefused()
    {
        Assert.Null(DependencyEdgesFile.Deserialize(
            """{"schema":2,"frameworkIdentity":"x","sets":[],"failed":[]}"""));
        Assert.NotNull(DependencyEdgesFile.Deserialize(
            """{"schema":1,"frameworkIdentity":"x","sets":[],"failed":[]}"""));
    }

    private static string EmitProbeModule(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "Probe.Edges.dll");
        var compilation = CSharpCompilation.Create(
            "Probe.Edges",
            [CSharpSyntaxTree.ParseText(ProbeModuleSource)],
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                .. ((AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty)
                    .Split(Path.PathSeparator)
                    .Where(p => !string.IsNullOrEmpty(p) && File.Exists(p))
                    .Where(p => Path.GetFileName(p) is "System.Runtime.dll" or "netstandard.dll")
                    .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))),
            ],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var emit = compilation.Emit(path);
        Assert.True(emit.Success, "the probe module did not compile: "
            + string.Join("; ", emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return path;
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
