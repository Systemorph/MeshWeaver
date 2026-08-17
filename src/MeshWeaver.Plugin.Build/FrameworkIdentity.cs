using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using MeshWeaver.Graph.Configuration;

namespace MeshWeaver.Plugin.Build;

/// <summary>
/// Resolves the framework identity a prebuilt assembly is bound to.
///
/// <para>🚨 <b>The identity is <see cref="FrameworkBuildIdentity"/>, not the package version.</b>
/// The runtime compares <c>NodeTypeDefinition.CompiledFrameworkVersion</c> against
/// <see cref="FrameworkBuildIdentity.FrameworkVersion"/>: for a CI-built framework that is the
/// stamped COMMIT identity (<c>AssemblyMetadata("MeshWeaverFrameworkIdentity", "g&lt;sha&gt;")</c>
/// on <see cref="IdentityAssembly"/> — identical for every CI build of the same commit, #1660
/// WS3); for a manifest-less local build it is the toolchain assembly's <b>MVID</b> — a content
/// identity, chosen over <c>AssemblyInformationalVersion</c> because deriving it from the version
/// string forced a fresh stamp into every build and destroyed incremental compilation.</para>
///
/// <para>So a package recording only <c>3.0.0-rc2</c> records something the runtime never looks at:
/// two builds can share a version string and differ in content, and the identity is what says
/// whether the bytes are ABI-compatible with the running process. Recording it is what lets an
/// installer decide honestly whether a prebuilt assembly may be seeded — and seeding one under the
/// live framework tag when it was built against different content is <b>actively harmful</b>: the
/// store hit suppresses the rebuild that was needed, and the mismatch surfaces as a
/// <c>TypeLoadException</c> inside an ALC at activation, where there is no diagnostic and no
/// overlay.</para>
/// </summary>
public static class FrameworkIdentity
{
    /// <summary>The assembly whose stamp/MVID IS the framework identity — the compile toolchain
    /// assembly the identity is anchored on (moved from MeshWeaver.Graph in #1707).</summary>
    public const string IdentityAssembly = "MeshWeaver.Compiler";

    /// <summary>
    /// Reads an assembly's framework identity without loading it — metadata only, so it cannot
    /// execute a module initializer or pin the file in an ALC. Resolution mirrors
    /// <see cref="FrameworkBuildIdentity.Resolve"/> exactly: the stamped
    /// <c>MeshWeaverFrameworkIdentity</c> metadata attribute when present (CI builds), else the
    /// MVID (local builds).
    /// </summary>
    public static string ReadIdentity(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        var metadata = peReader.GetMetadataReader();
        return FrameworkBuildIdentity.Resolve(
            ReadStampedIdentity(metadata),
            metadata.GetGuid(metadata.GetModuleDefinition().Mvid).ToString("N"));
    }

    /// <summary>
    /// Reads an assembly's MVID without loading it — metadata only. Kept for callers that need the
    /// raw content identity; adoption gates must use <see cref="ReadIdentity"/> so CI-built
    /// frameworks resolve their stamped commit identity.
    /// </summary>
    public static string ReadMvid(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        var metadata = peReader.GetMetadataReader();
        var mvid = metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
        return mvid.ToString("N");
    }

    /// <summary>
    /// The framework identity of the <see cref="IdentityAssembly"/> a unit compiled against, or
    /// null when the restored package cannot be located. Null is reported rather than guessed: an
    /// installer that cannot establish the identity must fall back to compiling, never seed on
    /// faith.
    /// </summary>
    public static string? ResolveFrameworkMvid(string frameworkVersion)
    {
        var packagesRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES")
                           ?? Path.Combine(
                               Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                               ".nuget", "packages");

        var candidate = Path.Combine(
            packagesRoot,
            IdentityAssembly.ToLowerInvariant(),
            frameworkVersion,
            "lib", "net10.0", IdentityAssembly + ".dll");

        return File.Exists(candidate) ? ReadIdentity(candidate) : null;
    }

    /// <summary>
    /// The <c>AssemblyMetadata("MeshWeaverFrameworkIdentity", …)</c> value on the assembly
    /// definition, or null when the assembly carries none (every local build). Decoded by hand
    /// from the custom-attribute blob — the fixed arguments of
    /// <see cref="System.Reflection.AssemblyMetadataAttribute"/> are two serialized strings after
    /// the 0x0001 prolog — because <see cref="MetadataReader"/> has no reflection-free typed
    /// decoder and this must not load the assembly.
    /// </summary>
    private static string? ReadStampedIdentity(MetadataReader metadata)
    {
        foreach (var handle in metadata.GetAssemblyDefinition().GetCustomAttributes())
        {
            var attribute = metadata.GetCustomAttribute(handle);
            if (!IsAssemblyMetadataAttribute(metadata, attribute))
                continue;

            var blob = metadata.GetBlobReader(attribute.Value);
            if (blob.Length < 2 || blob.ReadUInt16() != 0x0001)
                continue;
            var key = blob.ReadSerializedString();
            var value = blob.ReadSerializedString();
            if (string.Equals(key, FrameworkBuildIdentity.MetadataKey, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(value))
                return value;
        }
        return null;
    }

    /// <summary>
    /// Whether a custom attribute's constructor belongs to
    /// <c>System.Reflection.AssemblyMetadataAttribute</c>. The ctor is a
    /// <see cref="MemberReference"/> in ordinary assemblies (the attribute type lives in the
    /// runtime); the <see cref="MethodDefinition"/> shape is handled for completeness.
    /// </summary>
    private static bool IsAssemblyMetadataAttribute(MetadataReader metadata, CustomAttribute attribute)
    {
        string? name = null;
        string? ns = null;
        switch (attribute.Constructor.Kind)
        {
            case HandleKind.MemberReference:
            {
                var member = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
                if (member.Parent.Kind != HandleKind.TypeReference)
                    return false;
                var type = metadata.GetTypeReference((TypeReferenceHandle)member.Parent);
                name = metadata.GetString(type.Name);
                ns = metadata.GetString(type.Namespace);
                break;
            }
            case HandleKind.MethodDefinition:
            {
                var method = metadata.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor);
                var type = metadata.GetTypeDefinition(method.GetDeclaringType());
                name = metadata.GetString(type.Name);
                ns = metadata.GetString(type.Namespace);
                break;
            }
        }
        return string.Equals(name, nameof(System.Reflection.AssemblyMetadataAttribute), StringComparison.Ordinal)
               && string.Equals(ns, "System.Reflection", StringComparison.Ordinal);
    }
}
