using System.Collections.Immutable;
using System.Text.Json;
using MeshWeaver.Plugin.Packaging;

namespace MeshWeaver.GitSync;

/// <summary>
/// The per-module outcome vocabulary of <see cref="ModuleSyncDecision"/> — OPEN string constants
/// (policy <c>open-vocabulary-string-constants</c>): persisted on the sync config, read by the
/// settings tab and <c>/health</c>, and never switched over exhaustively.
/// </summary>
public static class ModuleSyncOutcomeKind
{
    /// <summary>The module's manifest hash equals the one this Space holds: nothing is written.</summary>
    public const string Unchanged = "Unchanged";

    /// <summary>The module changed (or was never recorded): it syncs to the incoming commit.</summary>
    public const string Synced = "Synced";

    /// <summary>The module declares a platform floor above the running platform: it alone is not
    /// written, and every sibling module still syncs.</summary>
    public const string Declined = "Declined";
}

/// <summary>
/// One module as the INCOMING tree states it: where it lives in the Space, its
/// <c>manifest.lock</c> content hash, and the platform floor it declares.
/// </summary>
/// <param name="Module">The module name (<c>manifest.lock</c>'s <c>module</c>, else its folder).</param>
/// <param name="Root">Space-relative folder the module's <c>manifest.lock</c> sits in — empty when
/// the Space IS the module (its sync subdirectory is the module folder).</param>
/// <param name="ModuleVersion">The manifest's <c>moduleVersion</c> — the content hash — or null when
/// the manifest carries none.</param>
/// <param name="Floor">The module's declared platform floor (<c>content.minMeshVersion</c> of its
/// root <c>index.json</c>), or null when it declares none.</param>
public sealed record ModuleReading(string Module, string Root, string? ModuleVersion, string? Floor)
{
    /// <summary>
    /// The package requirements the module root's <c>index.json</c> declares
    /// (<c>content.requires</c>, e.g. <c>AI@^1.20.0</c>) — what its sources need of the modules
    /// they call into. Empty when it declares none. An init property, not a fifth positional
    /// parameter: that would replace a public record's constructor (a binary break).
    /// </summary>
    public ImmutableList<string> Requires { get; init; } = [];
}

/// <summary>
/// One module's outcome for one import, as recorded on the sync config
/// (<see cref="GitHubSyncConfig.ModuleOutcomes"/>) and printed by <c>/health</c>.
/// </summary>
/// <param name="Module">The module name.</param>
/// <param name="Outcome">A <see cref="ModuleSyncOutcomeKind"/> value.</param>
/// <param name="HeldVersion">The manifest hash this Space held before the import, or null.</param>
/// <param name="IncomingVersion">The manifest hash the incoming tree carries, or null.</param>
/// <param name="Reason">Log copy: why this outcome.</param>
public sealed record ModuleSyncOutcome(
    string Module, string Outcome, string? HeldVersion, string? IncomingVersion, string Reason)
{
    /// <summary>The Space-relative folder of the module ("" when the Space is the module).</summary>
    public string Root { get; init; } = "";

    /// <summary>The declared floor, when the module was declined on it.</summary>
    public string? Floor { get; init; }

    /// <summary>The package requirement the module was declined on (<c>AI@^1.20.0</c>), when it
    /// was declined because this instance runs that package's module below the requirement's floor
    /// (MeshWeaver.Plugins#2715); null otherwise.</summary>
    public string? UnmetRequirement { get; init; }

    /// <summary>The version of the required package's module this instance runs, when the module
    /// was declined on <see cref="UnmetRequirement"/>; null otherwise.</summary>
    public string? RunningDependencyVersion { get; init; }

    /// <summary>The short form every surface names a requirement decline by:
    /// <c>Hosting (requires AI@^1.20.0, runs 1.12.1)</c>.</summary>
    public string DescribeUnmetRequirement() =>
        $"{Module} (requires {UnmetRequirement}, runs {RunningDependencyVersion})";
}

