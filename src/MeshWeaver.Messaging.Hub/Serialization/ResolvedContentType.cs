using System.Reflection;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// 🚨 <b>WHICH ASSEMBLY a NodeType's content type actually resolved to, in THIS process</b> (#4158,
/// ask 2) — a property of the BYTES that are loaded here, never of the record that describes them.
///
/// <para>Every other per-NodeType coordinate is a claim written by whoever last built the type:
/// <c>NodeTypeDefinition.LatestAssemblyPath</c> is "an ADDRESS, not an identity",
/// <c>LatestAssemblyMvid</c> is the identity of the bytes A BUILD produced, and
/// <c>[ModuleLoad]</c> names the assembly a module LOADED. None of them is the statement
/// <c>/schema/&lt;Type&gt;</c> and the serializer actually act on, which is: the CLR type
/// <see cref="IMeshContentTypeRegistry.TryResolveByNodeType"/> hands back right now. When two
/// builds of one assembly NAME are in the process (#3732 — a module bundle on the shelf and a
/// runtime-compiled collectible build of the same type), those two statements diverge, and until
/// this record existed nothing on the consumer side could tell them apart: a stale adopted build
/// and a stale registry shelf both read as "the newest generation, newest written, and old types".
/// </para>
///
/// <para><b>Read from the loaded assembly, so it cannot be stale by construction.</b>
/// <see cref="Mvid"/> is <c>Assembly.ManifestModule.ModuleVersionId</c> — minted by the compiler
/// into the bytes themselves — and <see cref="Assembly"/> is <c>Assembly.Location</c>, empty for an
/// assembly loaded from a byte array (every runtime-compiled NodeType), which is why
/// <see cref="Collectible"/> is carried beside it: a collectible assembly with no location IS the
/// in-process compile, and a non-collectible one at a path IS the shipped module.</para>
///
/// <para>🚨 <b>Absence is PRINTED, never inferred.</b> <see cref="Status"/> distinguishes
/// <see cref="Unresolved"/> (asked, and no type is registered for this NodeType here — the state
/// that renders every view of it empty) from <see cref="NotAsked"/> (this process has no content-type
/// registry at all, so nothing was measured). Reading a missing block as "fine" is the reading this
/// record exists to prevent.</para>
/// </summary>
/// <param name="Status">
/// <see cref="Resolved"/>, <see cref="Unresolved"/> or <see cref="NotAsked"/>.
/// </param>
/// <param name="TypeName">The resolved type's <c>AssemblyQualifiedName</c>-free full name, or <c>null</c>.</param>
/// <param name="Assembly">The resolved type's assembly <c>Location</c>, or <c>null</c> when it has
/// none (loaded from bytes) or nothing resolved.</param>
/// <param name="Mvid">The resolved assembly's module version id, or <c>null</c>.</param>
/// <param name="Collectible">Whether the resolved assembly is collectible — i.e. a runtime compile
/// in a <c>NodeAssemblyLoadContext</c> rather than a module shipped with the process.</param>
public sealed record ResolvedContentType(
    string Status,
    string? TypeName,
    string? Assembly,
    string? Mvid,
    bool Collectible)
{
    /// <summary><see cref="Status"/> when a CLR type is registered for the NodeType here.</summary>
    public const string Resolved = "resolved";

    /// <summary><see cref="Status"/> when the registry was asked and answered no — the state in
    /// which the node's content stays an untyped element and its views render empty.</summary>
    public const string Unresolved = "unresolved";

    /// <summary><see cref="Status"/> when there is no content-type registry in this process, so
    /// nothing was measured. NOT the same statement as <see cref="Unresolved"/>.</summary>
    public const string NotAsked = "not-asked";

    /// <summary>The <see cref="NotAsked"/> reading — no registry, so no measurement.</summary>
    public static readonly ResolvedContentType NoRegistry =
        new(NotAsked, null, null, null, Collectible: false);

    /// <summary>
    /// Asks <paramref name="registry"/> which CLR type <paramref name="nodeTypePath"/>'s content
    /// resolves to here, and describes the assembly that type came from. Pure given the registry:
    /// two map lookups and reflection over already-loaded metadata, so it is safe on any thread and
    /// costs nothing a health probe or a diagnostics call would notice.
    /// </summary>
    /// <param name="registry">The mesh-wide content-type registry, or <c>null</c>.</param>
    /// <param name="nodeTypePath">The NodeType path to ask under.</param>
    /// <returns>The reading. Never <c>null</c>.</returns>
    public static ResolvedContentType Of(IMeshContentTypeRegistry? registry, string? nodeTypePath)
    {
        if (registry is null)
            return NoRegistry;
        if (string.IsNullOrEmpty(nodeTypePath)
            || !registry.TryResolveByNodeType(nodeTypePath, out var contentType))
            return new ResolvedContentType(Unresolved, null, null, null, Collectible: false);
        return Describe(contentType);
    }

    /// <summary>
    /// The reading for a CLR type already in hand — the same description
    /// <see cref="Of(IMeshContentTypeRegistry?, string?)"/> produces, for callers that resolved the
    /// type themselves (the schema endpoint has it at the point it serialises).
    /// </summary>
    /// <param name="contentType">The resolved content type.</param>
    /// <returns>The reading. Never <c>null</c>.</returns>
    public static ResolvedContentType Describe(Type contentType)
    {
        ArgumentNullException.ThrowIfNull(contentType);
        var assembly = contentType.Assembly;
        // 🚨 Location is the EMPTY STRING — not null — for an assembly loaded from a byte array,
        // which is every runtime-compiled NodeType. Normalising it to null here is what keeps
        // "compiled in this process" from being reported as an assembly at path "".
        var location = string.IsNullOrEmpty(assembly.Location) ? null : assembly.Location;
        return new ResolvedContentType(
            Resolved,
            contentType.FullName,
            location,
            MvidOf(assembly),
            assembly.IsCollectible);
    }

    /// <summary>
    /// The assembly's module version id as the same short 8-hex form <c>[ModuleLoad]</c> prints, or
    /// <c>null</c> when the runtime refuses it (a reflection-only or otherwise metadata-less load).
    /// </summary>
    private static string? MvidOf(Assembly assembly)
    {
        try
        {
            return assembly.ManifestModule.ModuleVersionId.ToString("N")[..8];
        }
        catch (Exception)
        {
            // A manifest module that cannot be read is a "we did not measure it" answer, and saying
            // so beats inventing one — the same rule Status encodes above.
            return null;
        }
    }

    /// <summary>
    /// The one sentence a human reads. Says which of the three states this is, and — when resolved
    /// — names the assembly in the form that distinguishes a shipped module from an in-process
    /// compile.
    /// </summary>
    /// <returns>The sentence.</returns>
    public string Describe() => Status switch
    {
        Resolved => $"{TypeName} from "
                    + (Assembly ?? "an assembly loaded from bytes (no file)")
                    + $" (mvid {Mvid ?? "unreadable"}"
                    + (Collectible ? ", collectible — compiled in this process)" : ")"),
        Unresolved => "no CLR type is registered for this NodeType in this process — its content "
                      + "stays an untyped element and its views render empty",
        _ => "not measured: this process has no content-type registry",
    };
}
