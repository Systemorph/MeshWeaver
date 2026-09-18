using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace MeshWeaver.Mesh;

/// <summary>
/// One boot-installed module assembly (a <c>Modules:Assemblies</c> entry loaded by
/// <see cref="MeshBuilder.InstallAssemblies"/>), registered as an enumerable DI singleton — the
/// mesh-scoped record of "which modules does THIS deployment run" for surfaces that must treat
/// modules like platform (design #1644):
/// <list type="bullet">
/// <item>the in-mesh compile reference set — a module published into <c>modules/&lt;name&gt;/</c>
/// leaves <c>TRUSTED_PLATFORM_ASSEMBLIES</c>, so <c>MeshNodeCompilationService</c> composes its
/// references as the static TPA baseline PLUS these;</item>
/// <item>the bake fingerprint — the sorted installed-module MVID hash joins the usable-build
/// check, so a module upgrade invalidates baked node-type builds that could reference it.</item>
/// </list>
/// </summary>
/// <param name="Assembly">The loaded module assembly (Default ALC, file-backed).</param>
public sealed record InstalledModuleAssembly(Assembly Assembly)
{
    /// <summary>The assembly's MVID — the per-build identity the bake fingerprint hashes.</summary>
    public Guid Mvid => Assembly.ManifestModule.ModuleVersionId;

    /// <summary>
    /// 🚨 THE ONE READER of a module build's ORDERED version (#3934): its
    /// <see cref="AssemblyInformationalVersionAttribute"/> with the <c>+gitSha</c> build metadata
    /// stripped, or null when the assembly carries no stamp.
    ///
    /// <para><b>What it is for.</b> A compiled NodeType's dependency record states a FLOOR over
    /// this value — <c>min:&lt;version&gt;</c> — instead of the module's MVID, which moves on
    /// every compilation by construction and therefore declined every prebuilt bundle whenever a
    /// module was rebuilt anywhere (<c>Doc/Architecture/DependencyRecordFloor</c>). Producer and
    /// consumer must derive it identically or a floor is compared against a value nobody else
    /// computes, so both the portal's resolver and the bake host's go through this property.</para>
    ///
    /// <para>🚨 <b>Null is not a version, and never reads as one.</b> An unstamped module resolves
    /// its MVID instead, i.e. the pre-#3934 exact pin — inconclusive stays on the rebuild side.
    /// The metadata is stripped for the same reason <c>ModulePlatformFloor.RunningVersion</c>
    /// strips it: SemVer ignores it for ordering, and dropping it keeps a decline sentence
    /// readable.</para>
    /// </summary>
    public string? Version => VersionOf(Assembly);

    /// <summary>The pure half, so a producer holding a bare <see cref="Assembly"/> resolves the
    /// identical value.</summary>
    /// <param name="assembly">The module assembly.</param>
    public static string? VersionOf(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var version = InformationalVersionOf(assembly);
        if (string.IsNullOrWhiteSpace(version))
            return null;
        var plus = version.IndexOf('+');
        return plus < 0 ? version : version[..plus];
    }

    /// <summary>
    /// 🚨 The stamp read out of the assembly's OWN METADATA, never through
    /// <c>GetCustomAttribute&lt;T&gt;</c> (MeshWeaver.Plugins#2116).
    ///
    /// <para><b>Why the reflection call could not stay.</b> Reading ONE attribute through
    /// reflection pays for ALL of them: <c>Attribute.GetCustomAttributes(Assembly, Type)</c>
    /// resolves the declaring TYPE of every assembly-level attribute record in order to test it
    /// against the filter. So an assembly carrying an unrelated assembly-level attribute whose
    /// type lives in an assembly this process cannot bind throws
    /// <see cref="FileNotFoundException"/> from inside
    /// <c>System.Reflection.CustomAttribute.FilterCustomAttributeRecord</c> — while the value
    /// being asked for, an <see cref="AssemblyInformationalVersionAttribute"/> from corelib, sits
    /// in the metadata untouched. Measured 2026-09-18: <c>mw-plugin-test compile … --module
    /// Azure.Core.dll</c> (its <c>System.ClientModel</c> reference absent from the tester) died
    /// <c>FATAL</c> with a nine-frame reflection stack that named neither the module nor the
    /// missing assembly, and the same read runs in the portal
    /// (<c>NodeTypeCompilationHelpers.ModuleVersionsOf</c>) over every installed module.</para>
    ///
    /// <para>The metadata read resolves no type at all, so an incomplete attribute closure is
    /// simply not this property's business. It answers exactly what the reflection call answered
    /// wherever the reflection call could answer: the same attribute, the same string, decoded
    /// from the same bytes. An assembly with no <see cref="Assembly.Location"/> — loaded from
    /// bytes, or inside a single-file bundle — has no file to read and keeps the reflection path;
    /// a module is file-backed by construction (see the <c>Assembly</c> parameter), so the
    /// hazardous path is unreachable for one.</para>
    ///
    /// <para>The cost is one PE open per call rather than a cached reflection lookup. Deliberately
    /// NOT memoised on the record: record equality is over instance fields, so a cache field would
    /// make two <c>InstalledModuleAssembly</c> values over one assembly compare unequal.</para>
    /// </summary>
    private static string? InformationalVersionOf(Assembly assembly)
    {
        var location = assembly.Location;
        if (location.Length == 0 || !File.Exists(location))
            return assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        using var stream = File.OpenRead(location);
        using var peReader = new PEReader(stream);
        if (!peReader.HasMetadata)
            return null;
        var metadata = peReader.GetMetadataReader();
        foreach (var handle in metadata.GetAssemblyDefinition().GetCustomAttributes())
        {
            var attribute = metadata.GetCustomAttribute(handle);
            // The attribute type is in corelib, so its constructor is always a MemberReference
            // into a TypeReference — an attribute DEFINED in this assembly cannot be it.
            if (attribute.Constructor.Kind != HandleKind.MemberReference)
                continue;
            var member = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
            if (member.Parent.Kind != HandleKind.TypeReference)
                continue;
            var type = metadata.GetTypeReference((TypeReferenceHandle)member.Parent);
            if (!metadata.StringComparer.Equals(type.Name, nameof(AssemblyInformationalVersionAttribute))
                || !metadata.StringComparer.Equals(type.Namespace, InformationalVersionNamespace))
                continue;
            // The single-string constructor's blob: the 0x0001 prolog, then a SerString.
            var blob = metadata.GetBlobReader(attribute.Value);
            if (blob.RemainingBytes < sizeof(ushort) || blob.ReadUInt16() != CustomAttributeProlog)
                continue;
            return blob.ReadSerializedString();
        }
        return null;
    }

    /// <summary>The namespace of <see cref="AssemblyInformationalVersionAttribute"/>, spelled out
    /// because the metadata read compares names and never resolves the type.</summary>
    private const string InformationalVersionNamespace = "System.Reflection";

    /// <summary>The two-byte prolog every custom-attribute value blob opens with (ECMA-335 II.23.3).</summary>
    private const ushort CustomAttributeProlog = 0x0001;
}
