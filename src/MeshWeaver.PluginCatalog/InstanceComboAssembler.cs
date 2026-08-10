using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeshWeaver.GitSync;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// Turns an <see cref="InstanceCombo"/> — what an instance actually runs, as
/// <see cref="InstanceComboReader"/> states it — into a materialised repo-root directory that
/// <c>mw-plugin-test</c> can verify: every module's files at its RECORDED ref under
/// <c>&lt;workRoot&gt;/&lt;ModuleId&gt;/</c>, plus a MANIFEST
/// (<see cref="ManifestFileName"/>) naming module → resolved ref → content hash, so the gate's
/// verdict can name its exact input. Step 2 of the Candidate Release Protocol's instance gate
/// (<c>Doc/Architecture/CandidateReleaseProtocol</c> — "What is missing is only the combo
/// assembler").
///
/// <para><b>Refs resolve by fidelity, and moving refs are REFUSED by default.</b> A gate run on a
/// moving ref is not evidence — two runs can resolve to different content — and must never
/// silently become one:</para>
/// <list type="bullet">
///   <item>an exact commit sha (a GitSync entry's <c>lastSyncCommitSha</c>, an install record's
///     sha-shaped ref) fetches directly — <see cref="MaterializationPin.ExactCommit"/>;</item>
///   <item>a recorded <see cref="PackageCoordinate.ModuleVersion"/> with only a movable fetch ref
///     fetches and then PROVES itself against the tree's <c>manifest.lock</c> —
///     <see cref="MaterializationPin.VerifiedModuleVersion"/> on match, a NAMED failure
///     otherwise (degraded to <see cref="MaterializationPin.Moving"/> only under the explicit
///     <see cref="ComboAssemblyOptions.AllowMoving"/> flag);</item>
///   <item>a branch/tag with no proof needs <see cref="ComboAssemblyOptions.AllowMoving"/>, and
///     the manifest stamps the pin as moving;</item>
///   <item>a module with nothing recorded cannot be materialised at all — the error names it.</item>
/// </list>
///
/// <para><b>Breadth-complete.</b> Every module is attempted (or refused) independently; a fetch
/// failure is a NAMED failure for exactly the modules riding that fetch, and the run continues to
/// collect ALL failures rather than stopping at the first — the same reporting doctrine as the
/// protocol's closure walk.</para>
///
/// <para><b>Fetching reuses the existing machinery.</b> The fetch seam is
/// <see cref="IGitHubRepoClient.Fetch(string,string,string?,string)"/>'s delegate shape (the same
/// one <see cref="NodeRepoPackageSource"/> is built on); package-coordinate modules go through
/// <see cref="NodeRepoPackageSource.FetchPackageFiles"/> against a snapshot fetched ONCE per
/// (repo, ref) — no per-module refetch, no hand-rolled git. GitSync-coordinate modules fetch the
/// recorded <c>subdirectory</c> at the recorded sha — a single shallow pack, never a full-history
/// clone.</para>
///
/// <para>Reactive end to end; the blocking file writes ride the injected <see cref="IIoPool"/>.
/// Cold: nothing fetches or writes until Subscribe. The one Task bridge lives in the
/// <c>mw-combo-assemble</c> console tool's <c>Program.cs</c>.</para>
/// </summary>
public sealed class InstanceComboAssembler
{
    /// <summary>The manifest file written at the work root. A plain file — never a top-level
    /// folder — so <c>mw-plugin-test</c>'s package discovery cannot mistake it for a module.</summary>
    public const string ManifestFileName = "combo-assembly.json";

    /// <summary>Serializer for the combo INPUT and the manifest OUTPUT: web casing, indented,
    /// enums as strings — the manifest is read by humans and by the gate reporter alike.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly Func<string, string, string?, string, IObservable<RepoSnapshot>> fetch;
    private readonly IIoPool filePool;
    private readonly ComboAssemblyOptions options;
    private readonly ILogger? logger;

