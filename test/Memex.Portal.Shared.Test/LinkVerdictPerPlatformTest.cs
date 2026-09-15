using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using MeshWeaver.PluginCatalog;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 #4026, Copilot's review of #4427: whether a generation LOADS is a fact about the bytes AND the
/// platform image, and a rolling update puts two images on one volume. The fallback is ranked
/// "loadable here first", so each image must rank by ITS OWN measurement — a verdict frozen by
/// whichever replica recorded first would keep a fleet on a stale answer for the length of the
/// roll, or for good.
///
/// <para>Two replicas on two IMAGES, deterministically: "image-old" is this process; "image-new" is
/// the same process handed a platform surface whose <c>MeshWeaver.Mesh.Contract</c> is a stand-in
/// carrying a type this build does not have — the memex-cloud shape of #3538. One module built
/// against that type is therefore unloadable on the old image and loadable on the new one, measured
/// by the real link probe on both.</para>
/// </summary>
public class LinkVerdictPerPlatformTest : IDisposable
{
    private const string Module = "MeshWeaver.Test.PerPlatformVerdict";
    private const string ContractAssembly = "MeshWeaver.Mesh.Contract";
    private const string FutureType = "MeshWeaver.Mesh.PerPlatformVerdictFromTheFuture";

    private readonly string root =
        Path.Combine(Path.GetTempPath(), "mw-verdict-per-platform-" + Guid.NewGuid().ToString("N"));

    /// <summary>Creates the landing root both replicas share.</summary>
    public LinkVerdictPerPlatformTest() => Directory.CreateDirectory(root);