/// <summary>
/// 🚨 <b>Every module syncs, judged ALONE by the content hash in its <c>manifest.lock</c></b> —
/// policy <c>module-sync-per-manifest-hash</c>, coordinated with
/// <c>platform-backwards-compatibility</c>. Pure; the design is
/// <c>Doc/Architecture/ModuleSyncPerManifestHash</c>.
///
/// <para><b>The rule, per module, in order:</b></para>
/// <list type="number">
///   <item><description><b>Declined</b> — the module declares a platform floor above the running
///   platform (<see cref="PlatformFloor.Evaluate"/> — the ONE floor decision every package consumer
///   uses, policy <c>package-min-mesh-version</c>; an unknown, unreadable or unordered version on
///   either side, or a local <c>-ci.0</c> build, is accepted, never declined) — or (MeshWeaver.Plugins#2715,
///   <see cref="DecideAgainstRunningModules"/>) requires a package release above the module this
///   instance runs. Either way the module's paths are neither written nor pruned, the reason names
///   both versions, and it holds NO sibling module.</description></item>
///   <item><description><b>Unchanged</b> — the incoming <c>moduleVersion</c> equals the one this
///   Space recorded when that module last landed, and the import is not a reconcile or a force:
///   nothing is written.</description></item>
///   <item><description><b>Synced</b> — anything else (changed, never recorded, or a manifest with
///   no hash): the module syncs to the incoming commit. Whether each of its NodeTypes then adopts a
///   prebuilt bundle or compiles from the synced source is decided per type, by the bundle's
///   fingerprint and platform range — never by whether the sources arrive.</description></item>
/// </list>
///
/// <para>A tree with no <c>manifest.lock</c> states no module: no outcome is produced and the
/// import runs exactly as it did before (course repos, content repos).</para>
/// </summary>
public static class ModuleSyncDecision
{
    /// <summary>The manifest sidecar's file name (<c>ModuleManifest.FileName</c> in the catalog;
    /// restated here because GitSync is below the catalog in the dependency graph).</summary>
    public const string ManifestFileName = "manifest.lock";

    /// <summary>
    /// Decides every module of an incoming tree.
    /// </summary>
    /// <param name="incoming">The modules the incoming tree carries.</param>
    /// <param name="held">Module → manifest hash this Space recorded when each last landed; null or
    /// empty for a Space that never recorded one.</param>
    /// <param name="runningPlatformVersion">The running platform build, or null when unknown.</param>
    /// <param name="reconcile">True for a reconcile or a force import — the live mesh is measured
    /// against the tree, so no module may be skipped as unchanged.</param>
    /// <returns>One outcome per module, ordinal by module name.</returns>
    public static ImmutableList<ModuleSyncOutcome> Decide(
        IReadOnlyList<ModuleReading> incoming,
        IReadOnlyDictionary<string, string>? held,
        string? runningPlatformVersion,
        bool reconcile)
        => DecideAgainstRunningModules(incoming, held, runningPlatformVersion, reconcile, runningModules: null);

