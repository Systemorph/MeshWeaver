using System.Globalization;
using System.Reflection;
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
    {
        if (string.IsNullOrEmpty(producedKey))
            return $"the producer recorded no compatibility key, so it cannot be shown compatible "
                + $"with the live platform {liveKey}";
        if (!string.Equals(producedKey, liveKey, StringComparison.Ordinal))
            return IsKey(producedKey)
                ? $"built for compatibility key {producedKey}, live platform is {liveKey} — a "
                  + "declared compatibility-epoch or major break"
                : $"built under the retired build identity {producedKey}, which states no "
                  + $"compatibility epoch; live platform is {liveKey}";
        if (ProducerIsNewer(producerPlatformVersion, livePlatformVersion))
            return $"produced by platform {producerPlatformVersion}, NEWER than the running "
                + $"platform {livePlatformVersion} (key {liveKey}) — bytes from a newer build may "
                + "bind surface this platform does not have; roll the platform forward or publish "
                + "for this build";
        return null;
    }

    /// <summary>
    /// The FLOOR half of <see cref="DeclineReason"/>: true only when BOTH versions are readable and
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
