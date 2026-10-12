namespace MeshWeaver.PluginCatalog;

/// <summary>
/// 🌍 The reason codes of a release-availability ADVISORY (MeshWeaver#6540) — what the release
/// check reported about a candidate without holding on it, persisted on <c>Admin/UpdatePolicy</c>
/// and rendered on the Updates tab in the VIEWER's language.
///
/// <para>Each code IS its catalog key in <c>strings.{en,de}.json</c>; the advisory travels as a
/// <see cref="MeshWeaver.Data.LocalizableText"/> (its English, this key, and named arguments:
/// package, module, identity, target, …), so the record keeps the English sentence for the log
/// and for every older reader, and the tab localizes at render time.</para>
///
/// <para>🚨 An OPEN vocabulary (policy <c>open-vocabulary-string-constants</c>): a module or host
/// may key its own advisory with its own constant and catalog entry, and a renderer never validates
/// a code against this list. A code no catalog carries renders the stored English — never a default
/// branch that means something.</para>
/// </summary>
public static class ReleaseAdvisoryCode
{
    /// <summary>Packages the instance would Roslyn-compile at boot on the target: <c>{target}</c>,
    /// <c>{identity}</c>, <c>{packages}</c>.</summary>
    public const string BootCompile = "releaseAdvisory.bootCompile";

    /// <summary>The target adopts a set sealed by an earlier compatible build: <c>{target}</c>,
    /// <c>{count}</c>, <c>{key}</c>, <c>{packages}</c>.</summary>
    public const string OlderCompatibleSet = "releaseAdvisory.olderCompatibleSet";

    /// <summary>A declared <c>minMeshVersion</c> floor the target does not rank above:
    /// <c>{package}</c>, <c>{floor}</c>, <c>{running}</c>.</summary>
    public const string PlatformFloor = "releaseAdvisory.platformFloor";

    /// <summary>A declared floor that could not be compared (target version unknown):
    /// <c>{package}</c>, <c>{floor}</c>.</summary>
    public const string PlatformFloorUnknown = "releaseAdvisory.platformFloorUnknown";

    /// <summary>An install record naming a module no publisher produces: <c>{package}</c>,
    /// <c>{module}</c>, <c>{identity}</c>.</summary>
    public const string ModuleNotProduced = "releaseAdvisory.moduleNotProduced";

    /// <summary>A landed module that was not measured against the target's surface:
    /// <c>{package}</c>, <c>{module}</c>, <c>{target}</c>.</summary>
    public const string ModuleLinkNotMeasured = "releaseAdvisory.moduleLinkNotMeasured";

    /// <summary>A landed module whose link check could not be made, quoting the measurement's own
    /// diagnostic: <c>{package}</c>, <c>{module}</c>, <c>{target}</c>, <c>{detail}</c>.</summary>
    public const string ModuleLinkUndetermined = "releaseAdvisory.moduleLinkUndetermined";

    /// <summary>A landed module that links with assembly version skew the loader rolls forward:
    /// <c>{package}</c>, <c>{module}</c>, <c>{target}</c>, <c>{skew}</c>.</summary>
    public const string ModuleLinkSkew = "releaseAdvisory.moduleLinkSkew";

    /// <summary>Packages not available for the release, which do not follow the platform roll:
    /// <c>{count}</c>, <c>{reason}</c> (the package verdict's reasons, quoted).</summary>
    public const string PackagesDoNotFollow = "releaseAdvisory.packagesDoNotFollow";

    /// <summary>Package availability could not be determined, which decides nothing about the
    /// platform roll: <c>{reason}</c>.</summary>
    public const string AvailabilityUndetermined = "releaseAdvisory.availabilityUndetermined";

    /// <summary>The combo gate recorded RED for the release: <c>{reason}</c>.</summary>
    public const string ComboRed = "releaseAdvisory.comboRed";

    /// <summary>The combo gate's recorded refusal of a candidate, reported on the roll selection:
    /// <c>{refusal}</c>.</summary>
    public const string ModulesDoNotFollow = "releaseAdvisory.modulesDoNotFollow";
}
