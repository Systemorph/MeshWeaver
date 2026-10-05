using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using MeshWeaver.Compiler;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The shared fixture of the ladder's D and H rows (<c>Doc/Architecture/ModuleUpdateLadder</c>):
/// REAL emitted builds of an <c>MeshWeaver.AI</c>-shaped module, and a <c>Hosting</c>-shaped NodeType
/// compiled through the bake's real NodeType compile path (<see cref="NodeSetCompiler.Compile"/>, the
/// same shaping, generator and emit the runtime compile uses).
///
/// <para>Two builds of the module state the SAME informational version (<c>3.0.0.0</c>, inherited
/// from the platform, exactly as every AI build does) and DIFFERENT package versions. Only the newer
/// one has <c>ThreadPreparation.Group</c> — the member the 2026-10-05 incident's Hosting prebuilt
/// called.</para>
/// </summary>
internal sealed class LadderModuleFixture : IDisposable
{
    public const string Module = "MeshWeaver.AI";
    public const string Older = "1.20.4";
    public const string Newer = "1.21.0";
    public const string PlatformStamp = "3.0.0.0";
    public const string TypePath = "Hosting/OwningThread";

    /// <summary>The Hosting source that SETS the member only the newer build has.</summary>
    public const string UsesGroup =
        """
        using MeshWeaver.AI.Threading;

        /// <summary>The PR owning thread's preparation.</summary>
        public static class OwningThread
        {
            /// <summary>Prepares the thread and returns the group it was started in.</summary>
            public static string Start()
            {
                var preparation = new ThreadPreparation { Agent = "reviewer", Group = "PullRequests" };
                return preparation.Group!;
            }
        }
        """;

    /// <summary>A Hosting source that uses only what BOTH builds have.</summary>
    public const string UsesAgentOnly =
        """
        using MeshWeaver.AI.Threading;

        /// <summary>The PR owning thread's preparation.</summary>
        public static class OwningThread
        {
            /// <summary>Prepares the thread and returns the agent it was started for.</summary>
            public static string Start()
            {
                var preparation = new ThreadPreparation { Agent = "reviewer" };
                return preparation.Agent!;
            }
        }
        """;

    private readonly ImmutableList<AssemblyLoadContext>.Builder contexts = ImmutableList.CreateBuilder<AssemblyLoadContext>();

    public string Root { get; } = Path.Combine(Path.GetTempPath(), $"mw-ladder-{Guid.NewGuid():N}");

    public LadderModuleFixture() => Directory.CreateDirectory(Root);

