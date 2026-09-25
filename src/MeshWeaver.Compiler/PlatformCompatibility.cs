using System.Globalization;
using System.Reflection;
using System.Reflection.Metadata;
using MeshWeaver.Plugin.Packaging;

namespace MeshWeaver.Compiler;

/// <summary>
/// 🚨 <b>THE compatibility rule between compiled bytes and the platform that loads them</b> —
/// policy <c>platform-backwards-compatibility</c> (<c>Doc/Architecture/PolicyNotProse</c>).
///
/// <para><b>The ladder.</b> Platform builds are backwards compatible within a MAJOR and a declared
/// COMPATIBILITY EPOCH: <c>platform1+plugin1 → platform2+plugin1 → platform2+plugin2 →
/// platform3+plugin2 …</c>. A platform roll keeps the old plugin bytes — no rebuild, no re-seal —
/// and a plugin rolls independently against the platform that is RUNNING. Only a declared epoch
/// bump (or a major bump) breaks the ladder, and it breaks it on purpose.</para>
///
/// <para><b>So compiled bytes are keyed on a COMPATIBILITY KEY</b>, <see cref="KeyOf"/>:
/// <c>c&lt;major:D3&gt;e&lt;epoch:D3&gt;</c> (e.g. <c>c003e001</c>). <c>major</c> is the platform's
/// <c>AssemblyVersion</c> major (derived from <c>$(PlatformVersion)</c> in
/// <c>Directory.Build.props</c>) and <c>epoch</c> is <c>$(PlatformCompatibilityEpoch)</c>, stamped
/// into every assembly as <c>AssemblyMetadata("<see cref="EpochMetadataKey"/>")</c>. Producer and
/// consumer both read it off <c>MeshWeaver.Compiler.dll</c> — the same file, the same attribute —
/// so they cannot compute it differently, and no manifest is needed. The fixed width is load-bearing:
/// the assembly store's file-name tag is the key's first eight characters
/// (<c>AssemblyCacheFileName.FrameworkTagLength</c>), so the WHOLE key is the tag.</para>
///
/// <para><b>What moved to PROVENANCE.</b> The API-surface/MVID identity
/// (<see cref="FrameworkBuildIdentity.BuildProvenance"/>) changed on essentially every platform
/// build, so every new image missed the whole compiled cache and a per-identity re-seal sat on the
/// roll's critical path. It is still resolved, logged and recorded — it answers "which build made
/// these bytes" — but it never decides a cache miss, a decline or a rebuild.</para>
///
/// <para><b>The FLOOR.</b> Bytes produced by a NEWER platform build than the one running may bind
/// against surface this process does not have, so they are declined LOUDLY — named in the log and
/// on <c>/health</c> — never adopted. The producer therefore records its own platform build
/// (<c>producerPlatformVersion</c> on a bundle, <c>CompiledPlatformVersion</c> on a NodeType
/// record); an ABSENT producer version is "unknown producer = older" and is accepted, so every
/// record written before this field existed stays adoptable.</para>
///
/// <para>🚨 <b>When to bump the epoch.</b> A change that must invalidate compiled bytes — a public
/// API removal or signature change a compiled NodeType/module can bind, a change to the skeleton
/// generator (<c>DynamicMeshNodeAttributeGenerator</c>) or to any compile INPUT the toolchain
/// generates (source-query resolution, include rebasing, parse/compilation options) whose old output
/// is no longer loadable or correct — bumps <c>$(PlatformCompatibilityEpoch)</c>. Never an exact
/// build identity gate, never a per-identity seal on the roll path, never an image pin: those are
/// the workarounds this rule exists to retire.</para>
/// </summary>
public static class PlatformCompatibility
{
    /// <summary>
    /// The <see cref="AssemblyMetadataAttribute"/> key carrying the platform's declared
    /// compatibility epoch — stamped into every platform assembly by <c>Directory.Build.props</c>
    /// from <c>$(PlatformCompatibilityEpoch)</c>.
    /// </summary>
    public const string EpochMetadataKey = "MeshWeaverCompatibilityEpoch";

    /// <summary>The prefix every compatibility key starts with.</summary>
    public const char KeyPrefix = 'c';

