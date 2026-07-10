#pragma warning disable CS1591

using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.GitSync;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// Stage 3 — runtime code-load. Installs a <c>kind=code</c> package (a NodeType configuration in the
/// manifest + a <c>Source/*.cs</c> file) from a git repo, and asserts the mesh compiles the freshly
/// installed NodeType LIVE (Roslyn first-build → <see cref="CompilationStatus.Ok"/>) — no app rebuild,
/// no NuGet. This reuses the same compile/release flow that <c>CompileActivityLogTest</c> exercises.
/// </summary>
public class CodePackageInstallTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddGraph().AddPluginCatalog();

    [Fact(Timeout = 120_000)]
    public async Task CodePackage_InstallsNodeTypeAndSource_CompilesLive()
    {
        var repo = CreateTempDir();
        try
        {
            var git = new GitCli(Mesh.ServiceProvider.GetRequiredService<IoPoolRegistry>());

            WriteFile(repo, "catalog/widget-type/package.json",
                """{"id":"widget-type","name":"Widget","kind":"code","targetPartition":"type","version":"1.0.0","nodeTypeConfiguration":"config => config.WithContentType<Widget>()"}""");
            WriteFile(repo, "catalog/widget-type/Source/Widget.cs",
                "public record Widget { public string Title { get; init; } = string.Empty; }");

            await git.Run(repo, ["init"]).FirstAsync().ToTask();
            await git.Run(repo, ["add", "-A"]).FirstAsync().ToTask();
            await git.Run(repo, ["-c", "user.email=t@t", "-c", "user.name=t", "commit", "-m", "init"])
                .FirstAsync().ToTask();

            var source = new GitPackageSource(git, repo, "catalog");
            var pkg = (await source.ListPackages("HEAD").FirstAsync().ToTask()).First(p => p.Id == "widget-type");
            pkg.Kind.Should().Be(PackageKind.Code);
            var files = await source.FetchPackageFiles(pkg, "HEAD").FirstAsync().ToTask();

            var result = await PackageInstaller.Install(Mesh, pkg, files, "HEAD").FirstAsync().ToTask();
            result.Total.Should().Be(2); // the NodeType node + one Source Code node
            result.Written.Should().Be(2); // both freshly written on first install

            // The mesh compiles the just-installed NodeType (first build) and settles Ok — the custom
            // type is now live in a running mesh, installed purely by picking a git commit + folder.
            var node = await Mesh.GetMeshNodeStream("type/widget-type")
                .Should().Within(90.Seconds())
                .Match(n => n?.Content is NodeTypeDefinition d
                    && d.CompilationStatus is CompilationStatus.Ok or CompilationStatus.Error);

            var def = (NodeTypeDefinition)node.Content!;
            def.CompilationStatus.Should().Be(CompilationStatus.Ok,
                $"the installed code package must compile live; error: {def.CompilationError}");
        }
        finally
        {
            TryDelete(repo);
        }
    }

    private static string CreateTempDir()
    {
        var p = Path.Combine(Path.GetTempPath(), "pkgcode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(p);
        return p;
    }

    private static void WriteFile(string root, string relative, string content)
    {
        var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); }
        catch { /* best effort */ }
    }
}
