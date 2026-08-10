using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// Policy + bounds for one <see cref="InstanceComboAssembler"/> run.
/// </summary>
public sealed record ComboAssemblyOptions
{
    /// <summary>
    /// Materialise modules whose only pin MOVES (a branch, a tag, an unverifiable ref). Default
    /// <c>false</c>: a gate run on a moving ref is not evidence — two runs can resolve to different
    /// content — so the assembler REFUSES those modules unless the caller explicitly opts in.
    /// </summary>
    public bool AllowMoving { get; init; }

    /// <summary>
    /// Assemble a combo whose <see cref="InstanceCombo.IsComplete"/> is <c>false</c> (a source
    /// query failed when it was read, so the module list is KNOWN short). Default <c>false</c>:
    /// a gate over a partial set would read as though it verified the instance.
    /// </summary>
    public bool AllowIncomplete { get; init; }

    /// <summary>Budget for ONE repo fetch. A fetch that exceeds it is a NAMED failure for every
    /// module riding it — never an unbounded hang.</summary>
    public TimeSpan FetchTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Access token handed to the fetch (empty = anonymous, for public repos).</summary>
    public string AccessToken { get; init; } = "";

    /// <summary>
    /// Repository URL per registry source NAME, for package-coordinate modules — an install record
    /// carries <see cref="PackageCoordinate.SourceName"/> but no URL (the registry holds the
    /// credential and the repo mapping), so an off-portal assembly must be told where each source's
    /// repo lives.
    /// </summary>
    public ImmutableDictionary<string, string> SourceRepositories { get; init; } =
        ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);

    /// <summary>Fallback repository URL for a package-coordinate module whose source name is
    /// absent from <see cref="SourceRepositories"/> (or recorded as null).</summary>
    public string? DefaultSourceRepository { get; init; }

    /// <summary>Progress sink (default: silent). The console tool passes <c>Console.Out</c>.</summary>
    public TextWriter Output { get; init; } = TextWriter.Null;
}

/// <summary>How a materialised module tree is pinned — what a later run would have to resolve to
/// reproduce the same input.</summary>
public enum MaterializationPin
{
    /// <summary>Nothing was materialised (the module was refused or failed).</summary>
    None,

    /// <summary>Fetched at an immutable commit sha: re-running resolves identically.</summary>
    ExactCommit,

    /// <summary>Fetched at a movable ref, but the tree PROVED itself: its <c>manifest.lock</c>
    /// module version equals the recorded <see cref="PackageCoordinate.ModuleVersion"/>.</summary>
    VerifiedModuleVersion,

    /// <summary>Fetched at a movable ref with no proof of identity — allowed only via
    /// <see cref="ComboAssemblyOptions.AllowMoving"/>, and stamped so the gate's verdict can never
    /// silently read as reproducible evidence.</summary>
    Moving,
}

/// <summary>One module's assembly outcome.</summary>
public enum ModuleAssemblyStatus
{
    /// <summary>Not attempted, by POLICY: a moving/unrecorded ref without the explicit allow flag,
    /// or an id that cannot be a directory. The error names the module and the way out.</summary>
    Refused,

    /// <summary>Attempted and failed: an unresolvable source, a fetch error/timeout, an empty or
    /// unsafe file set, or a failed module-version proof. The error is specific to this module.</summary>
    Failed,

    /// <summary>The module's files are on disk under its folder, hashed and counted.</summary>
    Materialized,
}

/// <summary>
/// One line of the assembly MANIFEST: module → resolved ref → content hash. This is what lets the
/// gate's verdict name its exact input (and later lands on <c>Admin/UpdatePolicy</c>).
/// </summary>
public sealed record ModuleAssembly
{
    /// <summary>The module (mesh partition) this entry describes — also its folder name under the
    /// work root when materialised.</summary>
    public string ModuleId { get; init; } = "";

    /// <summary>The outcome.</summary>
    public ModuleAssemblyStatus Status { get; init; }

    /// <summary>The repository the module was (or would have been) fetched from.</summary>
    public string? RepositoryUrl { get; init; }

    /// <summary>The ref the fetch was asked for — a commit sha, tag, or branch.</summary>
    public string? RequestedRef { get; init; }

    /// <summary>The commit the fetch actually resolved to (the snapshot's sha).</summary>
    public string? ResolvedCommit { get; init; }

    /// <summary>The coordinate's recorded fidelity, straight off the combo — how well the INPUT
    /// was pinned before this run did anything.</summary>
    public RefFidelity Fidelity { get; init; }

    /// <summary>How the MATERIALISED tree is pinned — see <see cref="MaterializationPin"/>.</summary>
    public MaterializationPin Pin { get; init; }

    /// <summary>The install record's <see cref="PackageCoordinate.ModuleVersion"/>, when one was
    /// recorded — the content hash the module claims to be.</summary>
    public string? RecordedModuleVersion { get; init; }

