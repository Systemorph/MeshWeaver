using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;

namespace MeshWeaver.Mesh;

/// <summary>
/// Whether a module's bytes can be LINKED against the platform this process is running — the
/// measured answer that replaced the declared one (#3538).
/// </summary>
public enum ModuleLinkState
{
    /// <summary>Every type this module references from a platform assembly exists in the copy this
    /// process would bind to. The module may load.</summary>
    Linkable,

    /// <summary>At least one referenced type is ABSENT from the platform copy that would be bound —
    /// or a whole referenced platform assembly is not here at all. Loading it produces a
    /// <see cref="TypeLoadException"/> the first time the code path is reached, which is a RENDER,
    /// hours after the install.</summary>
    Unlinkable,

    /// <summary>🚨 The third state, modelled on purpose: the check could not be MADE. Never folded
    /// into <see cref="Linkable"/> — "I could not determine whether this loads" and "this loads"
    /// are different facts, and a gate that reports the first as the second is a gate that cannot
    /// fail. At landing and at boot every caller treats this exactly as <see cref="Unlinkable"/>
    /// (the thing refused is one module's load, and the previous generation keeps serving). At the
    /// platform ROLL gate (#3651) it is REPORTED and decides nothing — the thing that would be
    /// refused there is the whole platform's update, on a publication that may simply predate the
    /// surface document; the boot-time probe is the safety net.</summary>
    Indeterminate,

    /// <summary>🚨 The <see cref="FileLoadException"/> shape (#4083): the module's bytes reference
    /// an assembly the platform carries — third-party included — at a HIGHER version than the
    /// platform's copy, or under a different public key token. The platform's copy is what the
    /// loader binds (a copy travelling in the module's own bundle never loads when the platform
    /// already carries the simple name), and .NET binds a reference only to an equal or HIGHER
    /// version: the bind itself is refused, whatever the type names look like — both YamlDotNet
    /// majors carry the same types, and 2026-09-11 crash-looped every new pod on exactly that.
    /// A hard verdict everywhere <see cref="Unlinkable"/> is one: refused at landing and at boot,
    /// a hold at the roll gate. Appended, never inserted: the state is on the wire in
    /// serialized verdicts.</summary>
    BindingConflict,
}

/// <summary>
/// What <see cref="ModulePlatformLink"/> measured about one module — the verdict AND its
/// denominator, so a reader can tell "checked 412 type references and found none missing" from
/// "checked nothing".
/// </summary>
/// <param name="State">The verdict.</param>
/// <param name="Module">The module's assembly simple name.</param>
/// <param name="MissingTypes">Types the module references that the platform copy does not have,
/// each as <c>Full.Type.Name (AssemblySimpleName)</c>. Empty unless
/// <see cref="ModuleLinkState.Unlinkable"/>.</param>
/// <param name="CheckedTypeReferences">How many type references were actually resolved against a
/// platform assembly — the DENOMINATOR. A zero here with a
/// <see cref="ModuleLinkState.Linkable"/> verdict means nothing was checked, which the report
/// says out loud.</param>
/// <param name="CheckedAssemblies">The platform assemblies whose surface was read.</param>
/// <param name="UncheckedAssemblies">Referenced assemblies that are neither the platform's nor the
/// module's own closure — its private dependencies. Named, never silently folded into "fine":
/// they are out of this check's denominator, and a missing one fails later as a
/// <c>FileNotFoundException</c>, which is a different defect with a different remedy.</param>
/// <param name="Detail">Why the state is <see cref="ModuleLinkState.Indeterminate"/>, or null.</param>
public sealed record ModuleLinkVerdict(
    ModuleLinkState State,
    string Module,
    ImmutableArray<string> MissingTypes,
    int CheckedTypeReferences,
    ImmutableArray<string> CheckedAssemblies,
    ImmutableArray<string> UncheckedAssemblies,
    string? Detail = null)
{
    /// <summary>True only for <see cref="ModuleLinkState.Linkable"/> — the one state a caller may
    /// load on. Written as an explicit predicate so no call site can spell the check as
    /// <c>!= Unlinkable</c> and quietly admit <see cref="ModuleLinkState.Indeterminate"/> — or,
    /// since #4083, <see cref="ModuleLinkState.BindingConflict"/>.</summary>
    public bool MayLoad => State == ModuleLinkState.Linkable;

    /// <summary>
    /// 🚨 The assembly references that CANNOT bind on this platform (#4083), each as
    /// <c>Name wanted (this platform carries have)</c>: the module asks for a HIGHER version of an
    /// assembly the platform carries, or for a different public key token. Non-empty exactly when
    /// <see cref="State"/> is <see cref="ModuleLinkState.BindingConflict"/>.
    /// </summary>
    public ImmutableArray<string> BindingConflicts { get; init; } = [];

    /// <summary>
    /// Version skew that is REPORTED and never refused (#4083): a reference to a LOWER version than
    /// the platform carries (the loader rolls forward — patch drift is ordinary, and a gate that
    /// reds on it is switched off within the week), and a carried assembly whose version the
    /// surface does not record (a <see cref="ModulePlatformSurface.PublishedFileName"/> written
    /// before versions were published: unknown is unknown, never a hard verdict). Carried on a
    /// <see cref="ModuleLinkState.Linkable"/> verdict too — a reader that wants the drift can read
    /// it; nothing decides on it.
    /// </summary>
    public ImmutableArray<string> Advisories { get; init; } = [];

    /// <summary>How many assembly references were compared by version against the platform's copy
    /// — the version half's DENOMINATOR, beside <see cref="CheckedTypeReferences"/>.</summary>
    public int ComparedAssemblyReferences { get; init; }

    /// <summary>
    /// The operator-facing sentence: which module, what it wants that this build does not have,
    /// and what was actually measured. English by design — this goes to stderr and
    /// <c>/health</c>, which are operator channels; the VIEWER-facing rendering of the same fact
    /// is localized where the catalog renders it.
    /// </summary>
    public string Report() => State switch
    {
        ModuleLinkState.Linkable =>
            $"Module '{Module}' links against this platform ({Denominator()}" + Unchecked() + ")"
            + Advised() + ".",
        ModuleLinkState.Unlinkable =>
            $"Module '{Module}' was built against a platform this deployment is NOT running and "
            + "CANNOT be loaded here: it references "
            + string.Join(", ", MissingTypes)
            + ", which the copy this process would bind to does not have. Loading it anyway "
            + "throws TypeLoadException at the first render that touches it — every render, "
            + "forever, with nothing connecting it to the install. Move the platform and the "
            + "module together: this module becomes loadable when the platform updates. "
            + $"({Denominator()}" + Unchecked() + ")" + Advised(),
        ModuleLinkState.BindingConflict =>
            $"Module '{Module}' was built against assembly VERSIONS this deployment is NOT running "
            + "and CANNOT be loaded here: it references "
            + string.Join(", ", BindingConflicts)
            + ". The platform's copy is what the loader binds — a copy of the same assembly "
            + "travelling in the module's own bundle never loads when the platform carries the "
            + "name — and .NET binds a reference only to an EQUAL or HIGHER version under the same "
            + "public key token, never to a lower one. Loading it anyway throws FileLoadException "
            + "(0x80131040) the first time any code path touches the assembly; on 2026-09-11 that "
            + "was hub construction, and every new pod crash-looped. Move the platform and the "
            + "module together: this module becomes loadable on a platform carrying at least the "
            + "referenced version"
            + (MissingTypes.IsDefaultOrEmpty
                ? ""
                : " — and it also references " + string.Join(", ", MissingTypes)
                  + ", which the platform's copy does not have")
            + $". ({Denominator()}" + Unchecked() + ")" + Advised(),
        _ =>
            $"Module '{Module}': whether it links against this platform could NOT be determined "
            + $"({Detail}). Not loading it: an unanswerable check is not a passed one.",
    };

    private string Denominator() =>
        $"{CheckedTypeReferences} type reference(s) checked across {CheckedAssemblies.Length} "
        + $"platform assembly/assemblies, {ComparedAssemblyReferences} assembly reference(s) "
        + "compared by version";

    private string Unchecked() =>
        UncheckedAssemblies.IsDefaultOrEmpty
            ? string.Empty
            : $"; {UncheckedAssemblies.Length} referenced assembly/assemblies are neither the "
              + "platform's nor this module's own closure and were NOT checked: "
              + string.Join(", ", UncheckedAssemblies);

    private string Advised() =>
        Advisories.IsDefaultOrEmpty
            ? string.Empty
            : $" Advisory ({Advisories.Length}), reported and deciding nothing: "
              + string.Join("; ", Advisories);
}

