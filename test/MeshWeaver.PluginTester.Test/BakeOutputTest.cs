#pragma warning disable CS1591

using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text;
using System.Threading.Tasks;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Plugin.Packaging;
using MeshWeaver.PluginTester;
using Xunit;

namespace MeshWeaver.PluginTester.Test;

/// <summary>
/// Pins the ARTIFACT half of the content gate (#1660 WS1): a gate run given
/// <see cref="GateOptions.BakeOutputDirectory"/> persists what it compiled as a prebuilt-assembly
/// bundle a consumer can ADOPT — i.e. the bundle round-trips through <see cref="BundleReader"/>
/// (the one codec every consumer uses) and its manifest carries the LIVE framework MVID, so
/// <see cref="PrebuiltAssemblySeeder.DeclineReason(string?)"/> accepts it on the producing build.
///
/// <para>This is the seeder-conformance evidence: the producer is only correct if the exact gate
/// that holds at consumption (<c>DeclineReason</c>) accepts its output. Asserting file layout
/// without that gate would let the producer drift into writing bundles every consumer politely
/// refuses — dead weight that LOOKS like the feature shipped.</para>
/// </summary>
public class BakeOutputTest(ITestOutputHelper output)
{
    private const string WidgetIndexJson =
        """{"$type":"MeshNode","id":"Widget","namespace":"","path":"Widget","mainNode":"Widget","name":"Widget Plugin","nodeType":"Space","state":"Active","content":{"$type":"PluginManifest","description":"A widget plugin."}}""";

    private const string ThingNodeTypeJson =
        """{"$type":"MeshNode","id":"Thing","namespace":"Widget","path":"Widget/Thing","mainNode":"Widget/Thing","name":"Thing","nodeType":"NodeType","state":"Active","content":{"$type":"NodeTypeDefinition","description":"A thing.","configuration":"config => config.WithContentType<Thing>().AddDefaultLayoutAreas()","includeGlobalTypes":true}}""";

    private const string ThingSource =
        """
        public record Thing
        {
            public string Name { get; init; } = string.Empty;

            public int Answer() => 42;
        }
        """;

    [Fact(Timeout = 300_000)]
    public async Task GateRunWithBakeOutput_WritesABundleTheConsumersGateAccepts()
    {
        var repo = CreateRepo(root =>
        {
            WriteFile(root, "Widget/index.json", WidgetIndexJson);
            WriteFile(root, "Widget/Thing.json", ThingNodeTypeJson);
            WriteFile(root, "Widget/Thing/Source/Thing.cs", ThingSource);
        });
        var bakeDir = Path.Combine(Path.GetTempPath(), "mw-bake-" + Guid.NewGuid().ToString("N"));
        var log = new StringWriter();
        try
        {
            var report = await PluginGateRunner.Run(new GateOptions
                {
                    RepoRoot = repo,
                    Output = log,
                    CompileTimeout = TimeSpan.FromMinutes(4),
                    RenderTimeout = TimeSpan.FromSeconds(90),
                    BakeOutputDirectory = bakeDir,
                    SourceSha = "test-sha-0001",
                })
                .FirstAsync()
                .ToTask(TestContext.Current.CancellationToken);

            report.FatalError.Should().BeNull($"the run must complete; log:\n{log}");
            report.ExitCode.Should().Be(0, $"the fixture is green; log:\n{log}");

            // The MVID-keyed artifact identity CI reads to name the upload.
            var mvidFile = Path.Combine(bakeDir, BakeOutput.FrameworkMvidFile);
            File.Exists(mvidFile).Should().BeTrue("the bake must name the framework it is keyed to");
            var recordedMvid = File.ReadAllText(mvidFile).Trim();
            recordedMvid.Should().Be(PrebuiltAssemblySeeder.LiveFrameworkMvid);

            // One bundle per package, readable by the ONE consumer codec.
            var bundlePath = Path.Combine(bakeDir, "Widget.zip");
            File.Exists(bundlePath).Should().BeTrue(
                $"the compiled package must produce a bundle; bake dir holds: "
                + $"{string.Join(", ", Directory.EnumerateFileSystemEntries(bakeDir).Select(Path.GetFileName))}");
            var (manifest, assemblies) = BundleReader.Read(
                await File.ReadAllBytesAsync(bundlePath, TestContext.Current.CancellationToken));

            manifest.Should().NotBeNull();
            manifest!.Plugin.Should().Be("Widget");
            manifest.FrameworkMvid.Should().Be(PrebuiltAssemblySeeder.LiveFrameworkMvid);

            // 🚨 The consumption pin: the exact gate every consumer holds at adoption time must
            // accept this producer's identity — on the producing build, where they share one
            // Graph.dll. (A DIFFERENT build correctly declines; that is WS3's problem, not ours.)
            PrebuiltAssemblySeeder.DeclineReason(manifest.FrameworkMvid).Should().BeNull();

            var thing = assemblies.Should().ContainSingle().Subject;
            thing.NodePath.Should().Be("Widget/Thing");
            thing.Assembly.Should().NotBeEmpty("the bundle must carry the compiled bytes");
        }
        finally
        {
            output.WriteLine(log.ToString());
            TryDelete(repo);
            TryDelete(bakeDir);
        }
    }

    private static string CreateRepo(Action<string> populate)
    {
        var root = Path.Combine(Path.GetTempPath(), "mw-bake-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        populate(root);
        return root;
    }

    private static void WriteFile(string root, string relative, string content)
    {
        var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // best effort — the OS reclaims temp at reboot
        }
    }
}
