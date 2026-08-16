#pragma warning disable CS1591

using System.Text;
using MeshWeaver.Plugin.Build;
using MeshWeaver.Plugin.Packaging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The <c>module-pack</c> CLI (#1664 Slice B): its inputs flow into FILE PATHS (the entry-DLL
/// probe, the closure entries, the output bundle name), so path-injection-shaped values must be a
/// clear exit-2 refusal — and what it packs must read back through the ONE reader
/// (<see cref="BundleReader.ReadModule"/>) the consumers use.
/// </summary>
public class ModulePackCommandTest : IDisposable
{
    private readonly string root =
        Path.Combine(Path.GetTempPath(), "mw-module-pack-" + Guid.NewGuid().ToString("N"));

    public ModulePackCommandTest()
    {
        Directory.CreateDirectory(Path.Combine(root, "closure"));
        File.WriteAllBytes(Path.Combine(root, "closure", "Widget.dll"), "WIDGET"u8.ToArray());
    }

    public void Dispose()
    {
        // Best-effort temp cleanup — must run on assertion failure too, never mask the failure.
        try
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
        catch
        {
            // Leaked temp dirs are the OS's to reap; a cleanup error must not fail the test.
        }
    }

    [Theory]
    [InlineData("--module-name", "../evil")]
    [InlineData("--module-name", "a/b")]
    [InlineData("--plugin", "..\\..\\escape")]
    [InlineData("--package-version", "1.0.0/../../x")]
    public void PathInjectionShapedInputs_AreRejected_BeforeAnyPathIsComposed(
        string option, string value)
    {
        // 🚨 The Copilot finding: these values reach Path.Combine (entry-DLL probe, bundle file
        // name), so a traversal-shaped value must be a clear refusal naming it — never a probe
        // outside the module folder or a bundle written somewhere surprising.
        var args = new List<string>
        {
            Path.Combine(root, "closure"),
            "--module-name", "Widget",
            "--plugin", "WidgetPkg",
            "--package-version", "1.0.0",
            "--out", Path.Combine(root, "out"),
        };
        args[args.IndexOf(option) + 1] = value;

        var exit = ModulePackCommand.Run([.. args]);

        Assert.Equal(2, exit);
        Assert.False(Directory.Exists(Path.Combine(root, "out")),
            "a refused invocation must not have written anything");
    }

    [Fact]
    public void APackedModuleBundle_RoundTripsThroughTheReader()
    {
        var outDir = Path.Combine(root, "out");
        var exit = ModulePackCommand.Run(
        [
            Path.Combine(root, "closure"),
            "--module-name", "Widget",
            "--plugin", "WidgetPkg",
            "--package-version", "1.2.0",
            "--min-mesh-version", "3.0.0",
            "--out", outDir,
        ]);

        Assert.Equal(0, exit);
        var bundlePath = Path.Combine(outDir, "MeshWeaver.Plugin.WidgetPkg.1.2.0.module.nupkg");
        Assert.True(File.Exists(bundlePath));

        var (manifest, files) = BundleReader.ReadModule(File.ReadAllBytes(bundlePath));

        Assert.Equal("WidgetPkg", manifest!.Plugin);
        Assert.Equal("1.2.0", manifest.Version);
        Assert.Equal("Widget", manifest.Module!.AssemblyName);
        Assert.Equal("3.0.0", manifest.Module.MinMeshVersion);
        // No MeshWeaver.Graph.dll in the closure folder: the diagnostic MVID is simply
        // unrecorded — warned, never an error, never a gate.
        Assert.Null(manifest.FrameworkMvid);
        var only = Assert.Single(files);
        Assert.Equal("Widget.dll", only.FileName);
        Assert.Equal("WIDGET", Encoding.UTF8.GetString(only.Bytes));
    }
}