    /// <summary>
    /// <see cref="Decide"/> with the package versions of the modules this instance RUNS — the second
    /// per-module decline (MeshWeaver.Plugins#2715): a module whose <see cref="ModuleReading.Requires"/>
    /// names a package this instance runs BELOW the requirement's floor is declined, exactly as a
    /// platform floor above the running platform is, and every sibling still syncs.
    ///
    /// <para>🚨 <b>Why.</b> In-mesh sources compile against the module assemblies the process has
    /// LOADED, not against the ones their repository builds them with. Measured on the control
    /// instance 2026-10-02: a Hosting import brought sources calling <c>ModelOutcome</c> /
    /// <c>ModelCalibration</c> (MeshWeaver.AI from Plugins#2638) while the instance ran AI 1.12.1,
    /// and 16 Hosting NodeTypes went to <c>Error</c> (CS0246/CS0103) — nothing held the import. A
    /// declined module keeps its last-good sources and builds; it syncs on the first import after
    /// the required module version is running.</para>
    ///
    /// <para>🚨 <b>Only the FLOOR is judged, and only where something is known.</b> A requirement
    /// whose package this instance runs no recorded version of (a content-only package, a module
    /// with no recorded version) decides nothing; neither does a range shape without a readable
    /// lower bound. The upper bound of a caret range is NOT a decline here: a NEWER major of a
    /// dependency is a compatibility question the module set's own check answers
    /// (<c>ModuleDependencyFloor</c>), while an OLDER one is certainly missing what the sources
    /// call — which is the failure this exists to prevent.</para>
    /// </summary>
    /// <param name="incoming">The modules the incoming tree carries.</param>
    /// <param name="held">Module → manifest hash this Space recorded when each last landed.</param>
    /// <param name="runningPlatformVersion">The running platform build, or null when unknown.</param>
    /// <param name="reconcile">True for a reconcile or a force import.</param>
    /// <param name="runningModules">Package id (case-insensitive) → the version of its module this
    /// instance runs (<c>MeshWeaver.Mesh.ActivatedModuleVersion</c>); null or empty judges no
    /// requirement.</param>
    /// <returns>One outcome per module, ordinal by module name.</returns>
    public static ImmutableList<ModuleSyncOutcome> DecideAgainstRunningModules(
        IReadOnlyList<ModuleReading> incoming,
        IReadOnlyDictionary<string, string>? held,
        string? runningPlatformVersion,
        bool reconcile,
        IReadOnlyDictionary<string, string>? runningModules)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        return incoming
            .OrderBy(m => m.Module, StringComparer.Ordinal)
            .Select(module => DecideOne(module, held, runningPlatformVersion, reconcile, runningModules))
            .ToImmutableList();
    }

    /// <summary>
    /// The first requirement of <paramref name="requires"/> whose package this instance runs at a
    /// version BELOW the requirement's floor, as (requirement, package, running version, floor);
    /// null when every judged requirement is met or none can be judged. Pure.
    /// </summary>
    /// <param name="requires">The declared requirements (<c>AI@^1.20.0</c>).</param>
    /// <param name="runningModules">Package id → running module version.</param>
    public static (string Requirement, string Package, string Running, string Floor)? UnmetFloor(
        IReadOnlyList<string> requires, IReadOnlyDictionary<string, string>? runningModules)
    {
        ArgumentNullException.ThrowIfNull(requires);
        if (runningModules is null || runningModules.Count == 0)
            return null;
        foreach (var requirement in requires)
        {
            if (string.IsNullOrWhiteSpace(requirement))
                continue;
            var at = requirement.IndexOf('@');
            var package = (at < 0 ? requirement : requirement[..at]).Trim();
            if (package.Length == 0
                || !TryGetIgnoreCase(runningModules, package, out var running)
                || !IsOrderedVersion(running))
                continue;
            if (at < 0 || LowerBound(requirement[(at + 1)..]) is not { } floor)
                continue;
            if (NuGetVersionComparer.Instance.Compare(running, floor) < 0)
                return (requirement.Trim(), package, running, floor);
        }
        return null;
    }

    private static bool TryGetIgnoreCase(IReadOnlyDictionary<string, string> map, string key, out string value)
    {
        foreach (var pair in map)
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(pair.Value))
            {
                value = pair.Value;
                return true;
            }
        value = "";
        return false;
    }

    /// <summary>The inclusive lower bound of a requirement range — <c>^1.20.0</c>, <c>~1.20.0</c>,
    /// <c>&gt;=1.20.0</c>, <c>=1.20.0</c> or a bare <c>1.20.0</c> all floor at <c>1.20.0</c> — or
    /// null for a shape with no readable floor (<c>*</c>, <c>&gt;1.2</c>, empty).</summary>
    private static string? LowerBound(string range)
    {
        var text = range.Trim();
        if (text.StartsWith(">=", StringComparison.Ordinal))
            text = text[2..];
        else if (text.StartsWith('^') || text.StartsWith('~') || text.StartsWith('='))
            text = text[1..];
        text = text.Trim();
        return IsOrderedVersion(text) ? text : null;
    }

    private static bool IsOrderedVersion(string text)
    {
        var core = text.Trim();
        var cut = core.IndexOfAny(['-', '+']);
        if (cut >= 0)
            core = core[..cut];
        var parts = core.Split('.');
        return parts.Length is >= 1 and <= 4 && parts.All(p => p.Length > 0 && p.All(char.IsAsciiDigit));
    }

    private static ModuleSyncOutcome DecideOne(
        ModuleReading module, IReadOnlyDictionary<string, string>? held,
        string? runningPlatformVersion, bool reconcile, IReadOnlyDictionary<string, string>? runningModules)
    {
        var heldVersion = held is not null && held.TryGetValue(module.Module, out var h) ? h : null;

        // 🚨 The ONE floor decision every package consumer uses (PlatformFloor — policy
        // package-min-mesh-version): only a floor comparable with the running platform and strictly
        // above it declines; an unreadable or unordered floor, an unknown running version or a
        // local -ci.0 build proceeds (advisory), which is what keeps an rc or clean floor from
        // holding a ci build (the 2026-09-07 trap).
        var floorVerdict = PlatformFloor.Evaluate(module.Floor, runningPlatformVersion);
        if (floorVerdict.IsHeld)
            return new ModuleSyncOutcome(module.Module, ModuleSyncOutcomeKind.Declined, heldVersion,
                module.ModuleVersion,
                $"module '{module.Module}' declares platform ≥ {floorVerdict.Floor} but this instance runs "
                + $"{runningPlatformVersion} — it is not written until the platform is rolled forward; "
                + "every other module syncs (policies package-min-mesh-version, "
                + "platform-backwards-compatibility)")
            {
                Root = module.Root,
                Floor = floorVerdict.Floor,
            };

        // 🚨 MeshWeaver.Plugins#2715 — the second per-module decline: the sources need a module
        // version this instance does not run yet. Writing them would recompile every NodeType they
        // touch against the OLDER loaded module and park it in Error; declining keeps the last-good
        // sources and builds. It applies to a reconcile too — the loaded module is what it is.
        if (UnmetFloor(module.Requires, runningModules) is { } unmet)
            return new ModuleSyncOutcome(module.Module, ModuleSyncOutcomeKind.Declined, heldVersion,
                module.ModuleVersion,
                $"module '{module.Module}' requires {unmet.Requirement} but this instance runs "
                + $"'{unmet.Package}' {unmet.Running} — it is not written until a '{unmet.Package}' "
                + $"module at or above {unmet.Floor} is running here, so its NodeTypes keep their "
                + "last-good sources instead of compiling against a module that lacks what they call; "
                + "every other module syncs (MeshWeaver.Plugins#2715)")
            {
                Root = module.Root,
                UnmetRequirement = unmet.Requirement,
                RunningDependencyVersion = unmet.Running,
            };

        if (!reconcile
            && module.ModuleVersion is { Length: > 0 } incomingVersion
            && string.Equals(incomingVersion, heldVersion, StringComparison.Ordinal))
            return new ModuleSyncOutcome(module.Module, ModuleSyncOutcomeKind.Unchanged, heldVersion,
                incomingVersion,
                $"module '{module.Module}' is unchanged at manifest hash {incomingVersion} — nothing written")
            {
                Root = module.Root,
            };

        return new ModuleSyncOutcome(module.Module, ModuleSyncOutcomeKind.Synced, heldVersion,
            module.ModuleVersion,
            reconcile
                ? $"module '{module.Module}' is re-imported (reconcile) at manifest hash {module.ModuleVersion ?? "(none)"}"
                : heldVersion is null
                    ? $"module '{module.Module}' syncs at manifest hash {module.ModuleVersion ?? "(none)"} (no hash recorded before)"
                    : $"module '{module.Module}' changed {heldVersion} → {module.ModuleVersion ?? "(none)"} — it syncs")
        {
            Root = module.Root,
        };
    }

    /// <summary>
    /// Reads every module an incoming tree states: each <c>manifest.lock</c> (at the Space root or
    /// any depth), its <c>moduleVersion</c>, and the declared floor from the module root's
    /// <c>index.json</c> (<c>content.minMeshVersion</c>). Pure and tolerant: an unparsable manifest
    /// still names its module (its folder) with no hash, so it syncs — it is never skipped as
    /// unchanged on a reading that failed.
    /// </summary>
    /// <param name="files">The tree's files as (Space-relative path, text content).</param>
    /// <returns>The modules, ordinal by root.</returns>
    public static ImmutableList<ModuleReading> Read(IEnumerable<(string Path, string Content)> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var all = files.ToList();
        var byPath = all
            .GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Content, StringComparer.OrdinalIgnoreCase);
        return all
            .Where(f => IsManifestPath(f.Path))
            .Select(f =>
            {
                var root = f.Path.Length == ManifestFileName.Length
                    ? ""
                    : f.Path[..(f.Path.Length - ManifestFileName.Length - 1)];
                var (module, version) = ParseManifest(f.Content);
                var name = module is { Length: > 0 }
                    ? module
                    : root.Length > 0 ? root[(root.LastIndexOf('/') + 1)..] : "(root)";
                var indexPath = root.Length == 0 ? "index.json" : root + "/index.json";
                var hasIndex = byPath.TryGetValue(indexPath, out var index);
                var floor = hasIndex ? ParseFloor(index!) : null;
                return new ModuleReading(name, root, version, floor)
                {
                    Requires = hasIndex ? ParseRequires(index!) : [],
                };
            })
            .OrderBy(m => m.Root, StringComparer.Ordinal)
            .ToImmutableList();
    }

    /// <summary>
    /// The manifest hashes an import may RECORD once it concluded: every module that synced or was
    /// unchanged at its incoming hash; a declined module keeps what the Space held before (it did
    /// not land), and a module that is gone from the tree is dropped.
    /// </summary>
    /// <param name="outcomes">The import's per-module outcomes.</param>
    /// <returns>Module → manifest hash.</returns>
    public static ImmutableDictionary<string, string> Recorded(IEnumerable<ModuleSyncOutcome> outcomes)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var o in outcomes)
        {
            var version = o.Outcome == ModuleSyncOutcomeKind.Declined ? o.HeldVersion : o.IncomingVersion;
            if (version is { Length: > 0 })
                builder[o.Module] = version;
        }
        return builder.ToImmutable();
    }

    /// <summary>True when <paramref name="relativePath"/> is a <c>manifest.lock</c> at any depth.</summary>
    public static bool IsManifestPath(string relativePath)
        => string.Equals(relativePath, ManifestFileName, StringComparison.OrdinalIgnoreCase)
           || relativePath.EndsWith("/" + ManifestFileName, StringComparison.OrdinalIgnoreCase);

    private static (string? Module, string? Version) ParseManifest(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object)
                return (null, null);
            return (
                r.TryGetProperty("module", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null,
                r.TryGetProperty("moduleVersion", out var v) && v.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(v.GetString())
                    ? v.GetString()
                    : null);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static ImmutableList<string> ParseRequires(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object
                || !r.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.Object
                || !content.TryGetProperty("requires", out var requires)
                || requires.ValueKind != JsonValueKind.Array)
                return [];
            return requires.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(e.GetString()))
                .Select(e => e.GetString()!.Trim())
                .ToImmutableList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? ParseFloor(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            return r.ValueKind == JsonValueKind.Object
                   && r.TryGetProperty("content", out var content)
                   && content.ValueKind == JsonValueKind.Object
                   && content.TryGetProperty("minMeshVersion", out var floor)
                   && floor.ValueKind == JsonValueKind.String
                   && !string.IsNullOrWhiteSpace(floor.GetString())
                ? floor.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
