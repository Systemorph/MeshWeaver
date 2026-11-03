using System.Reflection;
using System.Runtime.Loader;
using MeshWeaver.Compiler;
using MeshWeaver.Messaging.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Xunit;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// A mesh-compiled NodeType's XML documentation reaches the reader wherever the dll a hub LOADS sits.
///
/// <para><b>What it was</b> (measured on a client instance, 2026-11-03): <c>@{instance}/schema/</c> of a
/// mesh-compiled content type (<c>Reinsurance/Acceptance</c>, every property documented with
/// <c>/// &lt;summary&gt;</c>) carried types and <c>x-meshNode</c> queries but NO <c>description</c>, while
/// platform types did. <see cref="XmlDocsReader"/> (since #7178) reads <c>{AssemblyName}.xml</c> beside
/// <see cref="Assembly.Location"/> and nowhere else. The compile writes <c>DynamicNode_{node}.xml</c> into its
/// release directory only; the assembly STORE publishes dll + pdb under a content-hashed name
/// (<c>v{n}-{tag}-{hash}.dll</c>), and a baked bundle carries dll + pdb. So neither a local compile loaded
/// from the store nor an adopted bundle ever had its documentation beside it.</para>
///
/// <para><b>The fix</b>: the emit embeds the compilation's own XML documentation in the image as the
/// manifest resource <c>{AssemblyName}.xml</c>; the reader falls back to it when no file is found. Each arm
/// below loads the emitted image the way the mesh does — out of a content-hashed path with no sidecar, and
/// from bytes — into a COLLECTIBLE load context.</para>
/// </summary>
public sealed class MeshCompiledTypesCarryTheirDocumentationTest : IDisposable
{
    private const string Source = """
        namespace DocProbe;

        /// <summary>A reinsurance acceptance.</summary>
        public record Acceptance
        {
            /// <summary>The line of business the acceptance covers — a path to a LineOfBusiness node.</summary>
            public string LineOfBusiness { get; init; } = "";
        }
        """;

    private readonly string root = Path.Combine(Path.GetTempPath(), "mw-doc-probe-" + Guid.NewGuid().ToString("N"));
    private readonly List<AssemblyLoadContext> contexts = [];

    private static CSharpCompilation Compilation(string assemblyName)
        => EmitPipeline.CreateEmitCompilation(Source, assemblyName,
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
             MetadataReference.CreateFromFile(Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Runtime.dll"))],
            parsePath: "", CancellationToken.None);

    private Assembly LoadFromPath(string path)
    {
        var context = new AssemblyLoadContext("doc-probe-" + Guid.NewGuid().ToString("N"), isCollectible: true);
        contexts.Add(context);
        return context.LoadFromAssemblyPath(path);
    }

    private Assembly LoadFromBytes(byte[] image)
    {
        var context = new AssemblyLoadContext("doc-probe-" + Guid.NewGuid().ToString("N"), isCollectible: true);
        contexts.Add(context);
        return context.LoadFromStream(new MemoryStream(image));
    }

    /// <summary>Copies the release directory's dll — and ONLY the dll and pdb — to a content-hashed name,
    /// the layout the assembly store and an adopted bundle both produce.</summary>
    private string AsStorePublished(string releaseDll)
    {
        var store = Path.Combine(root, "assembly-cache", "Reinsurance_Acceptance");
        Directory.CreateDirectory(store);
        var published = Path.Combine(store, "v18-c003e001-ca05d27ae753.dll");
        File.Copy(releaseDll, published);
        File.Copy(Path.ChangeExtension(releaseDll, ".pdb"), Path.ChangeExtension(published, ".pdb"));
        return published;
    }

    private static void AssertDocumented(Assembly assembly, string because)
    {
        var type = assembly.GetType("DocProbe.Acceptance");
        type.Should().NotBeNull();
        var reader = new XmlDocsReader();
        reader.Summary(type!).Should().Be("A reinsurance acceptance.", because);
        reader.Summary(type!.GetProperty("LineOfBusiness")!)
            .Should().Contain("path to a LineOfBusiness node", because);
    }

    [Fact]
    public void ALocalCompile_LoadedFromTheAssemblyStore_ReadsItsDocumentation()
    {
        var release = Path.Combine(root, "release");
        Directory.CreateDirectory(release);
        var artifact = EmitPipeline.EmitCompilationToDirectory(
            Compilation("DynamicNode_Reinsurance_Acceptance"), "Reinsurance_Acceptance", "Reinsurance/Acceptance",
            release, CancellationToken.None);

        var published = AsStorePublished(artifact.DllPath);
        File.Exists(Path.Combine(Path.GetDirectoryName(published)!, "DynamicNode_Reinsurance_Acceptance.xml"))
            .Should().BeFalse("precondition: the store publishes no documentation file beside the dll");

        AssertDocumented(LoadFromPath(published),
            "the docs travel inside the image, so the content-hashed copy the hub loads still has them");
    }

    [Fact]
    public void AnImageLoadedFromBytes_ReadsItsDocumentation()
    {
        // EmitToMemory: the EnableDiskCache=false path, and the shape a fetched bundle is handed in as.
        var (image, _) = EmitPipeline.EmitToMemory(
            Compilation("DynamicNode_Reinsurance_Acceptance_Mem"), "Reinsurance/Acceptance", CancellationToken.None);

        AssertDocumented(LoadFromBytes(image), "an assembly with no Location reads its embedded documentation");
    }

    [Fact]
    public void TheSidecarFile_StillWins_AndTheReleaseDirectoryStillHasIt()
    {
        var release = Path.Combine(root, "release-sidecar");
        Directory.CreateDirectory(release);
        var artifact = EmitPipeline.EmitCompilationToDirectory(
            Compilation("DynamicNode_Sidecar"), "Sidecar", "Doc/Sidecar", release, CancellationToken.None);

        File.Exists(Path.Combine(release, "DynamicNode_Sidecar.xml"))
            .Should().BeTrue("the release directory keeps its documentation file");
        AssertDocumented(LoadFromPath(artifact.DllPath), "beside its sidecar the assembly reads the file");
    }

    [Fact]
    public void AnImageWithoutEmbeddedDocumentation_DegradesToNoSummary_NeverAFault()
    {
        // Negative control — a bundle baked before this change: dll + pdb, no resource, no sidecar.
        var compilation = Compilation("DynamicNode_Legacy");
        using var dll = new MemoryStream();
        compilation.Emit(dll, options: new EmitOptions()).Success.Should().BeTrue();
        var legacy = Path.Combine(root, "legacy");
        Directory.CreateDirectory(legacy);
        var path = Path.Combine(legacy, "v1-c003e001-000000000000.dll");
        File.WriteAllBytes(path, dll.ToArray());

        var type = LoadFromPath(path).GetType("DocProbe.Acceptance")!;
        new XmlDocsReader().Summary(type).Should().BeEmpty(
            "with no documentation anywhere the summary is absent — this is exactly the state every "
            + "mesh-compiled type was in before the image carried its docs");
    }

    [Fact]
    public void TheEmbeddedResource_IsNamedForTheAssembly_AndNotAddedTwice()
    {
        var compilation = Compilation("DynamicNode_Named");
        var once = EmitPipeline.WithEmbeddedDocumentation(compilation, [], CancellationToken.None);
        once.Count.Should().Be(1);
        EmitPipeline.WithEmbeddedDocumentation(compilation, once, CancellationToken.None).Count
            .Should().Be(1, "a caller that already embedded the docs is not given a duplicate resource");
    }

    public void Dispose()
    {
        foreach (var context in contexts)
            context.Unload();
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
