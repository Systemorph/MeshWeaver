using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using MeshWeaver.Compiler;
using Microsoft.CodeAnalysis;

namespace MeshWeaver.PluginTester;

/// <summary>
/// 🚨 <b>The TARGET host's compile surfaces, read from the TARGET host's own
/// <c>MeshWeaver.Compiler.dll</c>.</b> A bake against another host (<c>--app</c>,
/// <see cref="BakeHost.ResolveDirectory"/>) must compile what that host's portals compile, and an
/// older host of the same compatibility key may still ship a package whose IMPLEMENTATION is wider
/// than its reference assembly. System.Interactive is the measured case: its <c>lib/</c> keeps
/// <c>EnumerableEx.MaxBy</c>/<c>MinBy</c> in namespace <c>System.Linq</c>, so every NodeType that
/// calls the BCL's <c>Enumerable.MaxBy</c> hits CS0121 against the bare file. Hosts built while the
/// platform shipped that package embedded the package's <c>ref/</c> assembly in their
/// <c>MeshWeaver.Compiler.dll</c> under <see cref="ResourcePrefix"/>, and their runtime compile used
/// it in place of the same-identity host file.
///
/// <para>The platform no longer ships the package, so this toolchain embeds no surface of its
/// own. Reading them from the TARGET host keeps a bake against a still-running older image
/// compiling exactly as that image compiles. Reading THIS process's resources would make the answer
/// depend on which tester build happened to run. A host that embeds none, which is every host
/// built after the package left, gets its own files unchanged.</para>
/// </summary>
public sealed class HostCompileSurfaces
{
    /// <summary>The manifest-resource name prefix a host's <c>MeshWeaver.Compiler.dll</c> embeds each
    /// compile surface under, followed by the assembly's file name.</summary>
    public const string ResourcePrefix = "MeshWeaver.Compiler.CompileSurface.";

    private readonly ImmutableDictionary<string, byte[]> imagesByFileName;

    private HostCompileSurfaces(ImmutableDictionary<string, byte[]> imagesByFileName) =>
        this.imagesByFileName = imagesByFileName;

    /// <summary>No substitutions: every host file is referenced as it is.</summary>
    public static HostCompileSurfaces None { get; } =
        new(ImmutableDictionary<string, byte[]>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase));

    /// <summary>The file names (e.g. <c>System.Interactive.dll</c>) the host carries a surface for.</summary>
    public IReadOnlyCollection<string> FileNames => imagesByFileName.Keys.ToImmutableArray();

    /// <summary>The surfaces the host whose application directory is <paramref name="appDirectory"/>
    /// embeds in its <c>MeshWeaver.Compiler.dll</c>.</summary>
    public static HostCompileSurfaces OfHost(string appDirectory) =>
        ReadFrom(Path.Combine(appDirectory, FrameworkBuildIdentity.AnchorAssemblyName + ".dll"));

    /// <summary>The surfaces embedded in the assembly at <paramref name="anchorAssemblyPath"/>, read as
    /// metadata and never loaded. A missing file embeds none; an unreadable one THROWS
    /// (<see cref="IOException"/>, <see cref="BadImageFormatException"/>), because quietly answering
    /// "none" would compile against the bare host files and bring the CS0121 back with nothing said.</summary>
    public static HostCompileSurfaces ReadFrom(string anchorAssemblyPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(anchorAssemblyPath);
        if (!File.Exists(anchorAssemblyPath))
            return None;
        using var stream = File.OpenRead(anchorAssemblyPath);
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata || pe.PEHeaders.CorHeader is not { } cor)
            throw new BadImageFormatException($"'{anchorAssemblyPath}' carries no CLI metadata.", anchorAssemblyPath);
        var metadata = pe.GetMetadataReader();
        var images = None.imagesByFileName;
        foreach (var handle in metadata.ManifestResources)
        {
            var resource = metadata.GetManifestResource(handle);
            if (!resource.Implementation.IsNil)
                continue;
            var name = metadata.GetString(resource.Name);
            if (!name.StartsWith(ResourcePrefix, StringComparison.Ordinal))
                continue;
            // An embedded resource is a 4-byte length followed by its bytes, at its offset into the
            // CLI resources directory.
            var section = pe.GetSectionData(cor.ResourcesDirectory.RelativeVirtualAddress);
            var offset = checked((int)resource.Offset);
            if (offset < 0 || offset + sizeof(int) > section.Length)
                throw new BadImageFormatException($"resource '{name}' in '{anchorAssemblyPath}' lies outside its resources directory.", anchorAssemblyPath);
            var reader = section.GetReader(offset, section.Length - offset);
            var length = reader.ReadInt32();
            if (length < 0 || length > reader.RemainingBytes)
                throw new BadImageFormatException($"resource '{name}' in '{anchorAssemblyPath}' declares {length} bytes beyond its resources directory.", anchorAssemblyPath);
            images = images.SetItem(name[ResourcePrefix.Length..], reader.ReadBytes(length));
        }
        return images.IsEmpty ? None : new HostCompileSurfaces(images);
    }

    /// <summary>
    /// The metadata reference a bake against this host uses for the host assembly at
    /// <paramref name="hostPath"/>. That is the file itself, unless the host carries a surface for the
    /// same identity (simple name, version, public key token): then the surface, materialised as a
    /// content-addressed file holding exactly those bytes, so every reader of
    /// <see cref="PortableExecutableReference.FilePath"/> sees the image Roslyn compiled against. A
    /// host file of another build, or a surface that cannot be written, gets the host file.
    /// </summary>
    /// <param name="hostPath">An assembly path from the host's reference set.</param>
    public MetadataReference ReferenceFor(string hostPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(hostPath);
        var fileName = Path.GetFileName(hostPath);
        return imagesByFileName.TryGetValue(fileName, out var image)
               && SameIdentity(image, hostPath)
               && Materialize(image, fileName) is { } surfacePath
            ? MetadataReference.CreateFromFile(surfacePath)
            : MetadataReference.CreateFromFile(hostPath);
    }

    private static bool SameIdentity(byte[] surfaceImage, string hostPath)
    {
        try
        {
            using var pe = new PEReader(ImmutableArray.Create(surfaceImage));
            var surface = pe.GetMetadataReader().GetAssemblyDefinition().GetAssemblyName();
            var host = System.Reflection.AssemblyName.GetAssemblyName(hostPath);
            return string.Equals(surface.Name, host.Name, StringComparison.OrdinalIgnoreCase)
                && surface.Version == host.Version
                && (surface.GetPublicKeyToken() ?? []).AsSpan().SequenceEqual(host.GetPublicKeyToken() ?? []);
        }
        catch (Exception e) when (e is IOException or BadImageFormatException or UnauthorizedAccessException
                                      or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Writes <paramref name="image"/> to <c>{temp}/meshweaver-compile-surfaces/{sha256}/{fileName}</c>
    /// once (content-addressed, published by a no-replace rename) and returns the path; <c>null</c> when
    /// it cannot be written.</summary>
    private static string? Materialize(byte[] image, string fileName)
    {
        try
        {
            var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(image));
            var directory = Path.Combine(Path.GetTempPath(), "meshweaver-compile-surfaces", hash);
            var target = Path.Combine(directory, fileName);
            if (File.Exists(target) && new FileInfo(target).Length == image.Length)
                return target;
            Directory.CreateDirectory(directory);
            var temporary = Path.Combine(directory, fileName + "." + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllBytes(temporary, image);
            if (!MeshWeaver.Utils.NoReplaceMove.TryMove(temporary, target))
                File.Delete(temporary);
            return File.Exists(target) && new FileInfo(target).Length == image.Length ? target : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
