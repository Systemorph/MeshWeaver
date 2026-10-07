using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// 🚨 <b>How far behind its source a stale-but-serving build may be before it stops serving</b> —
/// Systemorph/Memex#668, policy <c>stale-adoption-bound</c> (<c>Doc/Architecture/PolicyNotProse</c>).
///
/// <para><b>The defect.</b> <see cref="ModuleVersionCompatibility"/> keeps an adopted build serving
/// over source that moved past it whenever the two share a MAJOR (MeshWeaver#3583 — serving over
/// dead). Nothing bounded HOW FAR within that MAJOR. Measured on memex.systemorph.com: a
/// <c>Hosting/InstanceAction</c> build at module version 1.29.7 kept serving over source at 1.56 —
/// twenty-seven MINOR versions, across a change to how operations are signed — and every dispatch it
/// signed failed verification downstream, with nothing on the node or on <c>/health</c> saying how
/// old the serving build was. A build that far behind is not "the last build, briefly"; it is a
/// different program.</para>
///
/// <para><b>The rule.</b> A build more than the bound's MINOR versions behind the current source is
/// REFUSED exactly like a MAJOR bump (<see cref="BuildProvenance.AdoptionRefused"/>): on a mesh that
/// compiles, its coordinates are cleared and the live source is compiled; on a mesh that cannot, it
/// is kept but not run and the type reads Unavailable with the reason. Within the bound it keeps
/// serving, LOUDLY: every judgement logs the distance and the bound, and <c>/health</c>'s live record
/// census counts the stale-but-serving records.</para>
///
/// <para>Configured, never hard-coded at a call site: <see cref="ConfigKey"/>. A negative value
/// disables the bound (the pre-#668 behaviour) — an explicit operator choice that every judgement
/// prints, never a silent default. When either version cannot be read nothing is measured and nothing
/// is refused on this rule — the same INCONCLUSIVE rule the MAJOR check follows.</para>
/// </summary>
public static class StaleAdoptionBound
{
    /// <summary>The configuration key.</summary>
    public const string ConfigKey = "Modules:StaleAdoptionMaxMinorVersionsBehind";
    /// <summary>
    /// The default bound, in MINOR versions — a <c>proposed</c> value in the policy register
    /// (<c>stale-adoption-bound</c>) until ratified; a deployment overrides it with
    /// <see cref="ConfigKey"/>.
    /// </summary>
    public const int DefaultMaxMinorVersionsBehind = 5;

    /// <summary>The effective bound: <see cref="ConfigKey"/>, or
    /// <see cref="DefaultMaxMinorVersionsBehind"/> when unset or unreadable. Negative = disabled.</summary>
    /// <param name="configuration">The mesh's configuration, or null (then the default).</param>
    public static int MaxMinorVersionsBehind(IConfiguration? configuration)
        => configuration?[ConfigKey] is { } raw
           && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var configured)
            ? configured
            : DefaultMaxMinorVersionsBehind;

    /// <summary>
    /// True when the adopted build trails the current source by MORE than
    /// <paramref name="maxMinorVersionsBehind"/> MINOR versions within one MAJOR. False when the
    /// distance cannot be measured, when it is within the bound, or when the bound is disabled.
    /// </summary>
    /// <param name="adoptedVersion">The adopted build's module version.</param>
    /// <param name="currentVersion">The current source's module version.</param>
    /// <param name="maxMinorVersionsBehind">The bound; negative disables it.</param>
    public static bool Exceeds(string? adoptedVersion, string? currentVersion, int maxMinorVersionsBehind)
        => maxMinorVersionsBehind >= 0
           && ModuleVersionCompatibility.MinorVersionsBehind(adoptedVersion, currentVersion) is { } behind
           && behind > maxMinorVersionsBehind;

    /// <summary>The distance for a sentence.</summary>
    /// <param name="adoptedVersion">The adopted build's module version.</param>
    /// <param name="currentVersion">The current source's module version.</param>
    public static string DescribeDistance(string? adoptedVersion, string? currentVersion)
        => ModuleVersionCompatibility.MinorVersionsBehind(adoptedVersion, currentVersion) is { } behind
            ? $"{behind} minor version(s) behind"
            : "distance not measurable (a version is unknown or unparseable)";

    /// <summary>The bound for a sentence.</summary>
    /// <param name="maxMinorVersionsBehind">The bound; negative = disabled.</param>
    public static string DescribeBound(int maxMinorVersionsBehind)
        => maxMinorVersionsBehind < 0
            ? $"the bound is DISABLED ({ConfigKey} is negative)"
            : $"bound {maxMinorVersionsBehind} ({ConfigKey})";
}