    /// <summary>
    /// Creates an assembler over a fetch seam and a file pool.
    /// </summary>
    /// <param name="fetch">Snapshot fetch: (repositoryUrl, gitRef, subdirectory, accessToken) →
    /// one <see cref="RepoSnapshot"/>. Production wires
    /// <see cref="IGitHubRepoClient.Fetch(string,string,string?,string)"/>; tests hand in an
    /// in-memory fake — no network.</param>
    /// <param name="filePool">The pool the blocking file writes run on (never a hub scheduler).</param>
    /// <param name="options">Policy + bounds; defaults refuse moving refs and incomplete combos.</param>
    /// <param name="logger">Diagnostics.</param>
    public InstanceComboAssembler(
        Func<string, string, string?, string, IObservable<RepoSnapshot>> fetch,
        IIoPool filePool,
        ComboAssemblyOptions? options = null,
        ILogger? logger = null)
    {
        this.fetch = fetch;
        this.filePool = filePool;
        this.options = options ?? new ComboAssemblyOptions();
        this.logger = logger;
    }

    /// <summary>
    /// Assembles <paramref name="combo"/> into <paramref name="workRoot"/>. Cold — subscribe to
    /// run it. Emits the full report once (the same content as the manifest file), then completes;
    /// it never faults on a per-module problem — failures are entries, so the caller always learns
    /// everything that went wrong, not just the first thing.
    /// </summary>
    /// <param name="combo">The instance's combo, as the reader stated it.</param>
    /// <param name="workRoot">The directory to materialise into (created if missing). Its
    /// top-level folders become the repo root <c>mw-plugin-test</c> takes.</param>
    public IObservable<ComboAssemblyReport> Assemble(InstanceCombo combo, string workRoot) =>
        Observable.Defer(() =>
        {
            var report = new ComboAssemblyReport
            {
                AssembledAt = DateTimeOffset.UtcNow,
                ComboReadAt = combo.ReadAt,
                ComboIsComplete = combo.IsComplete,
                AllowMoving = options.AllowMoving,
                AllowIncomplete = options.AllowIncomplete,
                ComboCaveats = combo.Caveats,
            };

            if (!combo.IsComplete && !options.AllowIncomplete)
                return Finish(workRoot, report with
                {
                    FatalError =
                        "the combo is INCOMPLETE — a source query failed when it was read, so its "
                        + "module list is known to be short. A gate over a partial set would read "
                        + "as though it verified the instance; pass --allow-incomplete to assemble "
                        + "what WAS read anyway.",
                });

            if (combo.Modules.Count == 0)
                return Finish(workRoot, report with
                {
                    FatalError =
                        "the combo carries no modules — there is nothing to assemble, and a gate "
                        + "over an empty root would pass vacuously.",
                });

            var plans = combo.Modules.Select(Plan).ToImmutableList();
            var refused = plans
                .Where(plan => plan.Fetch is null)
                .Select(plan => Entry(plan.Module) with
                {
                    Status = plan.ErrorStatus,
                    Error = plan.Error,
                })
                .ToImmutableList();

            // ONE fetch per (repo, ref, subdirectory) — package modules of the same node repo at
            // the same ref share a single snapshot instead of refetching per module.
            var groups = plans
                .Where(plan => plan.Fetch is not null)
                .GroupBy(plan => (
                    plan.Fetch!.RepositoryUrl,
                    plan.Fetch.GitRef,
                    Subdirectory: plan.Fetch.Subdirectory ?? ""))
                .OrderBy(group => group.Key)
                .ToImmutableList();

            return groups
                .Select(group => MaterializeGroup(
                    group.Key.RepositoryUrl,
                    group.Key.GitRef,
                    group.Key.Subdirectory.Length == 0 ? null : group.Key.Subdirectory,
                    group.ToImmutableList(),
                    workRoot))
                .ToObservable().Concat().ToList()
                .Select(materialized => report with
                {
                    Modules = refused
                        .Concat(materialized.SelectMany(entries => entries))
                        .OrderBy(entry => entry.ModuleId, StringComparer.OrdinalIgnoreCase)
                        .ToImmutableList(),
                })
                .SelectMany(complete => Finish(workRoot, complete));
        });

    // ══════════════════════════════════════════════════════════════════════════
    //  Planning — pure, per module; refusals happen HERE, before any fetch
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>What one module needs fetched: which repo at which ref, GitSync's subdirectory
    /// (fetch-relative) OR the package folder to filter (whole-repo fetch), the pin the
    /// materialisation claims, and whether it must prove itself against the recorded
    /// module version.</summary>
    private sealed record FetchSpec(
        string RepositoryUrl,
        string GitRef,
        string? Subdirectory,
        string? FolderFilter,
        MaterializationPin Pin,
        bool RequireModuleVersionMatch);