    /// <summary>
    /// The compatibility key for a platform major and a declared epoch:
    /// <c>c&lt;major:D3&gt;e&lt;epoch:D3&gt;</c>. Pure.
    /// </summary>
    /// <param name="major">The platform's major version (0–999).</param>
    /// <param name="epoch">The declared compatibility epoch (0–999).</param>
    public static string KeyOf(int major, int epoch)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(major);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(major, 999);
        ArgumentOutOfRangeException.ThrowIfNegative(epoch);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(epoch, 999);
        return string.Create(CultureInfo.InvariantCulture, $"{KeyPrefix}{major:D3}e{epoch:D3}");
    }

    /// <summary>
    /// Parses a compatibility key back into its major and epoch, or false when the value is not one
    /// (a legacy <c>s…</c>/<c>g…</c>/MVID identity, a blank, anything else). Pure.
    /// </summary>
    public static bool TryParseKey(string? key, out int major, out int epoch)
    {
        major = epoch = 0;
        if (key is not { Length: 8 } || key[0] != KeyPrefix || key[4] != 'e')
            return false;
        return int.TryParse(key.AsSpan(1, 3), NumberStyles.None, CultureInfo.InvariantCulture, out major)
            && int.TryParse(key.AsSpan(5, 3), NumberStyles.None, CultureInfo.InvariantCulture, out epoch);
    }

    /// <summary>True when <paramref name="identity"/> is a compatibility key.</summary>
    public static bool IsKey(string? identity) => TryParseKey(identity, out _, out _);

    /// <summary>
    /// The compatibility key an assembly states: its <c>AssemblyVersion</c> major plus the
    /// <see cref="EpochMetadataKey"/> stamp, or null (with the reason) when it states no epoch — an
    /// assembly built outside the platform's <c>Directory.Build.props</c>.
    /// </summary>
    public static (string? Key, string? Problem) KeyOfAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var major = assembly.GetName().Version?.Major;
        var epochText = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => string.Equals(a.Key, EpochMetadataKey, StringComparison.Ordinal))
            ?.Value;
        return Compose(assembly.GetName().Name, major, epochText);
    }

    /// <summary>
    /// The compatibility key an assembly FILE states — metadata only, nothing loaded, so it answers
    /// for a foreign host's binaries (a container's extracted <c>/app</c>) exactly as
    /// <see cref="KeyOfAssembly"/> answers for a loaded one. Null with the reason when the file is
    /// missing, unreadable, or states no epoch.
    /// </summary>
    /// <param name="assemblyPath">Path to the assembly (in practice <c>MeshWeaver.Compiler.dll</c>).</param>
    public static (string? Key, string? Problem) KeyOfAssemblyFile(string assemblyPath)
    {
        if (!File.Exists(assemblyPath))
            return (null, $"'{assemblyPath}' does not exist");
        try
        {
            using var stream = File.OpenRead(assemblyPath);
            using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
            var md = pe.GetMetadataReader();
            var definition = md.GetAssemblyDefinition();
            return Compose(
                md.GetString(definition.Name),
                definition.Version.Major,
                ReadAssemblyMetadata(md, EpochMetadataKey));
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or InvalidOperationException
                                       or UnauthorizedAccessException)
        {
            return (null, $"'{assemblyPath}' could not be read ({ex.GetType().Name}: {ex.Message})");
        }
    }

    /// <summary>
    /// An <c>AssemblyMetadata(key, value)</c> value on the assembly definition, decoded by hand from
    /// the custom-attribute blob (two serialized strings after the 0x0001 prolog) because
    /// <see cref="System.Reflection.Metadata.MetadataReader"/> has no reflection-free typed decoder
    /// and this must not load the assembly. Null when absent.
    /// </summary>
    /// <param name="metadata">The assembly's metadata.</param>
    /// <param name="key">The metadata key.</param>
    public static string? ReadAssemblyMetadata(System.Reflection.Metadata.MetadataReader metadata, string key)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        foreach (var handle in metadata.GetAssemblyDefinition().GetCustomAttributes())
        {
            var attribute = metadata.GetCustomAttribute(handle);
            if (!IsAssemblyMetadataAttribute(metadata, attribute))
                continue;
            var blob = metadata.GetBlobReader(attribute.Value);
            if (blob.Length < 2 || blob.ReadUInt16() != 0x0001)
                continue;
            var k = blob.ReadSerializedString();
            var v = blob.ReadSerializedString();
            if (string.Equals(k, key, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(v))
                return v;
        }
        return null;
    }

    private static bool IsAssemblyMetadataAttribute(
        System.Reflection.Metadata.MetadataReader metadata, System.Reflection.Metadata.CustomAttribute attribute)
    {
        string? name = null;
        string? ns = null;
        switch (attribute.Constructor.Kind)
        {
            case System.Reflection.Metadata.HandleKind.MemberReference:
            {
                var member = metadata.GetMemberReference(
                    (System.Reflection.Metadata.MemberReferenceHandle)attribute.Constructor);
                if (member.Parent.Kind != System.Reflection.Metadata.HandleKind.TypeReference)
                    return false;
                var type = metadata.GetTypeReference(
                    (System.Reflection.Metadata.TypeReferenceHandle)member.Parent);
                name = metadata.GetString(type.Name);
                ns = metadata.GetString(type.Namespace);
                break;
            }
            case System.Reflection.Metadata.HandleKind.MethodDefinition:
            {
                var method = metadata.GetMethodDefinition(
                    (System.Reflection.Metadata.MethodDefinitionHandle)attribute.Constructor);
                var type = metadata.GetTypeDefinition(method.GetDeclaringType());
                name = metadata.GetString(type.Name);
                ns = metadata.GetString(type.Namespace);
                break;
            }
        }
        return string.Equals(name, nameof(AssemblyMetadataAttribute), StringComparison.Ordinal)
               && string.Equals(ns, "System.Reflection", StringComparison.Ordinal);
    }

    /// <summary>
    /// The pure composition behind <see cref="KeyOfAssembly"/> and the metadata-only file read —
    /// so both answer identically for the same (major, epoch) facts.
    /// </summary>
    public static (string? Key, string? Problem) Compose(string? assemblyName, int? major, string? epochText)
    {
        if (major is not { } m || m < 0 || m > 999)
            return (null, $"{assemblyName ?? "(unnamed)"} carries no usable AssemblyVersion major");
        if (!int.TryParse(epochText, NumberStyles.None, CultureInfo.InvariantCulture, out var epoch)
            || epoch > 999)
            return (null,
                $"{assemblyName ?? "(unnamed)"} carries no '{EpochMetadataKey}' assembly metadata "
                + $"(read '{epochText ?? "(absent)"}') — it was not built by the platform's "
                + "Directory.Build.props, so it states no compatibility epoch");
        return (KeyOf(m, epoch), null);
    }

    /// <summary>
    /// 🚨 <b>THE adoption decision</b> — why bytes produced under
    /// (<paramref name="producedKey"/>, <paramref name="producerPlatformVersion"/>) may NOT be
    /// adopted by a process running (<paramref name="liveKey"/>, <paramref name="livePlatformVersion"/>),
    /// or null when they may. Pure; every adoption site goes through it.
    ///
    /// <list type="number">
    /// <item><b>Key.</b> The produced key must EQUAL the live one — same major, same epoch. A
    /// different key is a declared break; an absent or legacy (<c>s…</c>/<c>g…</c>/MVID) key states
    /// no epoch and cannot be shown compatible, so it declines (and the caller compiles).</item>
    /// <item><b>Floor.</b> Within one key, the producer must not be NEWER than the running platform
    /// (<see cref="ProducerIsNewer"/>). An absent or unreadable producer version is accepted:
    /// unknown producer = older.</item>
    /// </list>
    /// </summary>
    /// <param name="producedKey">The key the producer stamped beside the bytes.</param>
    /// <param name="producerPlatformVersion">The producing platform build (e.g.
    /// <c>3.0.0-ci.9215</c>), or null when the producer recorded none.</param>
    /// <param name="liveKey">This process's key (<see cref="FrameworkBuildIdentity.FrameworkVersion"/>).</param>
    /// <param name="livePlatformVersion">This process's platform build
    /// (<c>PlatformBuildInfo.PlatformVersion</c>), or null when unknown.</param>
    public static string? DeclineReason(
        string? producedKey, string? producerPlatformVersion, string liveKey, string? livePlatformVersion)
        => DeclineReason(producedKey, producerPlatformVersion, null, liveKey, livePlatformVersion);

    /// <summary>
    /// 🚨 <b>THE adoption decision with the full platform RANGE</b> — floor (the producing platform
    /// build) and ceiling (the highest platform the bytes claim, OPEN by default):
    /// <c>same key AND floor &lt;= running &lt;= ceiling</c> ⇒ adopt and bind to the running platform;
    /// anything else ⇒ declined LOUDLY with both versions named. There is no other version gate.
    /// </summary>
    /// <param name="producedKey">The key the producer stamped beside the bytes.</param>
    /// <param name="floorPlatformVersion">The FLOOR — the producing platform build, or null
    /// (unknown producer = older = accepted).</param>
    /// <param name="ceilingPlatformVersion">The CEILING — the highest platform build the bytes claim
    /// to work on, or null (open). A declared break applies one to everything built against the
    /// previous epoch (<see cref="Declaration"/>).</param>
    /// <param name="liveKey">This process's key.</param>
    /// <param name="livePlatformVersion">This process's platform build, or null when unknown.</param>
    public static string? DeclineReason(
        string? producedKey,
        string? floorPlatformVersion,
        string? ceilingPlatformVersion,
        string liveKey,
        string? livePlatformVersion)
    {
        if (string.IsNullOrEmpty(producedKey))
            return $"the producer recorded no compatibility key, so it cannot be shown compatible "
                + $"with the live platform {liveKey}";
        if (!string.Equals(producedKey, liveKey, StringComparison.Ordinal))
        {
            if (!IsKey(producedKey))
                return $"built under the retired build identity {producedKey}, which states no "
                    + $"compatibility epoch; live platform is {liveKey}";
            var declared = TryParseKey(producedKey, out _, out var producedEpoch)
                           && Declaration.CeilingForEpoch(producedEpoch) is { } declaredCeiling
                ? $"; the declared break caps everything built against epoch {producedEpoch} at "
                  + $"platform {declaredCeiling}"
                : "";
            return $"built for compatibility key {producedKey}, live platform is {liveKey} "
                + $"({livePlatformVersion ?? "version unknown"}) — a declared compatibility-epoch or "
                + $"major break{declared}; rebuild and seal against {liveKey}";
        }
        if (ProducerIsNewer(floorPlatformVersion, livePlatformVersion))
            return $"its platform floor {floorPlatformVersion} (the build that produced it) is NEWER "
                + $"than the running platform {livePlatformVersion} (key {liveKey}) — bytes from a "
                + "newer build may bind surface this platform does not have; roll the platform "
                + "forward or publish for this build";
        if (ProducerIsNewer(livePlatformVersion, ceilingPlatformVersion))
            return $"the running platform {livePlatformVersion} (key {liveKey}) is ABOVE its "
                + $"platform ceiling {ceilingPlatformVersion} — it declares it works only up to that "
                + "build; rebuild and seal it against the running platform";
        return null;
    }

    /// <summary>
    /// The FLOOR half of <see cref="DeclineReason(string?, string?, string?, string, string?)"/>: true only when BOTH versions are readable and
    /// the producer is strictly newer. Comparable means both carry a run ordinal (two continuous
    /// builds) or neither does (two releases); a release against a continuous build is not ordered
    /// by run number and answers false — unknown is accepted, never declined.
    /// </summary>
    public static bool ProducerIsNewer(string? producerPlatformVersion, string? livePlatformVersion)
    {
        if (string.IsNullOrWhiteSpace(producerPlatformVersion) || string.IsNullOrWhiteSpace(livePlatformVersion))
            return false;
        var producerOrdinal = PlatformReleaseOrder.BuildOrdinal(producerPlatformVersion);
        var liveOrdinal = PlatformReleaseOrder.BuildOrdinal(livePlatformVersion);
        if ((producerOrdinal is null) != (liveOrdinal is null))
            return false;
        return PlatformReleaseOrder.Compare(producerPlatformVersion, livePlatformVersion) > 0;
    }

    /// <summary>
    /// The checked-in compatibility declaration (<c>src/MeshWeaver.Compiler/platform-compatibility.json</c>,
    /// embedded) — the ONE source of the epoch <c>Directory.Build.props</c> stamps and of every
    /// declared break. Parsed once; an unreadable resource yields an EMPTY declaration (no breaks),
    /// which only removes wording from a decline, never a decline itself.
    /// </summary>
    public static CompatibilityDeclaration Declaration => DeclarationValue.Value;

    private static readonly Lazy<CompatibilityDeclaration> DeclarationValue = new(() =>
    {
        try
        {
            using var stream = typeof(PlatformCompatibility).Assembly
                .GetManifestResourceStream("MeshWeaver.Compiler.platform-compatibility.json");
            return stream is null
                ? CompatibilityDeclaration.Empty
                : CompatibilityDeclaration.Parse(new StreamReader(stream).ReadToEnd());
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            return CompatibilityDeclaration.Empty;
        }
    });

    /// <summary>
    /// 🚨 <b>THE binding rule</b> for a platform-shared assembly reference out of a plugin or NodeType
    /// assembly: bind to the RUNNING platform's assembly whenever it is the same or a HIGHER version
    /// than the one compiled against — never a hard link to the exact version, never a private copy
    /// because the requested version is lower. A plugin compiled against <c>3.0.0.0</c> binds on a
    /// platform stamped <c>3.1.0.0</c>. A request for a HIGHER version than the running one is the
    /// floor-not-met case, declined loudly by the caller. Pure.
    /// </summary>
    /// <param name="compiledAgainst">The version the referencing assembly was compiled against
    /// (its AssemblyRef), or null for an unversioned reference.</param>
    /// <param name="running">The version the running platform carries.</param>
    public static bool MayBind(Version? compiledAgainst, Version running)
    {
        ArgumentNullException.ThrowIfNull(running);
        return compiledAgainst is null || running >= compiledAgainst;
    }
}

