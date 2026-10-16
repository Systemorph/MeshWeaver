using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using MeshWeaver.NuGet;
using Microsoft.CodeAnalysis.CSharp;

namespace MeshWeaver.Compiler;

/// <summary>
/// 🚨 <b>The package closure a build needs travels WITH the build.</b> A NodeType whose sources
/// declare <c>#r "nuget:…"</c> compiles against a resolved package set, and its emitted assembly
/// then references assemblies (Microsoft.Data.SqlClient, …) that only the package folder holds. The
/// load context finds them through probing directories — and those used to be registered in
/// memory by the EMIT alone, so every other reader of the same bytes (a replica that never compiled
/// the type, the compiling replica after a restart, a CI bake a portal adopts, the start-up
/// registration pass) loaded the build with no way to find its packages and failed
/// <c>GetTypes()</c> with <c>Could not load file or assembly 'Microsoft.Data.SqlClient'</c>
/// (measured on an enterprise client portal, 2026-10-16).
///
/// <para>So the references the compile resolved are stamped into the assembly itself, as
/// <see cref="System.Reflection.AssemblyMetadataAttribute"/>s keyed <see cref="MetadataKey"/> —
/// one per <c>#r "nuget:"</c>, its value written in the directive's own <c>Id, Version</c> shape. The
/// bytes go everywhere a build goes (the assembly store, a blob, a baked bundle, a release folder),
/// so every reader can <see cref="Read"/> them from the file's metadata — a header read, nothing is
/// loaded — and re-resolve them (memoized, from the package folder) before it loads the build.
/// Paths are deliberately NOT recorded: they name the compiling process's package folder, which
/// another replica does not share.</para>
///
/// <para>The stamp is part of the generated TEXT, so the content key (a hash of that text) covers
/// it: a build of the same sources compiled before the stamp existed has a different key and is
/// rebuilt rather than adopted. A source set with no <c>#r "nuget:"</c> is returned unchanged —
/// byte-identical, same key — so the stamp moves nothing for the NodeTypes that declare none.</para>
/// </summary>
internal static class NuGetBuildRecord
{
    /// <summary>The <c>AssemblyMetadataAttribute</c> key one recorded package reference is stored under.</summary>
    internal const string MetadataKey = "MeshWeaver.NuGetPackage";

    /// <summary>
    /// The generated skeleton's own assembly attribute (<c>DynamicMeshNodeAttributeGenerator</c>):
    /// the stamp goes immediately ahead of it, inside the region where C# admits assembly attributes
    /// (after the <c>using</c> block, before any type).
    /// </summary>
    internal const string SkeletonAttributePrefix = "[assembly: MeshWeaver.Graph.Generated.";

    /// <summary>
    /// <paramref name="generatedSource"/> with one <c>[assembly: AssemblyMetadata("MeshWeaver.NuGetPackage", "Id, Version")]</c>
    /// per reference, inserted ahead of the skeleton's assembly attribute. Unchanged when there are
    /// no references. Deterministic: the references keep their declared order.
    /// </summary>
    /// <exception cref="InvalidOperationException">The source carries references but no skeleton
    /// assembly attribute to anchor the stamp — a generator contract break, never a content error.</exception>
    public static string Stamp(string generatedSource, IReadOnlyList<NuGetPackageReference> references)
    {
        if (references.Count == 0)
            return generatedSource;

        var anchor = generatedSource.IndexOf(SkeletonAttributePrefix, StringComparison.Ordinal);
        if (anchor < 0 || (anchor > 0 && generatedSource[anchor - 1] != '\n'))
            throw new InvalidOperationException(
                $"The generated source declares {references.Count} `#r \"nuget:…\"` reference(s) but carries no "
                + $"line starting '{SkeletonAttributePrefix}' to record them ahead of; the skeleton generator's "
                + "output changed shape.");

        var stamp = new StringBuilder();
        foreach (var reference in references)
            stamp.Append("[assembly: System.Reflection.AssemblyMetadata(")
                .Append(SymbolDisplay.FormatLiteral(MetadataKey, quote: true))
                .Append(", ")
                .Append(SymbolDisplay.FormatLiteral(Format(reference), quote: true))
                .Append(")]\n");
        return generatedSource.Insert(anchor, stamp.ToString());
    }

