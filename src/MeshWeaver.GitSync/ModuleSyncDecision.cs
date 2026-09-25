using System.Collections.Immutable;
using System.Text.Json;
using MeshWeaver.Compiler;

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
public sealed record ModuleReading(string Module, string Root, string? ModuleVersion, string? Floor);

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
///   platform (<see cref="PlatformCompatibility.ProducerIsNewer"/>, the ladder's own comparison;
///   an unknown version on either side is accepted, never declined). The ONE per-module decline:
///   the module's paths are neither written nor pruned, the reason names both versions, and it
///   holds NO sibling module.</description></item>
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
    {
        ArgumentNullException.ThrowIfNull(incoming);
        return incoming
            .OrderBy(m => m.Module, StringComparer.Ordinal)
            .Select(module => DecideOne(module, held, runningPlatformVersion, reconcile))
            .ToImmutableList();
    }

    private static ModuleSyncOutcome DecideOne(
        ModuleReading module, IReadOnlyDictionary<string, string>? held,
        string? runningPlatformVersion, bool reconcile)
    {
        var heldVersion = held is not null && held.TryGetValue(module.Module, out var h) ? h : null;

        if (module.Floor is { Length: > 0 } floor
            && PlatformCompatibility.ProducerIsNewer(floor, runningPlatformVersion))
            return new ModuleSyncOutcome(module.Module, ModuleSyncOutcomeKind.Declined, heldVersion,
                module.ModuleVersion,
                $"module '{module.Module}' declares platform ≥ {floor} but this instance runs "
                + $"{runningPlatformVersion} — it is not written until the platform is rolled forward; "
                + "every other module syncs (policy platform-backwards-compatibility)")
            {
                Root = module.Root,
                Floor = floor,
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
                var floor = byPath.TryGetValue(indexPath, out var index) ? ParseFloor(index) : null;
                return new ModuleReading(name, root, version, floor);
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