    /// <summary>The <c>manifest.lock</c> module version found in the FETCHED tree, when it ships
    /// one — what the tree says it is.</summary>
    public string? FetchedModuleVersion { get; init; }

    /// <summary>Whether recorded and fetched module versions agree; <c>null</c> when either side is
    /// missing so no comparison is possible. A mismatch on an exact-commit fetch is DIVERGENCE
    /// (the space synced past the install), reported here rather than failing the module.</summary>
    public bool? ModuleVersionMatches { get; init; }

    /// <summary>Number of files written for this module.</summary>
    public int FileCount { get; init; }

    /// <summary>
    /// Deterministic hash of the materialised file set: sha256 over the sorted
    /// <c>path NUL sha256(bytes) LF</c> lines. Two assemblies of identical content produce the
    /// same value, so the gate's verdict can name its input by content, not just by ref.
    /// </summary>
    public string? ContentHash { get; init; }

    /// <summary>Whether the module root carries an <c>index.json</c> — without one,
    /// <c>mw-plugin-test</c> will not discover the folder as a package.</summary>
    public bool DeclaresRoot { get; init; }

    /// <summary>The speaking reason when <see cref="Status"/> is not
    /// <see cref="ModuleAssemblyStatus.Materialized"/>.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// The whole run's manifest — written to <see cref="InstanceComboAssembler.ManifestFileName"/> at
/// the work root AND returned to the caller. Breadth-complete: every module of the combo has an
/// entry, so one broken module never hides another.
/// </summary>
public sealed record ComboAssemblyReport
{
    /// <summary>When this assembly ran (UTC).</summary>
    public DateTimeOffset AssembledAt { get; init; }

    /// <summary>The combo snapshot's <see cref="InstanceCombo.ReadAt"/> — which read of the
    /// instance this assembly materialised.</summary>
    public DateTimeOffset ComboReadAt { get; init; }

    /// <summary>The combo's own completeness claim, carried through verbatim.</summary>
    public bool ComboIsComplete { get; init; }

    /// <summary>Whether the run was allowed to materialise moving refs.</summary>
    public bool AllowMoving { get; init; }

    /// <summary>Whether the run was allowed to assemble a known-incomplete combo.</summary>
    public bool AllowIncomplete { get; init; }

    /// <summary>The combo's caveats, carried through so the gate's verdict inherits them.</summary>
    public ImmutableList<string> ComboCaveats { get; init; } = [];

    /// <summary>Every module's outcome, ordered by module id.</summary>
    public ImmutableList<ModuleAssembly> Modules { get; init; } = [];

    /// <summary>A whole-run refusal (incomplete combo without the allow flag, an empty combo) —
    /// nothing was fetched or written except this manifest.</summary>
    public string? FatalError { get; init; }

    /// <summary>All green: no fatal error, at least one module, every module materialised.</summary>
    [JsonIgnore]
    public bool Success =>
        FatalError is null
        && Modules.Count > 0
        && Modules.All(m => m.Status == ModuleAssemblyStatus.Materialized);

    /// <summary>Process exit code: 0 all green, 1 anything else.</summary>
    [JsonIgnore]
    public int ExitCode => Success ? 0 : 1;

    /// <summary>Human summary for the console / CI log.</summary>
    public void WriteSummary(TextWriter output)
    {
        output.WriteLine();
        if (FatalError is not null)
            output.WriteLine($"FATAL: {FatalError}");
        foreach (var module in Modules)
        {
            var mark = module.Status == ModuleAssemblyStatus.Materialized ? "ok " : "RED";
            var detail = module.Status == ModuleAssemblyStatus.Materialized
                ? $"{module.Pin} @ {Shorten(module.ResolvedCommit)} — {module.FileCount} file(s), " +
                  $"hash {Shorten(module.ContentHash)}" +
                  (module.DeclaresRoot ? "" : " — ⚠ no index.json root (the gate will not discover it)") +
                  (module.ModuleVersionMatches == false
                      ? $" — ⚠ moduleVersion diverged from the install record ({Shorten(module.RecordedModuleVersion)} recorded)"
                      : "")
                : $"{module.Status}: {module.Error}";
            output.WriteLine($"  {mark} {module.ModuleId} [{detail}]");
        }
        var materialized = Modules.Count(m => m.Status == ModuleAssemblyStatus.Materialized);
        var failed = Modules.Count(m => m.Status == ModuleAssemblyStatus.Failed);
        var refused = Modules.Count(m => m.Status == ModuleAssemblyStatus.Refused);
        output.WriteLine(
            $"combo assembly: {Modules.Count} module(s) — {materialized} materialised, " +
            $"{failed} failed, {refused} refused → {(Success ? "GREEN" : "RED")}");
    }

    private static string Shorten(string? value) =>
        value is null ? "(none)" : value.Length > 12 ? value[..12] : value;
}