    /// <summary>
    /// The package references recorded in the assembly at <paramref name="assemblyPath"/>, in their
    /// recorded order; empty for a build that recorded none, for a missing file and for an image whose
    /// metadata cannot be read (the load then reports that image's own failure).
    /// </summary>
    public static ImmutableArray<NuGetPackageReference> Read(string? assemblyPath)
    {
        if (string.IsNullOrWhiteSpace(assemblyPath) || !File.Exists(assemblyPath))
            return [];
        try
        {
            using var stream = File.OpenRead(assemblyPath);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata)
                return [];
            var metadata = pe.GetMetadataReader();
            if (!metadata.IsAssembly)
                return [];

            var references = ImmutableArray.CreateBuilder<NuGetPackageReference>();
            foreach (var handle in metadata.GetAssemblyDefinition().GetCustomAttributes())
            {
                var attribute = metadata.GetCustomAttribute(handle);
                if (!IsAssemblyMetadataAttribute(metadata, attribute))
                    continue;
                var value = attribute.DecodeValue(StringArgumentProvider.Instance);
                if (value.FixedArguments is [{ Value: string key }, { Value: string recorded }]
                    && string.Equals(key, MetadataKey, StringComparison.Ordinal)
                    && Parse(recorded) is { } reference)
                    references.Add(reference);
            }
            return references.ToImmutable();
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException
                                       or InvalidOperationException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>The directive's own shape: <c>Id</c>, or <c>Id, Version</c>.</summary>
    internal static string Format(NuGetPackageReference reference)
        => string.IsNullOrEmpty(reference.VersionRange) ? reference.Id : $"{reference.Id}, {reference.VersionRange}";

    /// <summary>The inverse of <see cref="Format"/>; null for an empty value.</summary>
    internal static NuGetPackageReference? Parse(string recorded)
    {
        var comma = recorded.IndexOf(',');
        var id = (comma < 0 ? recorded : recorded[..comma]).Trim();
        if (id.Length == 0)
            return null;
        var version = comma < 0 ? null : recorded[(comma + 1)..].Trim();
        return new NuGetPackageReference(id, string.IsNullOrEmpty(version) ? null : version);
    }

    private static bool IsAssemblyMetadataAttribute(MetadataReader metadata, CustomAttribute attribute)
    {
        EntityHandle owner = attribute.Constructor.Kind switch
        {
            HandleKind.MemberReference => metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent,
            HandleKind.MethodDefinition => metadata.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType(),
            _ => default,
        };
        return owner.Kind switch
        {
            HandleKind.TypeReference => metadata.GetTypeReference((TypeReferenceHandle)owner) is var t
                && metadata.StringComparer.Equals(t.Name, "AssemblyMetadataAttribute")
                && metadata.StringComparer.Equals(t.Namespace, "System.Reflection"),
            _ => false,
        };
    }

    /// <summary>Decodes the two <c>string</c> arguments of <c>AssemblyMetadataAttribute(string, string)</c>;
    /// any other argument type is never asked for, because only that constructor is decoded.</summary>
    private sealed class StringArgumentProvider : ICustomAttributeTypeProvider<object?>
    {
        public static readonly StringArgumentProvider Instance = new();

        public object? GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode;
        public object? GetSystemType() => null;
        public object? GetSZArrayType(object? elementType) => null;
        public object? GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => null;
        public object? GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => null;
        public object? GetTypeFromSerializedName(string name) => null;
        public PrimitiveTypeCode GetUnderlyingEnumType(object? type)
            => throw new BadImageFormatException("AssemblyMetadataAttribute takes no enum argument.");
        public bool IsSystemType(object? type) => false;
    }
}