    private sealed record ModulePlan(ModuleCoordinate Module)
    {
        public FetchSpec? Fetch { get; init; }
        public ModuleAssemblyStatus ErrorStatus { get; init; }
        public string? Error { get; init; }

        public static ModulePlan Refuse(ModuleCoordinate module, string error) =>
            new(module) { ErrorStatus = ModuleAssemblyStatus.Refused, Error = error };

        public static ModulePlan Fail(ModuleCoordinate module, string error) =>
            new(module) { ErrorStatus = ModuleAssemblyStatus.Failed, Error = error };
    }

    private ModulePlan Plan(ModuleCoordinate module)
    {
        if (InvalidModuleIdReason(module.ModuleId) is { } idReason)
            return ModulePlan.Refuse(module,
                $"module id '{module.ModuleId}' cannot name a directory ({idReason}).");

        var syncRepo = NullIfBlank(module.GitSync?.RepositoryUrl);
        var syncSha = NullIfBlank(module.GitSync?.LastSyncCommitSha);
        var subdirectory = NullIfBlank(module.GitSync?.Subdirectory);
        var package = module.Package;
        var recordedModuleVersion = NullIfBlank(package?.ModuleVersion);

        // 1. The sync entry with an exact sha — the production majority, and the one coordinate
        //    that names repo, ref AND folder all at once.
        if (syncRepo is not null && syncSha is not null && LooksLikeCommitSha(syncSha))
            return new ModulePlan(module)
            {
                Fetch = new FetchSpec(syncRepo, syncSha, subdirectory, FolderFilter: null,
                    MaterializationPin.ExactCommit, RequireModuleVersionMatch: false),
            };

        // 2. The install record, when its source resolves to a repo.
        if (package is not null)
        {
            var packageRepo = ResolveSourceRepository(package.SourceName);
            if (packageRepo is not null)
            {
                var folder = NullIfBlank(package.PackageId) ?? module.ModuleId;
                var installedRef = NullIfBlank(package.InstalledFromRef);
                var version = NullIfBlank(package.Version);

                if (installedRef is not null && LooksLikeCommitSha(installedRef))
                    return PackagePlan(packageRepo, installedRef, MaterializationPin.ExactCommit,
                        requireMatch: false);
                if (version is not null && LooksLikeCommitSha(version))
                    return PackagePlan(packageRepo, version, MaterializationPin.ExactCommit,
                        requireMatch: false);
                if (recordedModuleVersion is not null)
                    // No exact GIT ref, but the record claims an exact IDENTITY (a content hash
                    // over the module's own files). Fetch the best movable ref — the recorded
                    // ref, else the node repos' per-module release tag ({Module}/vX.Y.Z), else
                    // HEAD — and PROVE the tree against the recorded hash.
                    return PackagePlan(packageRepo,
                        installedRef ?? (version is not null ? $"{folder}/v{version}" : "HEAD"),
                        MaterializationPin.VerifiedModuleVersion, requireMatch: true);
                if (options.AllowMoving)
                    return PackagePlan(packageRepo, installedRef ?? version ?? "HEAD",
                        MaterializationPin.Moving, requireMatch: false);
                return ModulePlan.Refuse(module, MovingRefusal(module));

                ModulePlan PackagePlan(string repo, string gitRef, MaterializationPin pin,
                    bool requireMatch) =>
                    new(module)
                    {
                        Fetch = new FetchSpec(repo, gitRef, Subdirectory: null, folder, pin,
                            requireMatch),
                    };
            }

            if (syncRepo is null)
                return ModulePlan.Fail(module,
                    $"no repository is known for source '{package.SourceName ?? "(unnamed)"}' — "
                    + "an install record names its source but not its repo; pass "
                    + "--source <name>=<url> (or --default-source <url>).");
        }

        // 3. The sync entry without an exact sha — a branch, or a recorded value that is not a
        //    sha. Movable, so it needs proof or the explicit flag.
        if (syncRepo is not null)
        {
            var movingRef = syncSha ?? NullIfBlank(module.GitSync?.Branch) ?? "HEAD";
            if (recordedModuleVersion is not null)
                return new ModulePlan(module)
                {
                    Fetch = new FetchSpec(syncRepo, movingRef, subdirectory, FolderFilter: null,
                        MaterializationPin.VerifiedModuleVersion, RequireModuleVersionMatch: true),
                };
            if (options.AllowMoving)
                return new ModulePlan(module)
                {
                    Fetch = new FetchSpec(syncRepo, movingRef, subdirectory, FolderFilter: null,
                        MaterializationPin.Moving, RequireModuleVersionMatch: false),
                };
            return ModulePlan.Refuse(module, MovingRefusal(module));
        }

        // 4. Nothing names a repository in either shape — no flag can help.
        return ModulePlan.Refuse(module,
            $"module '{module.ModuleId}' names no repository in either shape (no sync entry "
            + "repositoryUrl, no resolvable install-record source) — it cannot be materialised "
            + "from what is recorded.");
    }