/// <summary>
/// What a platform assembly IS, as the loader identifies it (#4083): the manifest version and the
/// public key token, read from the assembly's own identity without loading it. Null members are
/// UNKNOWN — a surface document written before versions were published, an unsigned assembly —
/// and unknown compares as an advisory, never as a conflict.
/// </summary>
/// <param name="Version">The assembly's manifest version, or null when not known.</param>
/// <param name="PublicKeyToken">The public key token as 16 lowercase hex characters, or null for
/// an unsigned assembly or one whose token is not known.</param>
public sealed record PlatformAssemblyIdentity(Version? Version, string? PublicKeyToken)
{
    /// <summary>The identity of an <see cref="AssemblyName"/> — a loaded assembly's, a file's
    /// manifest, or a reference's — in this record's spelling.</summary>
    public static PlatformAssemblyIdentity Of(AssemblyName name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var token = name.GetPublicKeyToken();
        return new PlatformAssemblyIdentity(
            name.Version,
            token is { Length: > 0 } ? Convert.ToHexStringLower(token) : null);
    }
}

/// <summary>
/// The type surface of the platform a module would be loaded INTO — one instance per check batch,
/// caching what it reads.
///
/// <para>🚨 An INSTANCE, never a static cache. The surface it describes is a property of THIS
/// process's loaded assemblies and probe directories; a process-wide static would survive mesh
/// disposal and bleed one test's fabricated platform into the next one's.</para>
///
/// <para><b>Three sources, one shape (#3651).</b> A surface is measured on a running process
/// (<see cref="OfRunningProcess"/>), on a set of files (<see cref="OfFiles"/>), or READ BACK from
/// the document a bake published about a platform that is not running here
/// (<see cref="FromJson"/> / <see cref="ToJson"/>). The link check does not know which it was
/// handed; that is what lets the release gate answer "would this module load on the target" with
/// the same code the boot probe runs.</para>
/// </summary>
public sealed class ModulePlatformSurface
{
    /// <summary>
    /// The file name a PUBLISHED surface travels under (#3651): the bake writes it beside
    /// <c>framework-mvid.txt</c>, <c>publish-bake-bundles.sh</c> uploads it beside <c>_complete</c>
    /// for every identity, and the release gate reads it back through <see cref="FromJson"/> to
    /// link a landed module against a platform that is not running anywhere it can reach. One
    /// name, three call sites, so the producer and both consumers cannot drift.
    /// </summary>
    public const string PublishedFileName = "platform-surface.json";

    private readonly ImmutableDictionary<string, string> _files;
    private readonly ImmutableDictionary<string, Assembly> _loaded;
    // 🚨 The DECLARED surface (#3651): assembly → the full type names it exports, read from a
    // platform-surface.json a bake wrote about a platform this process is NOT running. Authoritative
    // for the names it carries — the producer read the same metadata OfRunningProcess reads — and
    // silent about everything else, so a reference to an undeclared assembly is judged by the same
    // carries/platform-prefix rules as against a live surface.
    private readonly ImmutableDictionary<string, ImmutableHashSet<string>> _declared;
    // 🚨 The DECLARED identities (#4083): assembly → version + public key token, read from the
    // document's `identities` section. An assembly the document declares types for but no identity
    // (a document written before #4083) is carried with an UNKNOWN identity, which the probe
    // reports as an advisory and never as a conflict.
    private readonly ImmutableDictionary<string, PlatformAssemblyIdentity> _declaredIdentities;
    // 🚨 Which carried assemblies the loader binds to the PLATFORM's copy even when a module ships
    // its own (#4083): on a running process the trusted platform assemblies and the application
    // directory — a copy loaded from a LANDED module directory is not one of them, since a module
    // re-landing its own siblings must be measured against the siblings it brings, not the
    // generation it supersedes. Null means every carried assembly: a published document describes
    // a platform image's /app alone, and a file set IS the platform host.
    private readonly ImmutableHashSet<string>? _platformBound;
    // Per-instance memo of what each platform assembly exports. ConcurrentDictionary because a
    // caller may check several modules in parallel; an instance field, so its lifetime is the
    // batch's.
    private readonly ConcurrentDictionary<string, ImmutableHashSet<string>?> _types =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PlatformAssemblyIdentity?> _identities =
        new(StringComparer.OrdinalIgnoreCase);

