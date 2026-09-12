using System.Globalization;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Whether a module build may keep serving over source that has moved past it — decided on the
/// module's SemVer, never on the source fingerprint (MeshWeaver#3583).
/// </summary>
public enum ModuleVersionVerdict
{
    /// <summary>Both versions are known and share the same MAJOR: the build is compatible with
    /// the source and keeps serving (<see cref="BuildProvenance.StaleAdopted"/>).</summary>
    Compatible = 1,

    /// <summary>Both versions are known and the MAJOR differs — a declared incompatibility. The
    /// build is refused (<see cref="BuildProvenance.AdoptionRefused"/>), without erroring the
    /// type.</summary>
    Incompatible = 2,

    /// <summary>At least one side is not known (a legacy bundle that recorded no version, a
    /// partition root that carries none, a value that does not parse). Nothing was compared, so
    /// nothing is refused — the same INCONCLUSIVE rule the fingerprint judgement follows: a probe
    /// must not answer its scariest branch on its own inability to run.</summary>
    Unknown = 3,
}

/// <summary>
/// The ONE rule for "may the build that is serving keep serving after the source moved" —
/// MODULE VERSION COMPATIBILITY, as the platform owner stated it on 2026-09-09
/// (MeshWeaver#3583):
///
/// <list type="bullet">
///   <item>the adopted build's module version and the current source's module version share the
///     same MAJOR ⇒ compatible ⇒ the build keeps serving, marked stale-but-serving;</item>
///   <item>only a MAJOR bump — a declared incompatibility — may refuse the build, and even then
///     the type reports "incompatible, awaiting bundle" rather than erroring;</item>
///   <item>the source fingerprint stays the signal that the source MOVED (it drives the "pending"
///     status and the readiness notification); it no longer drives refusal.</item>
/// </list>
///
/// <para><b>Where the two versions come from.</b> The adopted side is the bundle manifest's
/// <c>Version</c> — the module's released SemVer, the value <c>manifest.lock</c>'s <c>version</c>
/// carries and the tag (<c>Essentials/v1.2.3</c>) is cut from — stamped onto the NodeType as
/// <c>AdoptedModuleVersion</c> when the bytes are adopted. The current side is the partition
/// root's <c>content.version</c> (the <c>Store/Plugin</c> root's authored MAJOR.MINOR plus the
/// build's PATCH, which the tree sync rewrites with the sources), published by the sources
/// watcher as <c>CurrentModuleVersion</c> beside the source fingerprint. The root is used rather
/// than the bundle's manifest for the CURRENT side because the incident's whole shape is "the
/// source moved and no bundle for this identity has caught up": the only current version on the
/// mesh is the one the sync wrote.</para>
///
/// <para>Pure. Parses leniently — a leading <c>v</c>, a prerelease or build suffix, a two-part
/// version all read; anything without a leading integer is UNKNOWN.</para>
/// </summary>
public static class ModuleVersionCompatibility
{
    /// <summary>The verdict for an adopted build at <paramref name="adoptedVersion"/> over source
    /// at <paramref name="currentVersion"/>.</summary>
    public static ModuleVersionVerdict Classify(string? adoptedVersion, string? currentVersion)
    {
        var adopted = MajorOf(adoptedVersion);
        var current = MajorOf(currentVersion);
        if (adopted is null || current is null)
            return ModuleVersionVerdict.Unknown;
        return adopted == current
            ? ModuleVersionVerdict.Compatible
            : ModuleVersionVerdict.Incompatible;
    }

    /// <summary>True exactly when <see cref="Classify"/> answers
    /// <see cref="ModuleVersionVerdict.Incompatible"/> — the only verdict that refuses.</summary>
    public static bool Refuses(string? adoptedVersion, string? currentVersion)
        => Classify(adoptedVersion, currentVersion) is ModuleVersionVerdict.Incompatible;

    /// <summary>
    /// The MAJOR component of a SemVer-ish string, or null when none can be read.
    ///
    /// <para>🚨 A value is a VERSION only when its leading integer is followed by a version
    /// separator (<c>.</c>, <c>-</c>, <c>+</c>) or by the end of the string. A content HASH such as
    /// <c>221c6c286785ddf2</c> — which is what a bundle baked without a released version carried
    /// as its module version — starts with digits too, and reading those digits as "major 221"
    /// against a root at <c>1.10</c> classified every such bundle INCOMPATIBLE and refused it.
    /// Measured on memex 2026-09-12: the CI bundle for the running framework identity sat on the
    /// prebuilt volume, its source fingerprint matched the live sources, and
    /// <c>Prebuilt assembly for Store/Plugin DECLINED … (bundle module version 221c6c286785ddf2,
    /// current 1.10: Incompatible)</c> sent every instance of the type through a Roslyn compile
    /// instead. Ten of sixteen hex hashes start with a digit, so the refusal was the common case,
    /// not a corner. A hash is UNKNOWN — nothing was compared — never a declared MAJOR bump.</para>
    /// </summary>
    public static int? MajorOf(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return null;
        var span = version.AsSpan().Trim();
        if (span.Length > 0 && (span[0] == 'v' || span[0] == 'V'))
            span = span[1..];
        var end = 0;
        while (end < span.Length && char.IsAsciiDigit(span[end]))
            end++;
        if (end == 0)
            return null;
        // The digits must END the version core: a following letter (a hash, a word) means the
        // value is not a version at all, and its digits are not a major.
        if (end < span.Length && span[end] is not ('.' or '-' or '+'))
            return null;
        return int.TryParse(span[..end], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            ? major
            : null;
    }

    /// <summary>A version for a sentence: the value, or "(unknown)".</summary>
    public static string Display(string? version)
        => string.IsNullOrWhiteSpace(version) ? "(unknown)" : version.Trim();
}