    /// <inheritdoc />
    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch { /* temp cleanup is the OS's problem, never a test failure */ }
    }

    private static byte[] Emit(
        string assemblyName, string source, bool withoutRealContract = false, MetadataReference? extra = null)
    {
        var references = PlatformReferences.Platform(excludeReference: withoutRealContract ? ContractAssembly : null);
        if (extra is not null)
            references = references.Add(extra);
        var compilation = CSharpCompilation.Create(
            assemblyName, [CSharpSyntaxTree.ParseText(source)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var buffer = new MemoryStream();
        var result = compilation.Emit(buffer);
        Assert.True(result.Success, string.Join(Environment.NewLine,
            result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return buffer.ToArray();
    }

    /// <summary>A module using a type this platform really has — loadable on the old image.</summary>
    private static byte[] BuiltAgainstThisPlatform(string body) => Emit(Module, $$"""
        using MeshWeaver.Mesh;
        public static class Views
        {
            public static string Render(MeshNode node) => node.Id + "{{body}}";
        }
        """);

    /// <summary>
    /// A self-contained view of the new image: everything this process's platform carries, except
    /// that its contract is the stand-in — first in the list, so it is the copy the surface binds.
    /// </summary>
    private static Func<string[], ModulePlatformSurface> NewImage(string standIn) => directories =>
        ModulePlatformSurface.OfFiles(
            new[] { standIn }
                .Concat(((AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string) ?? string.Empty)
                    .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                    .Where(path => !string.Equals(Path.GetFileNameWithoutExtension(path), ContractAssembly,
                        StringComparison.OrdinalIgnoreCase)))
                .Concat(directories.Where(Directory.Exists)
                    .SelectMany(directory => Directory.EnumerateFiles(directory, "*.dll"))
                    .Where(path => !string.Equals(Path.GetFileNameWithoutExtension(path), ContractAssembly,
                        StringComparison.OrdinalIgnoreCase))));

    private static async Task<ModuleLandingOutcome> Shelve(ModuleLandingService replica, byte[] bytes, string version) =>
        await replica.ShelveModule(Module, [(Module + ".dll", bytes)], frameworkMvid: "test-build", version: version)
            .Timeout(TestTimeouts.Convergence).Await();

    private static async Task<ModuleActivationEntry> ViewOf(ModuleLandingService replica) =>
        Assert.Single((await replica.GetActivation().Timeout(TestTimeouts.Convergence).Await()).Entries);

    /// <summary>
    /// 🚨 Copilot's interleaving: the OLD image lands 1.7.0 first and measures it unloadable; the NEW
    /// image lands the very same bytes later and measures them loadable. The new image must rank its
    /// fallback by ITS verdict — 1.7.0, the newest generation that loads there — and the old image
    /// by its own — 1.6.0 — at the same moment, over the same volume. And the modules GC, which runs
    /// on either, must keep BOTH fallbacks, because both images are live during a roll.
    /// </summary>
    [Fact]
    public async Task EachImageRanksTheFallbackByItsOwnVerdict_NotTheFirstReplicasFrozenOne()
    {
        var standInBytes = Emit(ContractAssembly, $$"""
            namespace MeshWeaver.Mesh;
            /// <summary>The type the new image has and this build does not.</summary>
            public enum {{FutureType.Split('.').Last()}} { Unknown, Current }
            """, withoutRealContract: true);
        var standIn = Path.Combine(root, "image-new", ContractAssembly + ".dll");
        Directory.CreateDirectory(Path.GetDirectoryName(standIn)!);
        File.WriteAllBytes(standIn, standInBytes);
        var futureModule = Emit(Module, $$"""
            using MeshWeaver.Mesh;
            public static class Views
            {
                public static string Render({{FutureType.Split('.').Last()}} currency) => currency.ToString();
            }
            """, withoutRealContract: true, extra: MetadataReference.CreateFromImage(standInBytes));
        var oldLoadable = BuiltAgainstThisPlatform("1.6.0");
        var head = BuiltAgainstThisPlatform("1.8.0");

        // The preconditions that make this able to fail, measured by the real probe on both images.
        var closure = ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, Module);
        Assert.False(ModulePlatformLink.Check(futureModule, Module, closure,
            ModulePlatformSurface.OfRunningProcess(AppContext.BaseDirectory)).MayLoad,
            "precondition: the future-built generation does NOT load on the old image");
        Assert.True(ModulePlatformLink.Check(futureModule, Module, closure,
            NewImage(standIn)([AppContext.BaseDirectory])).MayLoad,
            "precondition: the same bytes DO load on the new image");

        using var oldImage = new ModuleLandingService(null, root, beforeRecording: null,
            platformIdentity: "image-old");
        using var newImage = new ModuleLandingService(null, root, beforeRecording: null,
            platformIdentity: "image-new", platformSurface: NewImage(standIn));

        await Shelve(oldImage, oldLoadable, "1.6.0");
        Assert.True((await Shelve(oldImage, futureModule, "1.7.0")).Held,
            "the old image measures 1.7.0 unloadable and HOLDS it on the shelf");
        await Shelve(oldImage, head, "1.8.0");
        Assert.Equal("1.6.0", (await ViewOf(oldImage)).PreviousVersion);

        // The new image lands the SAME bytes, later, and measures them loadable.
        Assert.False((await Shelve(newImage, futureModule, "1.7.0")).Held,
            "precondition: on the new image 1.7.0 links");

        var seenByNew = await ViewOf(newImage);
        Assert.Equal("1.8.0", seenByNew.Version);
        Assert.Equal("1.7.0", seenByNew.PreviousVersion);
        var seenByOld = await ViewOf(oldImage);
        Assert.Equal("1.8.0", seenByOld.Version);
        Assert.Equal("1.6.0", seenByOld.PreviousVersion);

        ModuleLandingService.CollectGarbage(root, minAge: TimeSpan.Zero, nowUtc: DateTime.UtcNow.AddHours(1));
        Assert.True(ModuleActivationBoot.PreviousLandedModuleDllExists(root, seenByNew),
            "the new image's fallback survives a GC pass");
        Assert.True(ModuleActivationBoot.PreviousLandedModuleDllExists(root, seenByOld),
            "and so does the old image's — both are live during a roll");
    }
}