    private ModulePlatformSurface(
        ImmutableDictionary<string, string> files,
        ImmutableDictionary<string, Assembly> loaded,
        ImmutableDictionary<string, ImmutableHashSet<string>>? declared = null,
        string? identity = null,
        ImmutableDictionary<string, PlatformAssemblyIdentity>? declaredIdentities = null,
        ImmutableHashSet<string>? platformBound = null)
    {
        _files = files;
        _loaded = loaded;
        _declared = declared
            ?? ImmutableDictionary<string, ImmutableHashSet<string>>.Empty
                .WithComparers(StringComparer.OrdinalIgnoreCase);
        _declaredIdentities = declaredIdentities
            ?? ImmutableDictionary<string, PlatformAssemblyIdentity>.Empty
                .WithComparers(StringComparer.OrdinalIgnoreCase);
        _platformBound = platformBound;
        Identity = identity;
    }

    /// <summary>
    /// The framework build identity this surface describes, when it was PUBLISHED with one
    /// (<see cref="FromJson"/>); null for a surface read off a running process or a directory,
    /// which knows its bytes but not the identity the platform resolves for them.
    /// </summary>
    public string? Identity { get; }

    /// <summary>True when this surface was read back from a <see cref="PublishedFileName"/>
    /// document rather than measured on a process or a directory — a reader's hint about
    /// provenance; the verdicts are computed identically either way.</summary>
    public bool IsDeclared => !_declared.IsEmpty;