    public void Dispose()
    {
        foreach (var context in contexts)
            context.Unload();
        try
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory costs disk, never correctness.
        }
        catch (UnauthorizedAccessException)
        {
            // Same: an assembly still mapped by an unloading context.
        }
    }

    /// <summary>Emits one build of <see cref="Module"/> into <c>Root/&lt;build&gt;/</c> and returns
    /// its path. <paramref name="withGroup"/> decides whether <c>ThreadPreparation.Group</c> exists.</summary>
    public string EmitModule(string build, string? packageVersion, bool withGroup)
    {
        var packageLine = packageVersion is null
            ? string.Empty
            : $"""[assembly: System.Reflection.AssemblyMetadata("{InstalledModuleAssembly.PackageVersionMetadataKey}", "{packageVersion}")]""";
        var groupLine = withGroup ? "    public string? Group { get; set; }" : string.Empty;
        var source = $$"""
            [assembly: System.Reflection.AssemblyInformationalVersion("{{PlatformStamp}}")]
            {{packageLine}}
            namespace MeshWeaver.AI.Threading;
            public record ThreadPreparation
            {
                public string? Agent { get; init; }
            {{groupLine}}
            }
            """;
        var compilation = CSharpCompilation.Create(
            Module,
            [CSharpSyntaxTree.ParseText(source)],
            PlatformReferences.Platform(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        using var buffer = new MemoryStream();
        var result = compilation.Emit(buffer);
        Assert.True(result.Success, string.Join(
            Environment.NewLine, result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));

        var directory = Path.Combine(Root, build);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Module + ".dll");
        File.WriteAllBytes(path, buffer.ToArray());
        return path;
    }

    /// <summary>Loads a module build in its own collectible context (several builds of one simple
    /// name coexist in one process, as an image copy and a store copy do across a restart).</summary>
    public Assembly LoadModule(string path)
    {
        var context = new AssemblyLoadContext($"mw-ladder-module-{Guid.NewGuid():N}", isCollectible: true);
        contexts.Add(context);
        return context.LoadFromAssemblyPath(path);
    }

    /// <summary>The dependency-id resolver over ONE installed module, built exactly as
    /// <c>NodeTypeCompilationHelpers.DependencyIdResolverOf</c> builds the portal's — the live
    /// compatibility key, the module's MVID, informational and PACKAGE version.</summary>
    public static Func<string, string?> ResolverOver(Assembly module)
    {
        var installed = new InstalledModuleAssembly(module);
        return CompiledDependencies.CreateCompatibilityIdResolver(
            NodeTypeCompilationHelpers.FrameworkVersion,
            new Dictionary<string, string> { [Module] = installed.Mvid.ToString("N") },
            name => name == Module ? installed.Version : null,
            name => name == Module ? installed.PackageVersion : null);
    }

    /// <summary>The resolver as it was BEFORE #6116 — no package version, so the module id is the
    /// informational <c>min:3.0.0.0</c> every build states. The incident's mechanism.</summary>
    public static Func<string, string?> InformationalOnlyResolverOver(Assembly module)
    {
        var installed = new InstalledModuleAssembly(module);
        return CompiledDependencies.CreateCompatibilityIdResolver(
            NodeTypeCompilationHelpers.FrameworkVersion,
            new Dictionary<string, string> { [Module] = installed.Mvid.ToString("N") },
            name => name == Module ? installed.Version : null);
    }

    /// <summary>
    /// Compiles the Hosting-shaped NodeType through <see cref="NodeSetCompiler.Compile"/> against the
    /// platform references composed with <paramref name="module"/> — exactly what the portal's compile
    /// composes (<see cref="CompileReferences.ComposeWithModules"/>) — recording its dependency record
    /// over <paramref name="dependencyIdOf"/>.
    /// </summary>
    public NodeSetCompiler.CompiledNode CompileHosting(string build, string code, Assembly module, Func<string, string?> dependencyIdOf)
    {
        var typeNode = new MeshNode("OwningThread", "Hosting")
        {
            NodeType = "NodeType",
            Name = "Owning Thread",
            State = MeshNodeState.Active,
            Content = new NodeTypeDefinition { Description = "Hosting-shaped type that prepares a PR thread." },
        };
        var sourceNode = new MeshNode("OwningThread", $"{TypePath}/Source")
        {
            NodeType = "Code",
            Name = "Owning Thread",
            State = MeshNodeState.Active,
            Content = new CodeConfiguration { Code = code, Language = "csharp" },
        };
        return NodeSetCompiler.Compile(
            NodeSet.Create([typeNode, sourceNode]),
            typeNode,
            sources: null,
            tests: null,
            configuration: null,
            contentCollections: null,
            references: CompileReferences.ComposeWithModules([new InstalledModuleAssembly(module)]),
            dependencyIdOf: dependencyIdOf,
            toolchainId: NodeTypeCompilationHelpers.ProcessToolchainId,
            outputDirectory: Path.Combine(Root, "compiled-" + build));
    }

    /// <summary>
    /// RUNS the compiled Hosting type's <c>OwningThread.Start()</c> with <c>MeshWeaver.AI</c> bound
    /// to <paramref name="modulePath"/> — what a process that loaded that module build would do with
    /// these bytes. Every other reference resolves through the default context, as a NodeType's does.
    /// </summary>
    public object? RunStart(string compiledDllPath, string modulePath)
    {
        var context = new ModuleBindingContext(modulePath);
        contexts.Add(context);
        var assembly = context.LoadFromAssemblyPath(compiledDllPath);
        var type = assembly.GetType("OwningThread", throwOnError: true)!;
        return type.GetMethod("Start", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null);
    }

    private sealed class ModuleBindingContext(string modulePath)
        : AssemblyLoadContext($"mw-ladder-run-{Guid.NewGuid():N}", isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName) =>
            string.Equals(assemblyName.Name, Module, StringComparison.Ordinal)
                ? LoadFromAssemblyPath(modulePath)
                : null;
    }
}