/// <summary>
/// One DECLARED BREAK: from <see cref="Epoch"/> on, everything built against the previous epoch is
/// capped at <see cref="PreviousEpochCeiling"/> — "works up to platform N-1".
/// </summary>
/// <param name="Epoch">The epoch the break introduced.</param>
/// <param name="PreviousEpochCeiling">The last platform build of the previous epoch — the ceiling
/// applied to every plugin/NodeType built against it.</param>
/// <param name="Reason">Why the break was declared.</param>
public sealed record CompatibilityBreak(int Epoch, string? PreviousEpochCeiling, string? Reason);

/// <summary>
/// The parsed <c>platform-compatibility.json</c>: the current epoch and every declared break. Pure
/// parse; see <see cref="PlatformCompatibility.Declaration"/>.
/// </summary>
/// <param name="Epoch">The current compatibility epoch.</param>
/// <param name="Breaks">Every declared break, oldest first.</param>
public sealed record CompatibilityDeclaration(int Epoch, System.Collections.Immutable.ImmutableArray<CompatibilityBreak> Breaks)
{
    /// <summary>No epoch, no breaks.</summary>
    public static CompatibilityDeclaration Empty { get; } = new(0, []);

    /// <summary>The ceiling the declared break applies to bytes built against
    /// <paramref name="epoch"/>, or null when no break closed that epoch.</summary>
    public string? CeilingForEpoch(int epoch) =>
        Breaks.FirstOrDefault(b => b.Epoch == epoch + 1)?.PreviousEpochCeiling;

    /// <summary>Parses the declaration's JSON text. Throws <see cref="System.Text.Json.JsonException"/>
    /// on malformed input.</summary>
    public static CompatibilityDeclaration Parse(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var root = document.RootElement;
        var epoch = root.TryGetProperty("epoch", out var e) && e.TryGetInt32(out var parsed) ? parsed : 0;
        var breaks = System.Collections.Immutable.ImmutableArray.CreateBuilder<CompatibilityBreak>();
        if (root.TryGetProperty("breaks", out var list) && list.ValueKind == System.Text.Json.JsonValueKind.Array)
            foreach (var item in list.EnumerateArray())
                breaks.Add(new CompatibilityBreak(
                    item.TryGetProperty("epoch", out var be) && be.TryGetInt32(out var bEpoch) ? bEpoch : 0,
                    item.TryGetProperty("previousEpochCeiling", out var c) ? c.GetString() : null,
                    item.TryGetProperty("reason", out var r) ? r.GetString() : null));
        return new CompatibilityDeclaration(epoch, breaks.ToImmutable());
    }
}