    private static string MovingRefusal(ModuleCoordinate module) =>
        $"module '{module.ModuleId}' is pinned only to "
        + (module.Fidelity == RefFidelity.Unrecorded
            ? "nothing — no ref is recorded at all"
            : $"a MOVING ref ('{module.ProvenanceRef}')")
        + ". Two runs of this combo can resolve it to different content, so a gate run on it is "
        + "not evidence. Pass --allow-moving to materialise it anyway (the manifest will stamp "
        + "the pin as moving).";

    private string? ResolveSourceRepository(string? sourceName) =>
        sourceName is not null && options.SourceRepositories.TryGetValue(sourceName, out var repo)
            ? repo
            : NullIfBlank(options.DefaultSourceRepository);

    // ══════════════════════════════════════════════════════════════════════════
    //  Fetch + materialise
    // ══════════════════════════════════════════════════════════════════════════

    private IObservable<ImmutableList<ModuleAssembly>> MaterializeGroup(
        string repositoryUrl, string gitRef, string? subdirectory,
        ImmutableList<ModulePlan> members, string workRoot)
    {
        options.Output.WriteLine(
            $"fetching {repositoryUrl} @ {gitRef}"
            + (subdirectory is null ? "" : $" (subdirectory {subdirectory})")
            + $" for {members.Count} module(s)…");
        return fetch(repositoryUrl, gitRef, subdirectory, options.AccessToken)
            .Take(1)
            .Timeout(options.FetchTimeout)
            .SelectMany(snapshot => members
                .Select(member => MaterializeModule(member, snapshot, workRoot))
                .ToObservable().Concat().ToList()
                .Select(entries => entries.ToImmutableList()))
            .Catch((Exception exception) =>
            {
                var error =
                    $"fetch of '{repositoryUrl}' at '{gitRef}' failed within the "
                    + $"{options.FetchTimeout.TotalSeconds:F0}s budget: "
                    + $"{exception.GetType().Name}: {exception.Message}";
                logger?.LogWarning(exception,
                    "[ComboAssembly] fetch of {Repo} at {Ref} failed — {Count} module(s) named "
                    + "as failed; the run continues.", repositoryUrl, gitRef, members.Count);
                options.Output.WriteLine($"  RED {error}");
                return Observable.Return(members
                    .Select(member => Entry(member.Module) with
                    {
                        Status = ModuleAssemblyStatus.Failed,
                        RepositoryUrl = repositoryUrl,
                        RequestedRef = gitRef,
                        Error = error,
                    })
                    .ToImmutableList());
            });
    }