    /// <summary>
    /// The surface of the RUNNING process: every loaded assembly (the copies a module actually
    /// binds to), plus every managed DLL sitting in <paramref name="probeDirectories"/> for the
    /// platform assemblies this process has not touched yet.
    /// </summary>
    /// <param name="probeDirectories">Directories to add — production passes the application base
    /// directory. Missing directories are skipped.</param>
    public static ModulePlatformSurface OfRunningProcess(params string[] probeDirectories)
    {
        // 🚨 What binds AHEAD of a module's own copy on this process (#4083): the trusted platform
        // assemblies (the application's closure plus the shared frameworks — the TPA binder is
        // consulted before any LoadFrom sibling) and whatever sits in the application directory.
        // A copy this process loaded from a LANDED module's directory is deliberately not in the
        // set: it is what a superseded generation brought, not what the next boot binds.
        var platformBound = ImmutableHashSet.CreateBuilder<string>(StringComparer.OrdinalIgnoreCase);
        var tpa = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty;
        foreach (var path in tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            platformBound.Add(Path.GetFileNameWithoutExtension(path));
        var baseDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
        var loadedAssemblies = AppDomain.CurrentDomain.GetAssemblies();
        foreach (var assembly in loadedAssemblies)
        {
            var name = assembly.GetName().Name;
            if (string.IsNullOrEmpty(name) || assembly.IsDynamic || string.IsNullOrEmpty(assembly.Location))
                continue;
            if (IsUnder(assembly.Location, baseDirectory))
                platformBound.Add(name);
        }
        foreach (var directory in probeDirectories ?? [])
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)
                || !string.Equals(
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)), baseDirectory,
                    StringComparison.OrdinalIgnoreCase))
                continue;
            foreach (var file in Directory.EnumerateFiles(directory, "*.dll"))
                platformBound.Add(Path.GetFileNameWithoutExtension(file));
        }
        return Of(loadedAssemblies, platformBound.ToImmutable(), probeDirectories);
    }

    /// <summary>
    /// The pure form: an explicit set of loaded assemblies and probe directories. The seam a test
    /// fabricates an OLDER platform through — which is the only way to reproduce the defect
    /// without two builds of the product. The loaded assemblies handed in are what the loader
    /// binds, so they are the platform-bound set (<see cref="IsPlatformBound"/>); the probe
    /// directories' files are not.
    /// </summary>
    /// <param name="loadedAssemblies">The assemblies a module would bind to, in precedence order;
    /// the first of a simple name wins, exactly as the loader resolves it.</param>
    /// <param name="probeDirectories">Directories searched for assemblies not among the loaded.</param>
    public static ModulePlatformSurface Of(
        IEnumerable<Assembly> loadedAssemblies, params string[] probeDirectories)
    {
        ArgumentNullException.ThrowIfNull(loadedAssemblies);
        var assemblies = loadedAssemblies.ToArray();
        var platformBound = assemblies
            .Select(a => a.GetName().Name)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
        return Of(assemblies, platformBound, probeDirectories);
    }

    private static ModulePlatformSurface Of(
        IEnumerable<Assembly> loadedAssemblies, ImmutableHashSet<string> platformBound,
        string[]? probeDirectories)
    {
        var loaded = ImmutableDictionary.CreateBuilder<string, Assembly>(StringComparer.OrdinalIgnoreCase);
        foreach (var assembly in loadedAssemblies)
        {
            var name = assembly.GetName().Name;
            if (!string.IsNullOrEmpty(name))
                loaded.TryAdd(name, assembly);
        }

        var files = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in probeDirectories ?? [])
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                continue;
            foreach (var file in Directory.EnumerateFiles(directory, "*.dll"))
                files.TryAdd(Path.GetFileNameWithoutExtension(file), file);
        }

        return new ModulePlatformSurface(
            files.ToImmutable(), loaded.ToImmutable(), platformBound: platformBound);
    }

    private static bool IsUnder(string path, string directory)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(path));
        return parent is not null
               && string.Equals(
                   Path.TrimEndingDirectorySeparator(parent), directory,
                   StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The surface of an explicit set of assembly FILES — the shape a bake has when it compiles
    /// against a platform host that is a directory rather than this process (a portal image's
    /// <c>/app</c> plus its shared frameworks, #3022). The first file of a simple name wins, so the
    /// caller orders the paths by binding precedence exactly as it ordered its reference set.
    /// </summary>
    /// <param name="assemblyPaths">Managed assembly files, in precedence order. Paths that do not
    /// exist are skipped.</param>
    public static ModulePlatformSurface OfFiles(IEnumerable<string> assemblyPaths)
    {
        ArgumentNullException.ThrowIfNull(assemblyPaths);
        var files = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in assemblyPaths)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                continue;
            files.TryAdd(Path.GetFileNameWithoutExtension(path), path);
        }
        return new ModulePlatformSurface(
            files.ToImmutable(), ImmutableDictionary<string, Assembly>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 🚨 <b>The surface, SERIALIZED (#3651)</b> — so a release gate can link a landed module
    /// against a platform that is not running anywhere it can reach. A platform roll is held only by
    /// a module that provably cannot load on the target, and "provably" needs the target's type
    /// surface at gate time; the bake job runs inside the target image, so it is the one process
    /// that can write this document, and it writes it beside <c>framework-mvid.txt</c>.
    ///
    /// <para>The shape is minimal and documented in <c>Doc/Architecture/ModulePlatformLinkGate</c>:</para>
    /// <code>
    /// { "identity": "s&lt;hash&gt;",
    ///   "assemblies": { "MeshWeaver.Blazor": ["MeshWeaver.Blazor.BlazorView`2", …], … },
    ///   "identities": { "YamlDotNet": { "version": "16.3.0.0", "publicKeyToken": "ec19458f3c15af5e" }, … } }
    /// </code>
    /// <para>Every assembly this surface carries is listed with the SAME set <see cref="TypesOf"/>
    /// answers — type definitions and exported/forwarded types, full names, nested as
    /// <c>Outer+Inner</c> — so a check against the document reaches the verdict a check against the
    /// live process would. An assembly whose surface cannot be read (no file, unreadable metadata)
    /// is OMITTED rather than written empty: an empty list would read as "this assembly has no
    /// types" and report every reference to it as missing.</para>
    ///
    /// <para>🚨 <b><c>identities</c> (#4083)</b> carries, per listed assembly, the manifest version
    /// and public key token the loader binds by — the two facts type names cannot express (both
    /// YamlDotNet majors carry the same type names; only the version refuses the bind). A SIBLING
    /// of <c>assemblies</c> rather than a richer value inside it, because every reader shipped
    /// before this section requires each <c>assemblies</c> value to be an array of strings and
    /// throws otherwise — and ignores an unknown root property. So an older platform reads the new
    /// document exactly as before, and a newer platform reading an older document sees no
    /// identities and reports every version comparison as unknown (an advisory, never a
    /// verdict).</para>
    /// </summary>
    /// <param name="identity">The framework build identity the described platform resolves, or
    /// null when the writer does not know it.</param>
    public string ToJson(string? identity = null)
    {
        var names = _declared.Keys.Concat(_loaded.Keys).Concat(_files.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString(IdentityProperty, identity ?? Identity);
            writer.WriteStartObject(AssembliesProperty);
            var listed = new List<string>();
            foreach (var name in names)
            {
                var types = TypesOf(name);
                if (types is null)
                    continue;
                listed.Add(name);
                writer.WriteStartArray(name);
                foreach (var type in types.OrderBy(t => t, StringComparer.Ordinal))
                    writer.WriteStringValue(type);
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
            writer.WriteStartObject(IdentitiesProperty);
            foreach (var name in listed)
            {
                if (IdentityOf(name) is not { } assemblyIdentity)
                    continue;
                writer.WriteStartObject(name);
                if (assemblyIdentity.Version is { } version)
                    writer.WriteString(VersionProperty, version.ToString(4));
                if (assemblyIdentity.PublicKeyToken is { } token)
                    writer.WriteString(PublicKeyTokenProperty, token);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// Reads a surface written by <see cref="ToJson"/>. Strict about the shape — a document that
    /// is not the documented one throws <see cref="JsonException"/> rather than yielding a surface
    /// that carries nothing, because a surface that carries nothing refuses every module that
    /// references a platform assembly (the platform-prefix rule) and that is a confidently wrong
    /// verdict, not a missing one. The caller turns the exception into
    /// <see cref="ModuleLinkState.Indeterminate"/>.
    /// </summary>
    /// <param name="json">The document.</param>
    public static ModulePlatformSurface FromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException($"{PublishedFileName}: the document is not an object");
        if (!root.TryGetProperty(AssembliesProperty, out var assemblies)
            || assemblies.ValueKind != JsonValueKind.Object)
            throw new JsonException(
                $"{PublishedFileName}: no '{AssembliesProperty}' object — the document does not "
                + "describe a platform surface");
        string? identity = null;
        if (root.TryGetProperty(IdentityProperty, out var identityElement)
            && identityElement.ValueKind == JsonValueKind.String)
            identity = identityElement.GetString();

        var declared = ImmutableDictionary.CreateBuilder<string, ImmutableHashSet<string>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var assembly in assemblies.EnumerateObject())
        {
            if (assembly.Value.ValueKind != JsonValueKind.Array)
                throw new JsonException(
                    $"{PublishedFileName}: '{assembly.Name}' is not an array of type names");
            var types = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
            foreach (var type in assembly.Value.EnumerateArray())
            {
                if (type.ValueKind != JsonValueKind.String)
                    throw new JsonException(
                        $"{PublishedFileName}: '{assembly.Name}' lists a type name that is not a string");
                types.Add(type.GetString()!);
            }
            declared[assembly.Name] = types.ToImmutable();
        }
        if (declared.Count == 0)
            throw new JsonException(
                $"{PublishedFileName}: '{AssembliesProperty}' is empty — a platform with no "
                + "assemblies is not a surface anything could link against");

        // 🚨 OPTIONAL (#4083): a document written before versions were published has no
        // `identities`, and reads as a surface whose every version is UNKNOWN — advisory, never a
        // verdict. Present, it is held to the documented shape like everything else: a malformed
        // version would otherwise become a silent "unknown" on a document that claims to know.
        var identities = ImmutableDictionary.CreateBuilder<string, PlatformAssemblyIdentity>(
            StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty(IdentitiesProperty, out var identitiesElement))
        {
            if (identitiesElement.ValueKind != JsonValueKind.Object)
                throw new JsonException(
                    $"{PublishedFileName}: '{IdentitiesProperty}' is not an object");
            foreach (var entry in identitiesElement.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object)
                    throw new JsonException(
                        $"{PublishedFileName}: '{IdentitiesProperty}.{entry.Name}' is not an object");
                Version? version = null;
                if (entry.Value.TryGetProperty(VersionProperty, out var versionElement))
                {
                    if (versionElement.ValueKind != JsonValueKind.String
                        || !Version.TryParse(versionElement.GetString(), out version))
                        throw new JsonException(
                            $"{PublishedFileName}: '{IdentitiesProperty}.{entry.Name}.{VersionProperty}' "
                            + "is not a version");
                }
                string? token = null;
                if (entry.Value.TryGetProperty(PublicKeyTokenProperty, out var tokenElement))
                {
                    if (tokenElement.ValueKind != JsonValueKind.String)
                        throw new JsonException(
                            $"{PublishedFileName}: '{IdentitiesProperty}.{entry.Name}.{PublicKeyTokenProperty}' "
                            + "is not a string");
                    token = tokenElement.GetString();
                }
                identities[entry.Name] = new PlatformAssemblyIdentity(version, token);
            }
        }

        return new ModulePlatformSurface(
            ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase),
            ImmutableDictionary<string, Assembly>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase),
            declared.ToImmutable(),
            identity,
            identities.ToImmutable());
    }

    private const string IdentityProperty = "identity";
    private const string AssembliesProperty = "assemblies";
    private const string IdentitiesProperty = "identities";
    private const string VersionProperty = "version";
    private const string PublicKeyTokenProperty = "publicKeyToken";

    /// <summary>Whether this platform carries an assembly of that simple name at all.</summary>
    public bool Carries(string assemblyName) =>
        _loaded.ContainsKey(assemblyName) || _files.ContainsKey(assemblyName)
        || _declared.ContainsKey(assemblyName);

    /// <summary>
    /// 🚨 Whether the PLATFORM's copy of <paramref name="assemblyName"/> is what the loader binds
    /// even when a module ships its own copy (#4083). True for every assembly a published document
    /// or a file set carries (they describe a platform image and nothing else); on a running
    /// process, true for the trusted platform assemblies and the application directory, and false
    /// for a copy this process loaded from a landed module's directory — that copy is a superseded
    /// generation's, and a module re-landing its own siblings is measured against the siblings it
    /// brings. This is the predicate that decides whether a reference into the module's OWN
    /// closure is in the denominator: it is, exactly when the platform's copy wins.
    /// </summary>
    public bool IsPlatformBound(string assemblyName) =>
        Carries(assemblyName) && (_platformBound is null || _platformBound.Contains(assemblyName));

    /// <summary>
    /// The identity — manifest version and public key token — of the copy of
    /// <paramref name="assemblyName"/> this platform binds, read from the loaded assembly's name,
    /// the file's manifest, or the document's <c>identities</c> section; never by loading anything.
    /// Null when the assembly is not carried or its identity is not known (a document written
    /// before #4083, an unreadable file).
    /// </summary>
    public PlatformAssemblyIdentity? IdentityOf(string assemblyName) =>
        _identities.GetOrAdd(assemblyName, ReadIdentity);

    private PlatformAssemblyIdentity? ReadIdentity(string assemblyName)
    {
        if (_declaredIdentities.TryGetValue(assemblyName, out var declared))
            return declared;
        if (!_declared.IsEmpty)
            return null; // a declared surface knows only what its document says

        // The loaded copy is the exact identity bound; its name is metadata, already in memory.
        if (_loaded.TryGetValue(assemblyName, out var assembly))
            return PlatformAssemblyIdentity.Of(assembly.GetName());

        if (_files.GetValueOrDefault(assemblyName) is not { } path)
            return null;
        try
        {
            using var stream = File.OpenRead(path);
            using var peReader = new PEReader(stream);
            if (!peReader.HasMetadata)
                return null;
            var metadata = peReader.GetMetadataReader();
            if (!metadata.IsAssembly)
                return null;
            return PlatformAssemblyIdentity.Of(metadata.GetAssemblyDefinition().GetAssemblyName());
        }
        catch (Exception exception) when (exception is IOException or BadImageFormatException
                                              or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The full type names <paramref name="assemblyName"/> exposes here — type definitions AND
    /// exported/forwarded types, because a forwarded type is present as far as the loader is
    /// concerned and reading only definitions would report a type-forward as a missing type.
    /// Null when the assembly is carried but its surface could not be read.
    /// </summary>
    public ImmutableHashSet<string>? TypesOf(string assemblyName) =>
        _types.GetOrAdd(assemblyName, ReadTypes);

    private ImmutableHashSet<string>? ReadTypes(string assemblyName)
    {
        // A DECLARED surface is what its producer measured; nothing here can read past it.
        if (_declared.TryGetValue(assemblyName, out var declared))
            return declared;

        // Prefer the FILE of the loaded copy: it is the exact bytes bound, and metadata gives both
        // definitions and forwarders without loading a single type.
        var path = _loaded.TryGetValue(assemblyName, out var assembly)
                   && !string.IsNullOrEmpty(assembly.Location)
                   && File.Exists(assembly.Location)
            ? assembly.Location
            : _files.GetValueOrDefault(assemblyName);

        // No file to read (a single-file or dynamically loaded platform). Null, never an EMPTY set:
        // an empty surface would read as "this assembly has no types" and report every reference
        // as missing. The caller asks HasReadableFile first and falls back to reflection.
        if (path is null)
            return null;

        try
        {
            using var stream = File.OpenRead(path);
            using var peReader = new PEReader(stream);
            if (!peReader.HasMetadata)
                return null;
            var metadata = peReader.GetMetadataReader();
            var names = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
            foreach (var handle in metadata.TypeDefinitions)
                names.Add(FullNameOf(metadata, metadata.GetTypeDefinition(handle)));
            foreach (var handle in metadata.ExportedTypes)
                names.Add(FullNameOf(metadata, metadata.GetExportedType(handle)));
            return names.ToImmutable();
        }
        catch (Exception exception) when (exception is IOException or BadImageFormatException
                                              or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Whether the loaded copy of <paramref name="assemblyName"/> has
    /// <paramref name="fullTypeName"/>, asked through reflection — the fallback for a platform
    /// assembly with no readable file.</summary>
    internal bool LoadedCopyHasType(string assemblyName, string fullTypeName) =>
        _loaded.TryGetValue(assemblyName, out var assembly)
        && assembly.GetType(fullTypeName, throwOnError: false, ignoreCase: false) is not null;

    /// <summary>Whether a file-backed surface exists for <paramref name="assemblyName"/> — i.e.
    /// whether <see cref="TypesOf"/> is authoritative rather than the empty fallback.</summary>
    internal bool HasReadableFile(string assemblyName) =>
        _declared.ContainsKey(assemblyName)
        || (_loaded.TryGetValue(assemblyName, out var assembly)
            && !string.IsNullOrEmpty(assembly.Location) && File.Exists(assembly.Location))
        || _files.ContainsKey(assemblyName);

    private static string FullNameOf(MetadataReader metadata, TypeDefinition type)
    {
        var name = metadata.GetString(type.Name);
        var declaring = type.GetDeclaringType();
        if (!declaring.IsNil)
            return FullNameOf(metadata, metadata.GetTypeDefinition(declaring)) + "+" + name;
        var ns = metadata.GetString(type.Namespace);
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }

    private static string FullNameOf(MetadataReader metadata, ExportedType type)
    {
        var name = metadata.GetString(type.Name);
        if (type.Implementation.Kind == HandleKind.ExportedType)
            return FullNameOf(metadata,
                metadata.GetExportedType((ExportedTypeHandle)type.Implementation)) + "+" + name;
        var ns = metadata.GetString(type.Namespace);
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }
}

/// <summary>
/// 🚨 <b>The module lane's platform gate: measured, not declared (#3538).</b>
///
/// <para><b>What went wrong.</b> A module was adopted on the strength of its declared
/// <c>minMeshVersion</c> FLOOR alone — a string its author writes. memex-cloud, running a core of
/// 2026-09-03, therefore adopted <c>DefaultViews</c>' <c>MeshWeaver.Graph.Views</c>, whose bytes
/// were compiled on 09-06 against a <c>MeshWeaver.Mesh.Contract</c> that has
/// <c>CodeOutputCurrency</c> — a type added on 09-04. The floor said <c>3.0.0-rc8</c>; the running
/// <c>3.0.0-rc9.ci.7693</c> satisfied it; the bytes could not possibly load. The module INSTALLED
/// cleanly (its <c>MeshNodeProviderAttribute</c> never touches the missing type), so #2234's
/// install isolation saw nothing, and the defect surfaced as
/// <c>TypeLoadException: Could not load type 'MeshWeaver.Mesh.CodeOutputCurrency'</c> on EVERY
/// render of every code cell, for every user, until someone read a pod log.</para>
///
/// <para><b>Why a version string can never answer this.</b> A floor is a CLAIM about API
/// compatibility. The actual requirement is not a version but a SET OF TYPES: the ones the
/// module's bytes are linked against. Those are recorded in the assembly's own metadata, exactly
/// and without a producer having to say anything, so the requirement can be MEASURED against the
/// platform copies this process would bind to. That is what this class does — metadata only, no
/// <c>Assembly.Load</c>, no module initializer, no type loading, no side effect.</para>
///
/// <para><b>What is in the denominator, and what is deliberately not.</b> A referenced assembly is
/// checked when the PLATFORM carries it. An assembly travelling in the module's OWN closure is not
/// checked — it was built together with the module, and their agreement is not this gate's
/// question — <i>unless the platform carries the same simple name and its copy is what the loader
/// binds</i> (<see cref="ModulePlatformSurface.IsPlatformBound"/>, #4083): then the module's copy
/// never loads, the "built together" premise is false, and the pair that decides is
/// module ↔ platform. An assembly that is in neither is NAMED in the verdict and left unchecked:
/// it is a private dependency whose absence fails as a <c>FileNotFoundException</c>, a different
/// defect with a different remedy. The one exception is an assembly whose simple name is the
/// PLATFORM's own (<see cref="PlatformAssemblyPrefix"/>) that this deployment does not carry at
/// all — that is the whole-assembly shape of the same defect and is refused.</para>
///
/// <para><b>Two measurements, one verdict (#4083).</b> Type references are resolved by NAME
/// against the platform copy's types (the <c>TypeLoadException</c> shape,
/// <see cref="ModuleLinkState.Unlinkable"/>); assembly references are compared by IDENTITY —
/// version and public key token — against the platform copy's (the <c>FileLoadException</c>
/// shape, <see cref="ModuleLinkState.BindingConflict"/>). The second is asymmetric because the
/// loader is: a reference to a lower version than the platform carries rolls forward and is
/// reported as an advisory; a reference to a higher version, or to a different key, is refused
/// by the loader and is a hard verdict here. <c>MeshWeaver.*</c> assemblies keep the type-identity
/// rule and are compared by version like every other carried assembly.</para>
///
/// <para><b>Member-level skew is NOT covered and must not be assumed to be.</b> This checks TYPE
/// references. A method or constructor signature that moved on a type that still exists (#2234's
/// original <c>MissingMethodException</c>) is invisible here; that shape is caught at install by
/// <see cref="IncompatibleModule"/>, and the two are complementary halves rather than one
/// check.</para>
/// </summary>
public static class ModulePlatformLink
{
    /// <summary>The assembly-name prefix that makes an assembly the PLATFORM's rather than a
    /// module's private dependency. A reference to one of these that the deployment does not carry
    /// at all is refused, rather than counted as an unchecked private dependency.</summary>
    public const string PlatformAssemblyPrefix = "MeshWeaver.";

    /// <summary>
    /// Checks a module's entry assembly ON DISK against <paramref name="surface"/>.
    ///
    /// <para>The module's own closure is taken to be the managed DLLs sitting BESIDE it — which is
    /// exactly what a landed generation directory is. A baseline (image) module therefore has the
    /// whole application closure as its "siblings" and checks trivially clean, which is right: it
    /// ships with the platform by construction.</para>
    /// </summary>
    /// <param name="modulePath">Path to the module's entry DLL.</param>
    /// <param name="surface">The platform this module would be loaded into.</param>
    public static ModuleLinkVerdict Check(string modulePath, ModulePlatformSurface surface)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modulePath);
        ArgumentNullException.ThrowIfNull(surface);
        var moduleName = Path.GetFileNameWithoutExtension(modulePath);
        var directory = Path.GetDirectoryName(Path.GetFullPath(modulePath));
        var siblings = ImmutableHashSet.CreateBuilder<string>(StringComparer.OrdinalIgnoreCase);
        if (directory is not null && Directory.Exists(directory))
            foreach (var file in Directory.EnumerateFiles(directory, "*.dll"))
                siblings.Add(Path.GetFileNameWithoutExtension(file));

        try
        {
            using var stream = File.OpenRead(modulePath);
            return Check(stream, moduleName, siblings.ToImmutable(), surface);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or BadImageFormatException)
        {
            return Indeterminate(moduleName,
                $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    /// <summary>
    /// Checks a module's entry assembly held as BYTES — the landing path, where the bundle is in
    /// memory and nothing has touched the disk yet, so a refusal costs no generation directory.
    /// </summary>
    /// <param name="entryBytes">The entry assembly's bytes.</param>
    /// <param name="moduleName">The module's assembly simple name (for the report).</param>
    /// <param name="closure">The simple names of the assemblies travelling WITH the module,
    /// including the entry itself — references to those are out of the denominator, unless the
    /// platform carries the same simple name and its copy is what the loader binds
    /// (<see cref="ModulePlatformSurface.IsPlatformBound"/>, #4083).</param>
    /// <param name="surface">The platform this module would be loaded into.</param>
    public static ModuleLinkVerdict Check(
        byte[] entryBytes, string moduleName, IReadOnlySet<string> closure,
        ModulePlatformSurface surface)
    {
        ArgumentNullException.ThrowIfNull(entryBytes);
        ArgumentNullException.ThrowIfNull(closure);
        ArgumentNullException.ThrowIfNull(surface);
        using var stream = new MemoryStream(entryBytes, writable: false);
        return Check(stream, moduleName, closure, surface);
    }

    private static ModuleLinkVerdict Check(
        Stream module, string moduleName, IReadOnlySet<string> closure,
        ModulePlatformSurface surface)
    {
        List<string> missing = [];
        var checkedRefs = 0;
        var checkedAssemblies = ImmutableHashSet.CreateBuilder<string>(StringComparer.OrdinalIgnoreCase);
        var uncheckedAssemblies = ImmutableHashSet.CreateBuilder<string>(StringComparer.OrdinalIgnoreCase);

        PEReader? peReader = null;
        MetadataReader metadata;
        try
        {
            peReader = new PEReader(module);
            if (!peReader.HasMetadata)
            {
                peReader.Dispose();
                return Indeterminate(moduleName,
                    "the file carries no managed metadata — it is not a managed assembly");
            }
            metadata = peReader.GetMetadataReader();
        }
        catch (Exception exception) when (exception is BadImageFormatException or IOException)
        {
            peReader?.Dispose();
            return Indeterminate(moduleName, $"{exception.GetType().Name}: {exception.Message}");
        }

        List<string> conflicts = [];
        List<string> advisories = [];
        var comparedRefs = 0;

        using (peReader)
        {
            // 🚨 THE VERSION HALF (#4083). Type names cannot express "the reference asks for
            // 18.1.0.0 and this platform has 16.3.0.0": both YamlDotNet majors carry the same
            // type names, and on 2026-09-11 the type walk below said Linkable while every new
            // pod crash-looped on FileLoadException at hub construction. So every assembly
            // reference whose simple name the platform carries is compared by IDENTITY against the
            // platform's copy — the copy the loader binds. The rule is ASYMMETRIC on purpose,
            // because .NET binding rolls FORWARD and never back: a reference to a LOWER version
            // than the platform carries binds to the platform's newer copy (an advisory, so the
            // drift is on record; never a refusal, because patch drift is ordinary and a gate that
            // reds on it is switched off within the week), a reference to a HIGHER version is
            // refused by the loader itself and is therefore a hard verdict here, and a different
            // public key token under the same name is refused whatever the versions say.
            foreach (var handle in metadata.AssemblyReferences)
            {
                var reference = metadata.GetAssemblyReference(handle);
                var assemblyName = metadata.GetString(reference.Name);
                if (!InDenominator(assemblyName, closure, surface))
                    continue;

                var wanted = PlatformAssemblyIdentity.Of(reference.GetAssemblyName());
                var have = surface.IdentityOf(assemblyName);
                comparedRefs++;
                if (have is null)
                {
                    advisories.Add(
                        $"{assemblyName}: the module references {wanted.Version?.ToString(4) ?? "an unversioned copy"}, "
                        + "and this platform's surface records no version for its copy — not "
                        + "compared (a surface published before versions were recorded reads as "
                        + "unknown, never as a conflict)");
                    continue;
                }

                if (wanted.PublicKeyToken is { } wantedToken && have.PublicKeyToken is { } haveToken
                    && !string.Equals(wantedToken, haveToken, StringComparison.OrdinalIgnoreCase))
                {
                    conflicts.Add(
                        $"{assemblyName} with public key token {wantedToken} (this platform carries "
                        + $"public key token {haveToken})");
                    continue;
                }

                // An unversioned reference (0.0.0.0) binds to any version; nothing to compare.
                if (wanted.Version is not { } wantedVersion || wantedVersion == UnversionedReference)
                    continue;
                if (have.Version is not { } haveVersion)
                {
                    advisories.Add(
                        $"{assemblyName}: the module references {wantedVersion.ToString(4)}, and the "
                        + "version of this platform's copy is not known — not compared");
                    continue;
                }
                var order = wantedVersion.CompareTo(haveVersion);
                if (order > 0)
                    conflicts.Add(
                        $"{assemblyName} {wantedVersion.ToString(4)} (this platform carries "
                        + $"{haveVersion.ToString(4)})");
                else if (order < 0)
                    advisories.Add(
                        $"{assemblyName}: the module references {wantedVersion.ToString(4)}, this "
                        + $"platform carries {haveVersion.ToString(4)} — binds and rolls forward");
            }

            foreach (var handle in metadata.TypeReferences)
            {
                var reference = metadata.GetTypeReference(handle);
                if (ResolveScope(metadata, reference) is not { } assemblyName)
                    continue; // same-assembly or module-scoped reference — nothing external to check

                // The module's own closure: built together with the entry, so their agreement is
                // not this gate's question — UNLESS the platform carries the same simple name and
                // its copy is what the loader binds (#4083), in which case the module's copy never
                // loads and the pair that decides is module ↔ platform.
                if (closure.Contains(assemblyName) && !surface.IsPlatformBound(assemblyName))
                    continue;

                if (!surface.Carries(assemblyName))
                {
                    if (assemblyName.StartsWith(PlatformAssemblyPrefix, StringComparison.Ordinal))
                    {
                        // A whole PLATFORM assembly this deployment does not have — the
                        // coarse-grained shape of the same defect, and a certain load failure.
                        missing.Add($"{FullName(metadata, reference)} ({assemblyName} — this "
                                    + "deployment carries no such platform assembly)");
                        checkedAssemblies.Add(assemblyName);
                        checkedRefs++;
                        continue;
                    }

                    uncheckedAssemblies.Add(assemblyName);
                    continue;
                }

                var fullName = FullName(metadata, reference);
                checkedAssemblies.Add(assemblyName);
                checkedRefs++;

                if (surface.HasReadableFile(assemblyName))
                {
                    var types = surface.TypesOf(assemblyName);
                    if (types is null)
                        return Indeterminate(moduleName,
                            $"the platform's copy of '{assemblyName}' could not be read, so "
                            + "whether it carries the types this module links against is unknown");
                    if (!types.Contains(fullName))
                        missing.Add($"{fullName} ({assemblyName})");
                }
                else if (!surface.LoadedCopyHasType(assemblyName, fullName))
                {
                    missing.Add($"{fullName} ({assemblyName})");
                }
            }
        }

        // A binding conflict is the loader's FIRST refusal — the assembly never binds, so no type
        // in it is ever looked up — which is why it outranks a missing type in the verdict.
        var state = conflicts.Count > 0 ? ModuleLinkState.BindingConflict
            : missing.Count > 0 ? ModuleLinkState.Unlinkable
            : ModuleLinkState.Linkable;
        return new ModuleLinkVerdict(
            state,
            moduleName,
            [.. missing.Distinct(StringComparer.Ordinal).OrderBy(m => m, StringComparer.Ordinal)],
            checkedRefs,
            [.. checkedAssemblies.ToImmutable().OrderBy(a => a, StringComparer.OrdinalIgnoreCase)],
            [.. uncheckedAssemblies.ToImmutable().OrderBy(a => a, StringComparer.OrdinalIgnoreCase)])
        {
            BindingConflicts = [.. conflicts.Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal)],
            Advisories = [.. advisories.Distinct(StringComparer.Ordinal).OrderBy(a => a, StringComparer.Ordinal)],
            ComparedAssemblyReferences = comparedRefs,
        };
    }

    /// <summary>The version an assembly reference carries when the compiler was given no version
    /// at all — it binds to any copy, so there is nothing to compare.</summary>
    private static readonly Version UnversionedReference = new(0, 0, 0, 0);

    /// <summary>
    /// Whether a reference to <paramref name="assemblyName"/> is measured against the platform:
    /// the platform carries it, and either the module does not ship its own copy or the platform's
    /// copy is what the loader binds anyway (<see cref="ModulePlatformSurface.IsPlatformBound"/>).
    /// </summary>
    private static bool InDenominator(
        string assemblyName, IReadOnlySet<string> closure, ModulePlatformSurface surface) =>
        surface.Carries(assemblyName)
        && (!closure.Contains(assemblyName) || surface.IsPlatformBound(assemblyName));

    private static ModuleLinkVerdict Indeterminate(string moduleName, string detail) =>
        new(ModuleLinkState.Indeterminate, moduleName, [], 0, [], [], detail);

    /// <summary>
    /// The simple name of the ASSEMBLY a type reference resolves to, or null when the reference is
    /// not to another assembly. A NESTED type's scope is its declaring TypeReference, so the walk
    /// climbs until it reaches an assembly reference.
    /// </summary>
    private static string? ResolveScope(MetadataReader metadata, TypeReference reference)
    {
        var scope = reference.ResolutionScope;
        while (scope.Kind == HandleKind.TypeReference)
            scope = metadata.GetTypeReference((TypeReferenceHandle)scope).ResolutionScope;
        return scope.Kind == HandleKind.AssemblyReference
            ? metadata.GetString(
                metadata.GetAssemblyReference((AssemblyReferenceHandle)scope).Name)
            : null;
    }

    /// <summary>The reference's full name in the shape a type definition carries it —
    /// <c>Namespace.Type</c>, nested as <c>Namespace.Outer+Inner</c>.</summary>
    private static string FullName(MetadataReader metadata, TypeReference reference)
    {
        var name = metadata.GetString(reference.Name);
        if (reference.ResolutionScope.Kind == HandleKind.TypeReference)
            return FullName(metadata,
                metadata.GetTypeReference((TypeReferenceHandle)reference.ResolutionScope))
                + "+" + name;
        var ns = metadata.GetString(reference.Namespace);
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }
}