    private IObservable<ModuleAssembly> MaterializeModule(
        ModulePlan plan, RepoSnapshot snapshot, string workRoot)
    {
        var spec = plan.Fetch!;
        var baseline = Entry(plan.Module) with
        {
            RepositoryUrl = spec.RepositoryUrl,
            RequestedRef = spec.GitRef,
            ResolvedCommit = snapshot.CommitSha,
        };
        return SelectModuleFiles(spec, snapshot)
            .SelectMany(files =>
            {
                if (files.Count == 0)
                    return Observable.Return(baseline with
                    {
                        Status = ModuleAssemblyStatus.Failed,
                        Error = spec.FolderFilter is null
                            ? $"the fetch of '{spec.RepositoryUrl}' at '{spec.GitRef}'"
                              + (spec.Subdirectory is null ? "" : $" (subdirectory '{spec.Subdirectory}')")
                              + " returned no files."
                            : $"folder '{spec.FolderFilter}/' does not exist in "
                              + $"'{spec.RepositoryUrl}' at '{spec.GitRef}'.",
                    });

                if (files.Select(f => InvalidPathReason(f.Path)).FirstOrDefault(r => r is not null)
                    is { } pathReason)
                    return Observable.Return(baseline with
                    {
                        Status = ModuleAssemblyStatus.Failed,
                        Error = $"the fetched tree carries an unsafe path ({pathReason}) — "
                                + "refusing to write it.",
                    });

                var verdict = VerifyModuleVersion(plan, spec, files, baseline);
                if (verdict.Failed is not null)
                    return Observable.Return(verdict.Failed);

                return filePool.InvokeBlocking(_ =>
                        WriteModule(workRoot, plan.Module.ModuleId, files))
                    .Select(written => verdict.Entry with
                    {
                        Status = ModuleAssemblyStatus.Materialized,
                        FileCount = files.Count,
                        ContentHash = written.ContentHash,
                        DeclaresRoot = written.DeclaresRoot,
                    })
                    .Do(entry => options.Output.WriteLine(
                        $"  ok  {entry.ModuleId}: {entry.FileCount} file(s) @ "
                        + $"{snapshot.CommitSha} → {entry.Pin}"));
            })
            .Catch((Exception exception) =>
            {
                logger?.LogWarning(exception,
                    "[ComboAssembly] materialising {Module} failed.", plan.Module.ModuleId);
                return Observable.Return(baseline with
                {
                    Status = ModuleAssemblyStatus.Failed,
                    Error = $"{exception.GetType().Name}: {exception.Message}",
                });
            });
    }

    /// <summary>
    /// The module's file set, module-root-relative. A GitSync fetch already returns
    /// subdirectory-relative paths; a package fetch goes through
    /// <see cref="NodeRepoPackageSource.FetchPackageFiles"/> against the ALREADY-fetched snapshot
    /// (the same in-memory-snapshot pattern <c>mw-plugin-test</c> uses) — inheriting its
    /// binary-preservation semantics — and strips the package-folder prefix.
    /// </summary>
    private static IObservable<IReadOnlyList<RepoFile>> SelectModuleFiles(
        FetchSpec spec, RepoSnapshot snapshot)
    {
        if (spec.FolderFilter is null)
            return Observable.Return<IReadOnlyList<RepoFile>>(
                snapshot.Files.ToImmutableList());

        var source = new NodeRepoPackageSource(
            (_, _, _, _) => Observable.Return(snapshot), repoUrl: spec.RepositoryUrl);
        var prefix = spec.FolderFilter + "/";
        return source
            .FetchPackageFiles(new PackageManifest { Id = spec.FolderFilter }, spec.GitRef)
            .Select(files => (IReadOnlyList<RepoFile>)files
                .Select(file => new RepoFile(
                    file.RelativePath[prefix.Length..], file.Content, file.Binary))
                .ToImmutableList());
    }

    private (ModuleAssembly Entry, ModuleAssembly? Failed) VerifyModuleVersion(
        ModulePlan plan, FetchSpec spec, IReadOnlyList<RepoFile> files, ModuleAssembly baseline)
    {
        var recorded = NullIfBlank(plan.Module.Package?.ModuleVersion);
        var sidecar = files.FirstOrDefault(file => string.Equals(
            file.Path, ModuleManifest.FileName, StringComparison.OrdinalIgnoreCase));
        var fetched = sidecar is null
            ? null
            : ModuleManifest.TryParse(sidecar.Content, logger)?.ModuleVersion;
        var matches = recorded is not null && fetched is not null
            ? string.Equals(recorded, fetched, StringComparison.Ordinal)
            : (bool?)null;

        var entry = baseline with
        {
            Pin = spec.Pin,
            RecordedModuleVersion = recorded,
            FetchedModuleVersion = fetched,
            ModuleVersionMatches = matches,
        };

        if (!spec.RequireModuleVersionMatch)
            return (entry, null);
        if (matches == true)
            return (entry with { Pin = MaterializationPin.VerifiedModuleVersion }, null);

        // The recorded identity is a content hash and the fetched tree cannot prove it holds it.
        // Under the explicit moving flag the caller accepts unpinned input — stamped as such;
        // otherwise this materialisation would NOT be the recorded input, so it is a failure.
        if (options.AllowMoving)
            return (entry with { Pin = MaterializationPin.Moving }, null);
        return (entry, entry with
        {
            Status = ModuleAssemblyStatus.Failed,
            Error = fetched is null
                ? $"the install record pins moduleVersion '{recorded}', but the tree fetched at "
                  + $"'{spec.GitRef}' ships no readable manifest.lock — the materialisation "
                  + "cannot be proven to be the recorded input. Pass --allow-moving to accept "
                  + "unproven content."
                : $"moduleVersion mismatch: the install record pins '{recorded}' but the tree "
                  + $"fetched at '{spec.GitRef}' carries '{fetched}' — the ref has moved past "
                  + "the recorded install. Pass --allow-moving to accept the moved content.",
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Disk — blocking leaves, on the file pool
    // ══════════════════════════════════════════════════════════════════════════

    private sealed record WrittenModule(string ContentHash, bool DeclaresRoot);

    private static WrittenModule WriteModule(
        string workRoot, string moduleId, IReadOnlyList<RepoFile> files)
    {
        var moduleRoot = Path.Combine(Path.GetFullPath(workRoot), moduleId);
        Directory.CreateDirectory(moduleRoot);
        var manifest = new StringBuilder();
        var declaresRoot = false;
        foreach (var file in files.OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            var bytes = file.Bytes;
            var full = Path.Combine(moduleRoot,
                file.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, bytes);
            manifest.Append(file.Path).Append('\0')
                .Append(Convert.ToHexStringLower(SHA256.HashData(bytes))).Append('\n');
            declaresRoot |= string.Equals(file.Path, "index.json", StringComparison.OrdinalIgnoreCase);
        }
        var contentHash = Convert.ToHexStringLower(
            SHA256.HashData(Utf8NoBom.GetBytes(manifest.ToString())));
        return new WrittenModule(contentHash, declaresRoot);
    }

    /// <summary>Writes the manifest (also on refusal — the manifest documents WHY nothing was
    /// materialised) and emits the report.</summary>
    private IObservable<ComboAssemblyReport> Finish(string workRoot, ComboAssemblyReport report) =>
        filePool.InvokeBlocking(_ =>
        {
            var root = Path.GetFullPath(workRoot);
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, ManifestFileName),
                JsonSerializer.Serialize(report, Json), Utf8NoBom);
            return report;
        });

    // ══════════════════════════════════════════════════════════════════════════
    //  Small pure helpers
    // ══════════════════════════════════════════════════════════════════════════

    private static ModuleAssembly Entry(ModuleCoordinate module) => new()
    {
        ModuleId = module.ModuleId,
        Fidelity = module.Fidelity,
        RepositoryUrl = module.RepositoryUrl,
        RequestedRef = module.ProvenanceRef,
        RecordedModuleVersion = NullIfBlank(module.Package?.ModuleVersion),
    };

    /// <summary>A 40- or 64-character hex string — mirrors
    /// <see cref="ModuleCoordinate"/>'s classification of exact refs.</summary>
    private static bool LooksLikeCommitSha(string value) =>
        value is { Length: 40 or 64 } && value.All(char.IsAsciiHexDigit);

    private static string? InvalidModuleIdReason(string moduleId) =>
        string.IsNullOrWhiteSpace(moduleId) ? "empty"
        : moduleId is "." or ".." ? "path traversal"
        : moduleId.Contains('/') || moduleId.Contains('\\') ? "contains a path separator"
        : moduleId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ? "invalid characters"
        : null;

    private static string? InvalidPathReason(string relativePath) =>
        string.IsNullOrWhiteSpace(relativePath) ? "empty path"
        : Path.IsPathRooted(relativePath) ? $"rooted path '{relativePath}'"
        : relativePath.Contains('\\') ? $"backslash in '{relativePath}'"
        : relativePath.Split('/').Any(segment => segment is "" or "." or "..")
            ? $"traversal in '{relativePath}'"
        : null;

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
