using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeshWeaver.Mesh;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// Where a module-activation entry came from — the two lanes of #1664 step 9.
/// </summary>
public static class ModuleActivationSources
{
    /// <summary>The deployment's <c>Modules:Assemblies</c> appsettings baseline.</summary>
    public const string AppSettings = "appsettings";

    /// <summary>A Store install that landed the module via <see cref="ModuleLandingService"/>.</summary>
    public const string Store = "store";
}

/// <summary>
/// One activated (or deliberately deactivated) module in the persisted activation list —
/// the durable record that replaces "edit appsettings and redeploy" for store-installed modules
/// (#1664 step 9).
/// </summary>
public sealed record ModuleActivationEntry
{
    /// <summary>The module's DLL name WITHOUT extension (e.g. <c>MeshWeaver.Markdown.Export</c>)
    /// — the same identity <c>MeshBuilder.ResolveModulePath</c> probes <c>modules/&lt;name&gt;/</c>
    /// with.</summary>
    public required string Name { get; init; }

    /// <summary>One of <see cref="ModuleActivationSources"/>. Sidecar entries are written by the
    /// store lane; the appsettings baseline never round-trips through this file.</summary>
    public string Source { get; init; } = ModuleActivationSources.Store;

    /// <summary>The mesh path of the install record (Package node) that landed this module, when
    /// the store lane wrote it — the back-pointer Slice C's funnel uses.</summary>
    public string? PackagePath { get; init; }

    /// <summary>
    /// The GENERATION directory under <c>modules/</c> this entry's bytes live in
    /// (<c>&lt;name&gt;@&lt;id&gt;</c>). Landing writes every version into a FRESH generation and
    /// moves this pointer — nothing on the landing path ever deletes or overwrites a directory a
    /// running pod may hold open, which is what made delete-based swaps unsafe on a shared volume
    /// (2026-08-20: a rolling restart's boot-time applies half-deleted 13 of 15 module closures).
    /// Absent → the legacy fixed folder <c>modules/&lt;name&gt;/</c>. Unreferenced generations are
    /// garbage-collected at boot, skip-on-locked.
    /// </summary>
    public string? Directory { get; init; }

    /// <summary>
    /// The generation this module ran BEFORE <see cref="Directory"/> was landed — the one boot
    /// falls back to when <see cref="Directory"/> cannot load on this platform (#3649, rule R1 of
    /// the module adoption policy: <i>an installation runs the newest generation that LOADS, and
    /// keeps the one it has until a newer one does</i>).
    ///
    /// <para>Set by <see cref="ModuleLandingService"/> from the entry a landing displaces, whenever
    /// a NEW generation lands for a module that already had one. Referenced by the modules GC
    /// exactly like <see cref="Directory"/>, so the generation that loads is never reclaimed while
    /// the one that does not is the entry's head — which is what a shelved landing built for a
    /// newer platform used to do to a Store-only module: overwrite the only reference to its
    /// loadable bytes, and the next GC pass took them away. Cleared by an uninstall together with
    /// <see cref="Directory"/>. Absent = no previous generation is held.</para>
    /// </summary>
    public string? PreviousDirectory { get; init; }

    /// <summary>The package version <see cref="PreviousDirectory"/> was landed at, so a status
    /// row can say "runs v1.2.3; v1.3.0 landed but does not load here". Null when unrecorded.</summary>
    public string? PreviousVersion { get; init; }

    /// <summary>The framework MVID <see cref="PreviousDirectory"/> was built against — diagnostic,
    /// like <see cref="FrameworkMvid"/>.</summary>
    public string? PreviousFrameworkMvid { get; init; }

    /// <summary>The framework MVID (MeshWeaver.Graph's ModuleVersionId) the landed assemblies
    /// were built against, as the producer recorded it — DIAGNOSTIC metadata only: it names the
    /// exact build behind the bytes when something needs debugging, but it is never a gate.
    /// Modules bind by simple name and their contract is API compatibility, expressed by
    /// <see cref="MinMeshVersion"/>; the strict MVID gate is bake semantics and belongs to the
    /// NodeType assembly lane.</summary>
    public string? FrameworkMvid { get; init; }

    /// <summary>The module's declared platform FLOOR (<c>minMeshVersion</c>) as recorded at
    /// landing — ADVISORY since #3648. Boot used to SKIP the entry when the running platform did
    /// not satisfy it; it no longer does (that string comparison held every production portal on
    /// 2026-09-07 while the bytes would have loaded). The floor is worded onto the boot log and
    /// the status surfaces as "declares platform ≥ X; running Y"
    /// (<see cref="ModulePlatformFloor.DeclineReason(string?)"/>) and decides nothing; whether the
    /// entry loads is measured by the link probe in <c>MeshBuilder.InstallAssemblies</c>. Absent =
    /// none declared.</summary>
    public string? MinMeshVersion { get; init; }

    /// <summary>The package version the landed bundle was served at (the module package's released
    /// SemVer). What the auto-update reconcile compares against the registry's bundle index to
    /// decide "already landed" without downloading a byte (<see cref="ModuleUpdateDecision"/>).
    /// Null on an entry written before this field existed — which reads as "unknown", so the next
    /// reconcile re-lands once and records it.</summary>
    public string? Version { get; init; }

    /// <summary>
    /// 🚨 The framework identity of the HEAD generation (<see cref="Directory"/>) when the boot
    /// MEASURED it unloadable on this platform — the link probe refused it, or the load threw —
    /// and fell back to the previous one (#3649) or parked the module. Null when the head loads,
    /// or was never measured: the ordinary state.
    ///
    /// <para>This is the one fact the update reconcile needs to honour rule R3 of
    /// <c>Doc/Architecture/ModuleAdoptionPolicy</c> (#3650): an entry in fallback is
    /// RE-EXAMINED on every reconcile, and an index entry serving the SAME version built against a
    /// DIFFERENT identity than this one is a build for this platform that appeared — it lands.
    /// Without it the same-version branch of <see cref="ModuleUpdateDecision"/> could only compare
    /// against <see cref="FrameworkMvid"/>, and a deployment running its previous generation would
    /// answer "already landed" for exactly the build that would have got it off the fallback.</para>
    ///
    /// <para>🚨 <b>Derived at read time from the sidecar's MARKER file, never stored in the entry
    /// file</b> (<see cref="ModuleActivationSidecar.UnloadableMarkerPath"/>,
    /// <c>activation.d/&lt;Name&gt;.unloadable</c>). The boot that measures the head writes the
    /// marker (unloadable) or deletes it (loaded) — a create and a delete, never a read-modify-write
    /// of the entry a landing on another replica may be replacing at that moment (#2090). The marker
    /// names the generation it measured, and <see cref="ModuleActivationSidecar.Read"/> attaches it
    /// here ONLY while <see cref="Directory"/> is still that generation: a landing that moves the
    /// head on makes a stale marker inert without touching it, and the next boot re-measures. It is
    /// deliberately the boot's measurement and not the module set's adoption record
    /// (<c>ModuleSetIndex.FallbackGenerations</c>): that record is written once per set by the
    /// first replica to adopt it and survives a platform roll unchanged, so it can report a fallback
    /// the image now running no longer takes; every boot rewrites this.</para>
    /// </summary>
    [JsonIgnore]
    public string? UnloadableFrameworkMvid { get; init; }

    /// <summary>False = uninstalled (the record is kept for history/idempotence; the folder is
    /// deleted). Takes effect at the next restart, like every activation change.</summary>
    public bool Enabled { get; init; } = true;
}

/// <summary>
/// The persisted per-deployment module-activation list — the content of the
/// <c>modules/activation.json</c> sidecar (see <see cref="ModuleActivationSidecar"/>).
/// </summary>
public sealed record ModuleActivationList
{
    /// <summary>The activation entries, in landing order.</summary>
    public ImmutableList<ModuleActivationEntry> Entries { get; init; } = [];

    /// <summary>True when an activation change (install/uninstall) has landed since the last
    /// restart — the minimal #1664 step-10 "restart required" signal. Boot consumes it: applying
    /// the list IS the restart, so <c>ConfigureMemexMesh</c> resets it to false.</summary>
    public bool PendingRestart { get; init; }
}

/// <summary>
/// Plain-file persistence of the module-activation list, beside the module folders it describes.
///
/// <para><b>Why a sidecar file and not a mesh node:</b> the list is consumed at BOOT, in
/// <c>ConfigureMemexMesh</c>, BEFORE the DI container exists — before any storage provider is
/// registered, before any hub runs, and (on PG) before a connection string has been validated.
/// A mesh-node read at that point would need a parallel pre-DI storage bootstrap; a file beside
/// the folders it activates needs <c>File.ReadAllText</c>. It also cannot drift from the DLLs:
/// the landing service writes both in the same operation onto the same volume, so a
/// restore/copy of the deployment's file tree carries both or neither.</para>
///
/// <para>🚨 <b>ONE FILE PER MODULE — never one shared mutable index (#2090, #2189).</b> The
/// activation record used to be a single <c>modules/activation.json</c> that every writer
/// read-modify-wrote. On the RWX <c>/data</c> volume every portal replica shares, that single
/// mutable cell has two defects no retry can fix:</para>
/// <list type="number">
///   <item><b>Lost updates.</b> Replica A reads [Y], adds X, writes [Y,X]; replica B concurrently
///     reads [Y], adds Z, writes [Y,Z] — and X is gone, silently. A busy republish (30+ modules
///     after a release) is exactly this shape.</item>
///   <item><b>Replace-in-place races every reader.</b> The write is a rename over the live file.
///     On SMB the server refuses a rename whose target another client holds open
///     (<c>SHARING_VIOLATION</c> → <c>Access to the path '…/activation.json' is denied</c>, the
///     409s of #2090), and a reader whose <c>File.Exists</c> hit the CIFS attribute cache opens
///     into the replace window and gets <c>ENOENT</c> — reported as a CORRUPT sidecar, which
///     collapsed the whole list to empty and booted the pod with NO store modules at all
///     (#2189).</item>
/// </list>
/// <para>So the contended cell is removed rather than guarded: each module owns
/// <c>modules/activation.d/&lt;Name&gt;.json</c> and a writer touches ONLY its own file. Two
/// landings of DIFFERENT modules now share no path at all — no contention, and lost updates are
/// structurally impossible. Two landings of the SAME module are inherently ordered work whose
/// last-writer-wins is the correct answer, and can no longer cost any OTHER module its entry.
/// A per-entry file that cannot be read costs exactly that one entry, reported loudly, instead of
/// the whole deployment's module set.</para>
///
/// <para><b>The legacy aggregate file is still READ, never written by the runtime lane.</b>
/// <c>modules/activation.json</c> is what deployments already on disk carry, so
/// <see cref="Read"/> unions it under the per-module files (a per-module file WINS by name — an
/// uninstall recorded there must beat a stale enabled row in the aggregate). Nothing on the
/// landing path writes it any more, which is what takes the contention to zero.</para>
///
/// <para>All IO here is plain and synchronous by design — boot-time (pre-DI, pre-IoPool) callers
/// use it directly; runtime callers go through <see cref="ModuleLandingService"/>, which runs
/// these on its bounded IO pool.</para>
/// </summary>
public static class ModuleActivationSidecar
{
    /// <summary>The legacy aggregate file's name inside the <c>modules/</c> folder. Read for
    /// deployments that already carry one; never written by the landing lane.</summary>
    public const string FileName = "activation.json";

    /// <summary>The per-module entry directory inside <c>modules/</c>. One file per module,
    /// so concurrent writers of different modules never share a path.</summary>
    public const string EntriesDirectoryName = "activation.d";

    /// <summary>The restart-required marker's file name inside <see cref="EntriesDirectoryName"/>.
    /// A marker rather than a field, for the same reason the entries are split: setting it is a
    /// create and clearing it is a delete, and neither is a read-modify-write of a file some other
    /// replica is reading.</summary>
    public const string PendingRestartMarkerName = ".pending-restart";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The legacy aggregate file's full path for a deployment rooted at
    /// <paramref name="baseDirectory"/> (normally <c>AppContext.BaseDirectory</c>).</summary>
    public static string SidecarPath(string baseDirectory) =>
        Path.Combine(baseDirectory, "modules", FileName);

    /// <summary>The per-module entry directory for a deployment rooted at
    /// <paramref name="baseDirectory"/>.</summary>
    public static string EntriesDirectory(string baseDirectory) =>
        Path.Combine(baseDirectory, "modules", EntriesDirectoryName);

    /// <summary>The file one module's activation entry lives in — the ONLY path a landing of that
    /// module writes.</summary>
    public static string EntryPath(string baseDirectory, string moduleName) =>
        Path.Combine(EntriesDirectory(baseDirectory), moduleName + ".json");

    /// <summary>The restart-required marker's full path.</summary>
    public static string PendingRestartMarkerPath(string baseDirectory) =>
        Path.Combine(EntriesDirectory(baseDirectory), PendingRestartMarkerName);

    /// <summary>
    /// Reads the activation list: the legacy aggregate file unioned with every per-module entry
    /// file, the per-module file winning by name.
    ///
    /// <para>An ABSENT file — either kind — is the normal fresh-deployment state and contributes
    /// nothing, silently. An UNREADABLE one is reported through <paramref name="onCorrupt"/> and
    /// contributes nothing, so the skip is loud rather than silent — but 🚨 it no longer costs the
    /// OTHER entries: one bad file used to collapse the entire answer to the empty list, which is
    /// how a transient SMB read fault booted a pod with none of its store modules (#2189). The
    /// caller still gets everything that WAS readable, plus one report per file that was not.</para>
    /// </summary>
    public static ModuleActivationList Read(string baseDirectory, Action<string>? onCorrupt = null)
    {
        var legacy = ReadLegacy(baseDirectory, onCorrupt);
        var byName = new Dictionary<string, ModuleActivationEntry>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();

        void Accept(ModuleActivationEntry entry)
        {
            if (string.IsNullOrWhiteSpace(entry.Name))
                return;
            if (!byName.ContainsKey(entry.Name))
                order.Add(entry.Name);
            byName[entry.Name] = entry;
        }

        foreach (var entry in legacy.Entries)
            Accept(entry);
        // Per-module files last: a record written by the landing lane WINS over whatever the
        // frozen aggregate still says about that name (an uninstall must beat a stale enabled row).
        // Sorted so the union is deterministic regardless of directory-enumeration order.
        foreach (var entry in ReadEntryFiles(baseDirectory, onCorrupt))
            Accept(entry);

        return new ModuleActivationList
        {
            // #3650 — the boot's measurement of each head generation rides along, from the
            // per-module marker file, only while the head is still the generation it measured.
            Entries = [.. order.Select(name => WithUnloadableMarker(baseDirectory, byName[name]))],
            // The marker is authoritative; the legacy flag is honoured once, for a deployment
            // upgrading with the flag still set. Boot clears both.
            PendingRestart = File.Exists(PendingRestartMarkerPath(baseDirectory)) || legacy.PendingRestart,
        };
    }

    // ── the unloadable-head marker (#3650) ──────────────────────────────────

    /// <summary>The per-module marker's file suffix inside <see cref="EntriesDirectoryName"/> —
    /// deliberately NOT <c>.json</c>, so <see cref="Read"/>'s entry enumeration never parses a
    /// marker as an entry and the bulk <see cref="Write"/> never sweeps one.</summary>
    public const string UnloadableMarkerSuffix = ".unloadable";

    /// <summary>The marker file recording that a module's head generation was measured unloadable
    /// on this platform: <c>modules/activation.d/&lt;Name&gt;.unloadable</c>.</summary>
    public static string UnloadableMarkerPath(string baseDirectory, string moduleName) =>
        Path.Combine(EntriesDirectory(baseDirectory), moduleName + UnloadableMarkerSuffix);

    /// <summary>
    /// What the boot measured about one module's head generation (#3650): the generation it
    /// tried, the framework identity that generation was recorded with, why it did not load, and
    /// when. The generation is what keeps the marker honest across a landing — a marker for a
    /// generation the entry no longer heads is inert.
    /// </summary>
    /// <param name="Generation">The generation directory leaf the boot measured (<c>&lt;name&gt;@&lt;id&gt;</c>).</param>
    /// <param name="FrameworkMvid">That generation's recorded framework identity, or null when unrecorded.</param>
    /// <param name="Reason">Why it did not load — the link probe's report or the load exception.</param>
    /// <param name="MeasuredAt">When the boot measured it.</param>
    public sealed record UnloadableGeneration(
        string Generation, string? FrameworkMvid, string? Reason, DateTimeOffset MeasuredAt);

    /// <summary>
    /// Records that <paramref name="generation"/> of <paramref name="moduleName"/> was measured
    /// unloadable on this platform — an atomic replace of that module's own marker file, which
    /// only boots write and which no landing ever reads or modifies. Idempotent.
    /// </summary>
    public static void SetUnloadable(
        string baseDirectory, string moduleName, string generation, string? frameworkMvid, string? reason)
    {
        ValidateModuleName(moduleName);
        if (string.IsNullOrWhiteSpace(generation))
            throw new ArgumentException("the measured generation is required", nameof(generation));
        WriteAtomic(UnloadableMarkerPath(baseDirectory, moduleName), JsonSerializer.Serialize(
            new UnloadableGeneration(generation, frameworkMvid, reason, DateTimeOffset.UtcNow), Json));
    }

    /// <summary>Removes the module's marker — the boot loaded its head generation, or the module
    /// was uninstalled. A delete, tolerant of an absent file and of a volume that is momentarily
    /// read-only: the marker is a measurement, and activation never depends on it.</summary>
    public static void ClearUnloadable(string baseDirectory, string moduleName)
    {
        ValidateModuleName(moduleName);
        try
        {
            File.Delete(UnloadableMarkerPath(baseDirectory, moduleName));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Another replica clearing the same marker, or a read-only volume; the next boot
            // measures again.
        }
    }

    /// <summary>
    /// The module's marker as written, or null when there is none — or when it cannot be read or
    /// parsed. Silent on purpose: the marker is a hint the boot rewrites, and a read that opens
    /// into another replica's atomic replace (#2189's shape) must cost nothing but that one
    /// reading, never a false "corrupt sidecar".
    /// </summary>
    public static UnloadableGeneration? ReadUnloadable(string baseDirectory, string moduleName)
    {
        if (string.IsNullOrWhiteSpace(moduleName))
            return null;
        try
        {
            var text = TryReadAllText(UnloadableMarkerPath(baseDirectory, moduleName));
            return text is null ? null : JsonSerializer.Deserialize<UnloadableGeneration>(text, Json);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Attaches the marker's identity to <paramref name="entry"/> when — and only when —
    /// the marker measured the generation the entry currently heads.</summary>
    private static ModuleActivationEntry WithUnloadableMarker(string baseDirectory, ModuleActivationEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Directory))
            return entry;
        var marker = ReadUnloadable(baseDirectory, entry.Name);
        if (marker is null
            || !string.Equals(marker.Generation, entry.Directory, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(marker.FrameworkMvid))
            return entry;
        return entry with { UnloadableFrameworkMvid = marker.FrameworkMvid };
    }

    /// <summary>The same rule <see cref="WriteEntry"/> applies: the name BECOMES a path.</summary>
    private static void ValidateModuleName(string? moduleName)
    {
        if (string.IsNullOrWhiteSpace(moduleName)
            || moduleName is "." or ".."
            || moduleName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || moduleName.Contains('/') || moduleName.Contains('\\'))
            throw new ArgumentException(
                $"'{moduleName}' is not a valid module name — a marker is a file named after its module.",
                nameof(moduleName));
    }

    /// <summary>
    /// Records ONE module's activation entry — the only write the landing lane performs, and the
    /// reason concurrent landings of different modules cannot contend or lose each other's work.
    /// Atomic: serialized to a temp file in the same directory, then renamed into place.
    /// </summary>
    public static void WriteEntry(string baseDirectory, ModuleActivationEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        // 🚨 The name BECOMES a path here, so it is validated here — not only in the landing
        // service that happens to be today's caller. A record file is derived from the module
        // name, and a name carrying a separator or '..' would write wherever it points.
        if (string.IsNullOrWhiteSpace(entry.Name)
            || entry.Name is "." or ".."
            || entry.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || entry.Name.Contains('/') || entry.Name.Contains('\\'))
            throw new ArgumentException(
                $"'{entry.Name}' is not a valid module name — an activation record is a file named "
                + "after its module.", nameof(entry));
        WriteAtomic(EntryPath(baseDirectory, entry.Name), JsonSerializer.Serialize(entry, Json));
    }

    /// <summary>
    /// Raises or clears the deployment's restart-required marker. A create and a delete — never a
    /// read-modify-write — so it adds no contention of its own. Clearing a marker that is already
    /// gone is a no-op, which is what makes several replicas booting at once benign.
    /// </summary>
    public static void SetPendingRestart(string baseDirectory, bool pending)
    {
        var marker = PendingRestartMarkerPath(baseDirectory);
        if (pending)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            // Create-if-absent. Never a rewrite: a second replica raising the same marker must not
            // rename over a file the first is holding.
            if (!File.Exists(marker))
                File.Open(marker, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite).Dispose();
            return;
        }

        try
        {
            File.Delete(marker);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Another replica is clearing the same marker, or the volume is momentarily read-only.
            // The flag is a hint; activation itself never depends on it.
        }

        // One-time carry-over: a deployment upgrading with the flag still set in the frozen
        // aggregate would otherwise read PendingRestart forever. Rewritten only while it is
        // actually set, so this converges after the first boot and never becomes a hot write.
        var legacyPath = SidecarPath(baseDirectory);
        if (!File.Exists(legacyPath))
            return;
        var legacy = ReadLegacy(baseDirectory, null);
        if (!legacy.PendingRestart)
            return;
        try
        {
            WriteAtomic(legacyPath, JsonSerializer.Serialize(legacy with { PendingRestart = false }, Json));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best effort, exactly as before: on a read-only /app the flag simply stays set.
        }
    }

    /// <summary>
    /// Writes the WHOLE list — the administrative/bulk form, used to seed or rewrite a
    /// deployment's activation state wholesale (and by tests to arrange one).
    ///
    /// <para>🚨 Not the landing lane's write: this rewrites every module's record, which is
    /// precisely the read-modify-write of shared state that #2090 was. Runtime callers use
    /// <see cref="WriteEntry"/>. Bulk means bulk — the per-module directory is made to match the
    /// list exactly, so an entry dropped from the list is dropped from disk.</para>
    /// </summary>
    public static void Write(string baseDirectory, ModuleActivationList list)
    {
        ArgumentNullException.ThrowIfNull(list);
        var entries = EntriesDirectory(baseDirectory);
        Directory.CreateDirectory(entries);

        var keep = list.Entries
            .Where(e => !string.IsNullOrWhiteSpace(e.Name))
            .ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var file in Directory.EnumerateFiles(entries, "*.json").ToArray())
            if (!keep.ContainsKey(Path.GetFileNameWithoutExtension(file)))
                File.Delete(file);

        foreach (var entry in keep.Values)
            WriteEntry(baseDirectory, entry);

        // The aggregate is superseded by what was just written per module. Leaving a stale copy
        // behind would resurrect entries this call removed.
        var legacyPath = SidecarPath(baseDirectory);
        if (File.Exists(legacyPath))
            File.Delete(legacyPath);

        SetPendingRestart(baseDirectory, list.PendingRestart);
    }

    private static ModuleActivationList ReadLegacy(string baseDirectory, Action<string>? onCorrupt)
    {
        var path = SidecarPath(baseDirectory);
        try
        {
            // 🚨 The READ is inside the try, not before it. Only genuine ABSENCE is silent
            // (TryReadAllText); every other failure — an SMB sharing violation or lease conflict
            // arriving as IOException/UnauthorizedAccessException just as much as a parse error —
            // must be REPORTED and skipped, never allowed to escape. Boot calls this un-wrapped, so
            // an escaping exception would take the portal down over a transient volume blip: worse
            // than the silence this whole change exists to remove.
            var text = TryReadAllText(path);
            if (text is null)
                return new ModuleActivationList();
            return JsonSerializer.Deserialize<ModuleActivationList>(text, Json)
                   ?? new ModuleActivationList();
        }
        catch (Exception ex)
        {
            onCorrupt?.Invoke(
                $"Module activation sidecar '{path}' could not be read ({ex.GetType().Name}: "
                + $"{ex.Message}) — the entries it holds are skipped. Store-installed modules "
                + "recorded there will NOT load until the file is repaired or the modules are "
                + "re-installed.");
            return new ModuleActivationList();
        }
    }

    private static IEnumerable<ModuleActivationEntry> ReadEntryFiles(
        string baseDirectory, Action<string>? onCorrupt)
    {
        var directory = EntriesDirectory(baseDirectory);
        string[] files;
        try
        {
            files = Directory.Exists(directory)
                ? [.. Directory.EnumerateFiles(directory, "*.json").OrderBy(f => f, StringComparer.Ordinal)]
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            onCorrupt?.Invoke(
                $"Module activation entries under '{directory}' could not be listed "
                + $"({ex.GetType().Name}: {ex.Message}) — store-installed modules will NOT load "
                + "until the volume is readable again.");
            yield break;
        }

        foreach (var file in files)
        {
            ModuleActivationEntry? entry;
            try
            {
                var text = TryReadAllText(file);
                if (text is null)
                    // Vanished between the listing and the read — another replica re-landing that
                    // very module. Its next read sees the new file; nothing else is affected, which
                    // is the whole point of one file per module.
                    continue;
                entry = JsonSerializer.Deserialize<ModuleActivationEntry>(text, Json);
            }
            // 🚨 EVERY failure, not only a parse error. An SMB sharing violation or lease conflict
            // arrives as IOException/UnauthorizedAccessException, and letting it escape would fail
            // the WHOLE activation read — restoring the exact all-or-nothing behaviour this change
            // removes, and crashing boot, which calls Read un-wrapped. It costs the one module it
            // names, loudly. Deliberately NOT retried: the contended window is now a single
            // module's own record being replaced, and a retry loop here would be a band-aid over a
            // condition the next boot resolves on its own.
            catch (Exception ex)
            {
                onCorrupt?.Invoke(
                    $"Module activation entry '{file}' could not be read ({ex.GetType().Name}: "
                    + $"{ex.Message}) — that ONE module is skipped; re-install it to repair the "
                    + "entry. Every other activation entry is unaffected.");
                continue;
            }
            if (entry is not null && !string.IsNullOrWhiteSpace(entry.Name))
                yield return entry;
        }
    }

    /// <summary>Reads a file, answering null for "not there" — including the ENOENT a concurrent
    /// rename produces on SMB, which is genuinely absence and never corruption.</summary>
    private static string? TryReadAllText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static void WriteAtomic(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, content);
        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>
/// One module the boot union activates, with the LANE it came from.
/// </summary>
/// <param name="Entry">The <c>Modules:Assemblies</c>-shaped entry (<c>&lt;Name&gt;.dll</c> for a
/// sidecar entry; the operator's raw value for a baseline one).</param>
/// <param name="Landed">The sidecar entry this came from, or <c>null</c> when it came from the
/// appsettings baseline. 🚨 Provenance is carried rather than re-derived: a landed module's bytes
/// live in the directory ITS entry points at, a baseline module's are resolved by probing, and a
/// caller that guesses which lane a name belongs to is how the boot gate and the boot loader came
/// to disagree (#1949).</param>
public sealed record EffectiveModule(string Entry, ModuleActivationEntry? Landed);

/// <summary>
/// The boot-time union of #1664 step 9: appsettings baseline ∪ enabled persisted store installs,
/// deduped by module name, with the one skip rule (missing DLL) applied loudly and the declared
/// platform floor announced as an advisory (#3648) — extracted PURE so the computation is
/// unit-testable without booting a portal.
/// </summary>
public static class ModuleActivationBoot
{
    /// <summary>
    /// Rewrites the activation list onto THE module set the mesh runs (#3395) — the convergence
    /// step, applied before <see cref="ComputeEffectiveModuleEntries(IReadOnlyList{string}, ModuleActivationList, Func{string, string}, Func{ModuleActivationEntry, bool}, Action{string, string}, Action{string, string})"/> so the union it computes is
    /// the mesh's set rather than this process's own snapshot of a moving record.
    ///
    /// <para>🚨 <b>Why the raw record is not what a boot should load.</b> Landing moves each
    /// module's entry the instant that module's bytes are on disk, so the record is a MOVING
    /// TARGET: two replicas booting seconds apart read two different answers, and a replica
    /// booting mid-wave reads a TORN one — some modules of the new wave, the rest of the old — a
    /// combination no wave ever intended and nothing ever tested. Every replica pinning its own
    /// answer is how three pods of one ReplicaSet came to differ in 39 of 40 module generations
    /// (memex-cloud, 2026-09-06). The set is proposed ONCE per completed wave
    /// (<see cref="ModuleSetStore.Propose"/>), so every boot between two waves converges on the
    /// same bytes.</para>
    ///
    /// <para>Three rules, and each one is a deliberate refusal to guess:</para>
    /// <list type="bullet">
    ///   <item>An enabled entry whose module the set names loads the SET's generation, even when
    ///     the entry has since moved past it. That is the convergence.</item>
    ///   <item>An enabled entry the set does NOT name is LANDED BUT UNPROPOSED — a wave that has
    ///     not completed (or died half-landed). It is deferred, loudly, and activates when the
    ///     wave proposes. Adopting it would be exactly the independent per-replica pin this
    ///     exists to remove, and adopting HALF a wave is the torn set.</item>
    ///   <item>A DISABLED entry passes through untouched. An uninstall deletes the folder, so
    ///     honouring it is not optional and never waits for a set. So does an entry with NO
    ///     GENERATION — the legacy fixed <c>modules/&lt;name&gt;/</c> folder, which
    ///     <see cref="ModuleSetStore.GenerationsOf"/> excludes by design because there is nothing
    ///     to pin; deferring it would silently disable it.</item>
    ///   <item>The entry's <see cref="ModuleActivationEntry.PreviousDirectory"/> — the generation
    ///     boot falls back to when the head one does not load here (#3649) — travels with it onto
    ///     the set's generation, and is dropped only when it names that very generation.</item>
    /// </list>
    ///
    /// <para>A null <paramref name="meshSet"/> — a deployment on which no landing wave has ever
    /// completed, which is every deployment until the first wave after this change — returns the
    /// list UNCHANGED. Pre-#3395 behaviour, byte for byte, is the migration path.</para>
    ///
    /// <para>🚨 <b>One degradation, loud and deliberate: a set generation whose BYTES ARE GONE.</b>
    /// The set can only pin what is on the volume, and the GC that could take it away is fixed in
    /// the same change (the modules GC now counts the set's
    /// generations as referenced). What remains is what no rule here controls — a pod still running
    /// the PREVIOUS platform build sweeping by the entries alone during this change's own rollout, a
    /// manual deletion, a partial volume restore. In that state the two candidates are "run the
    /// generation the entry names" and "run nothing", and running nothing is the WORSE half of the
    /// very defect this exists to fix: a missing module is what turns a healthy NodeType into a
    /// failed one. So it falls back to the entry, reports it through
    /// <paramref name="onDeferred"/>, and the next completed wave re-proposes a set whose bytes
    /// exist. It is a REPORTED degradation, never a silent one, and it is unreachable once every
    /// replica sweeps with this build.</para>
    /// </summary>
    /// <param name="landed">The activation record as <see cref="ModuleActivationSidecar.Read"/>
    /// answered it.</param>
    /// <param name="meshSet">The mesh's module set — <see cref="ModuleSetIndex.Proposed"/>.</param>
    /// <param name="onDeferred">The loud channel for a landed-but-UNPROPOSED entry: (module name,
    /// reason). Exactly one meaning, so a surface can render it as one state.</param>
    /// <param name="landedDllExists">Whether an entry's landed DLL is on the volume — production
    /// passes <see cref="LandedModuleDllExists"/>, the SAME check boot's own gate applies. Null
    /// skips the check entirely, which is the pure form this stays testable in.</param>
    /// <param name="onSetGenerationMissing">The loud channel for the degradation above — the set's
    /// generation is not on the volume and the entry's is used instead: (module name, reason).
    /// SEPARATE from <paramref name="onDeferred"/> deliberately: that one means "running on no
    /// replica", and this module IS running, just possibly not what the set says.</param>
    public static ModuleActivationList ProjectOntoMeshSet(
        ModuleActivationList landed,
        ModuleSet? meshSet,
        Action<string, string>? onDeferred = null,
        Func<ModuleActivationEntry, bool>? landedDllExists = null,
        Action<string, string>? onSetGenerationMissing = null)
    {
        ArgumentNullException.ThrowIfNull(landed);
        if (meshSet is null)
            return landed;

        var projected = ImmutableList.CreateBuilder<ModuleActivationEntry>();
        foreach (var entry in landed.Entries)
        {
            // 🚨 An entry with NO GENERATION is the legacy fixed folder `modules/<name>/`, and a set
            // has nothing to say about it: `ModuleSetStore.GenerationsOf` excludes it BY DESIGN
            // (there is no `<name>@<id>` to pin), so asking whether the set names it would defer —
            // i.e. silently DISABLE — every such module on every deployment that still has one.
            // It passes through for the same reason a disabled entry does: this projection chooses
            // between generations, and where there are none to choose between it must not choose.
            if (!entry.Enabled
                || string.IsNullOrWhiteSpace(entry.Name)
                || string.IsNullOrWhiteSpace(entry.Directory))
            {
                projected.Add(entry);
                continue;
            }

            if (meshSet.Generations.TryGetValue(entry.Name, out var generation))
            {
                if (string.Equals(entry.Directory, generation, StringComparison.Ordinal))
                {
                    projected.Add(entry);
                    continue;
                }

                // 🚨 #3649 — the fallback pointer travels WITH the entry onto the set's generation,
                // and is dropped only when it names that very generation (the common mid-wave
                // shape: the entry moved to D with PreviousDirectory = the set's G, so G is the
                // head now and needs no fallback to itself). Where it names an OLDER generation
                // than the set's — a landing that carried its fallback forward because the
                // displaced generation was measured unloadable — that older one is exactly what
                // boot must fall back to if the set's generation does not load here either.
                var previous = string.Equals(entry.PreviousDirectory, generation, StringComparison.Ordinal)
                    ? entry with { PreviousDirectory = null, PreviousVersion = null, PreviousFrameworkMvid = null }
                    : entry;
                var onSet = previous with { Directory = generation };
                if (landedDllExists is null || landedDllExists(onSet))
                {
                    projected.Add(onSet);
                    continue;
                }

                onSetGenerationMissing?.Invoke(entry.Name,
                    $"the mesh's module set {meshSet.Sequence} ('{meshSet.Id}') pins generation "
                    + $"'{generation}', whose bytes are NOT on the volume — running '{entry.Directory}' "
                    + "instead, which may differ from what other replicas run until the next landing "
                    + "wave proposes a set whose bytes exist. A module that is simply ABSENT is the "
                    + "worse half of #3395: it is what turns a healthy NodeType into a failed one.");
                projected.Add(entry);
                continue;
            }

            // 🚨 Not a skip we can be quiet about: the module IS installed and its bytes ARE on
            // disk. What is missing is the wave's completion, and until that lands, activating it
            // here would put this replica on a set no other replica has.
            onDeferred?.Invoke(entry.Name,
                $"it landed after module set {meshSet.Sequence} ('{meshSet.Id}') was proposed, so "
                + "the landing wave that brought it has not completed — it activates on the first "
                + "restart after the wave proposes its set. Running it here would put this replica "
                + "on a module set no other replica has (#3395).");
        }

        return landed with { Entries = projected.ToImmutable() };
    }

    /// <summary>
    /// The pre-#3648 shape, kept as a real overload so a host or module compiled against the
    /// previous platform still binds (an optional parameter is a compile-time default, not a
    /// binary one: dropping the five-argument method would surface as MissingMethodException at
    /// the caller's first boot). Forwards with no advisory channel.
    /// </summary>
    public static ImmutableList<EffectiveModule> ComputeEffectiveModuleEntries(
        IReadOnlyList<string>? baselineEntries,
        ModuleActivationList? persisted,
        Func<string?, string?> platformGate,
        Func<ModuleActivationEntry, bool> landedModuleDllExists,
        Action<string, string>? onSkipped) =>
        ComputeEffectiveModuleEntries(baselineEntries, persisted, platformGate, landedModuleDllExists,
            onSkipped, onAdvisory: null);

    /// <summary>
    /// Computes the effective module list the boot loader feeds to
    /// <c>MeshBuilder.InstallAssemblies</c> (after per-module <see cref="ResolveLoadPath"/>).
    ///
    /// <para>Each result carries its PROVENANCE — the sidecar entry it came from, or null for an
    /// appsettings-baseline entry — because the two lanes resolve to a file by different rules and
    /// the caller must not re-derive which lane a name belongs to. That re-derivation is what
    /// #1949 was: the existence gate and the load-path resolver each decided for themselves where
    /// a module's bytes were, disagreed about generation directories, and every store module on
    /// the deployment was skipped at boot while its bytes sat correctly on disk.</para>
    ///
    /// <para>Rules, in order:</para>
    /// <list type="bullet">
    ///   <item>The appsettings <paramref name="baselineEntries"/> pass through UNCHANGED, in
    ///     order — they are the image's own closure and keep today's contract (a baseline entry
    ///     that fails to load fails loudly at startup; the skip rules below are for persisted
    ///     entries only).</item>
    ///   <item>Each ENABLED persisted entry appends as <c>&lt;Name&gt;.dll</c> unless: it
    ///     duplicates a baseline (or earlier persisted) module name — dedupe, silent, the module
    ///     is simply already activated; or its LANDED DLL is missing per
    ///     <paramref name="landedModuleDllExists"/> (a lost volume / manual deletion — SKIPPED
    ///     loudly; a same-named app-closure DLL does not count). A skip is never a crash: the
    ///     deployment must boot. A landed module's MVID is deliberately NOT a skip condition —
    ///     modules bind by simple name across platform builds; the recorded MVID is diagnostic.
    ///     🚨 Nor is its recorded <see cref="ModuleActivationEntry.MinMeshVersion"/> floor, since
    ///     #3648: an entry whose declared floor ranks above the running platform is handed to the
    ///     loader like any other, with the claim ANNOUNCED through <paramref name="onAdvisory"/>;
    ///     whether it loads is measured by the link probe in <c>MeshBuilder.InstallAssemblies</c>.
    ///     (It used to be SKIPPED on that string, which is how every production portal was held on
    ///     the morning build for all of 2026-09-07.)</item>
    ///   <item>Disabled entries (uninstalled) contribute nothing and report nothing.</item>
    /// </list>
    /// </summary>
    /// <param name="baselineEntries">The raw <c>Modules:Assemblies</c> values (may be null/empty).</param>
    /// <param name="persisted">The sidecar list (may be null).</param>
    /// <param name="platformGate">Words the declared-floor ADVISORY — the sentence naming both
    /// versions when an entry's recorded floor ranks above the running platform, or null;
    /// production passes <see cref="ModulePlatformFloor.DeclineReason(string?)"/> so there is never
    /// a second wording. 🚨 Its answer skips NOTHING (#3648): it is delivered through
    /// <paramref name="onAdvisory"/> for the entries that DO become effective. The parameter keeps
    /// its position so callers compiled against the previous platform keep binding.</param>
    /// <param name="landedModuleDllExists">Whether a persisted module's LANDED entry DLL exists —
    /// called with the ENTRY, never with the bare name, because the entry is what says WHERE its
    /// bytes are: <see cref="ModuleActivationEntry.Directory"/> names the generation directory
    /// landing wrote them into. 🚨 It must check that ONE directory
    /// (<c>modules/&lt;entry.Directory ?? name&gt;/&lt;name&gt;.dll</c> — production passes
    /// <see cref="LandedModuleDllExists"/>), never <c>MeshBuilder.ResolveModulePath</c>: that
    /// resolver falls back to the app's base directory, so a sidecar entry whose landed folder is
    /// gone (tampered sidecar, deleted folder) would silently BIND a same-named app-closure DLL
    /// instead of being skipped. A store-installed module never legitimately lives in the app
    /// closure — a name collision there is exactly what <see cref="ModuleLandingService"/> refuses
    /// at landing.</param>
    /// <param name="onSkipped">The loud channel: called once per skipped persisted entry with
    /// (module name, reason).</param>
    /// <param name="onAdvisory">The advisory channel (#3648): called once per EFFECTIVE persisted
    /// entry whose declared floor <paramref name="platformGate"/> words as above the running
    /// platform, with (module name, the sentence). Never for a skipped entry — the skip line
    /// already names it — and never a reason to skip.</param>
    public static ImmutableList<EffectiveModule> ComputeEffectiveModuleEntries(
        IReadOnlyList<string>? baselineEntries,
        ModuleActivationList? persisted,
        Func<string?, string?> platformGate,
        Func<ModuleActivationEntry, bool> landedModuleDllExists,
        Action<string, string>? onSkipped = null,
        Action<string, string>? onAdvisory = null)
    {
        var effective = ImmutableList.CreateBuilder<EffectiveModule>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ── Pass 1: which enabled persisted entries are USABLE ──────────────────────────────────
        // A usable one OVERRIDES a same-named baseline entry (#2548); an unusable one must not,
        // because removing the baseline would turn a rejected upgrade into a missing module — a
        // strictly worse outcome than running the older copy.
        //
        // 🚨 The gates now run for an entry that shadows a baseline name, which they did not before:
        // the old code deduped such an entry away BEFORE reaching them, so a store-installed module
        // that could not load was silently indistinguishable from one that was never installed.
        // Reporting it is the point — the operator needs to know the registry copy was refused.
        var overrides = new Dictionary<string, ModuleActivationEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var module in persisted?.Entries ?? [])
        {
            if (!module.Enabled || string.IsNullOrWhiteSpace(module.Name))
                continue;
            if (overrides.ContainsKey(module.Name))
                continue; // first ENABLED entry of a name wins, as it always has

            // 🚨 #3648 — no floor skip. `if (platformGate(module.MinMeshVersion) is { } reason)
            // { onSkipped(...); continue; }` stood here, and it is the line that kept every
            // rc-floored module off every ci-built portal for all of 2026-09-07. The declared
            // floor is announced below for the entries that become effective; whether they LOAD
            // is measured by the link probe in MeshBuilder.InstallAssemblies, on the bytes.

            if (!landedModuleDllExists(module))
            {
                onSkipped?.Invoke(module.Name,
                    $"its landed DLL '{RelativeLandedDll(module)}' does not exist "
                    + "(folder lost or never landed; a same-named app-closure DLL deliberately "
                    + "does NOT satisfy a store-installed entry) — re-install the module");
                continue;
            }

            overrides[module.Name] = module;
            if (platformGate(module.MinMeshVersion) is { } advisory)
                onAdvisory?.Invoke(module.Name, advisory);
        }

        // ── Pass 2: the baseline, in its own order, with a usable override substituted IN PLACE ──
        // Order is preserved deliberately: when nothing overrides, the emitted list is byte-for-byte
        // what it was before this change, so the only behaviour that moves is the one #2548 is about.
        foreach (var entry in baselineEntries ?? [])
        {
            if (string.IsNullOrWhiteSpace(entry))
                continue;
            var name = Path.GetFileNameWithoutExtension(entry);
            if (!seen.Add(name))
                continue; // a baseline that repeats a name is still deduped

            effective.Add(overrides.TryGetValue(name, out var winner)
                ? new EffectiveModule(winner.Name + ".dll", winner)
                : new EffectiveModule(entry, Landed: null));
        }

        // ── Pass 3: usable persisted entries with no baseline counterpart, in persisted order ────
        foreach (var module in persisted?.Entries ?? [])
        {
            if (!module.Enabled || string.IsNullOrWhiteSpace(module.Name))
                continue;
            if (!overrides.TryGetValue(module.Name, out var winner) || !ReferenceEquals(winner, module))
                continue; // not the winning entry for this name, or not usable at all
            if (!seen.Add(module.Name))
                continue; // already emitted in pass 2, substituted into the baseline's slot

            effective.Add(new EffectiveModule(module.Name + ".dll", module));
        }

        return effective.ToImmutable();
    }

    /// <summary>
    /// The landed entry-DLL path of a store-installed entry:
    /// <c>{baseDirectory}/modules/{entry.Directory ?? entry.Name}/{entry.Name}.dll</c>.
    ///
    /// <para>🚨 The directory comes from the ENTRY, through the ONE resolution rule
    /// (<see cref="ModuleLandingService.ModuleDirectoryFor"/>) the serve side and the boot loader
    /// already share. Landing writes every version into a FRESH generation
    /// (<c>modules/&lt;name&gt;@&lt;id&gt;/</c>) and moves this pointer; a check that looked in the
    /// legacy fixed <c>modules/&lt;name&gt;/</c> folder would find NOTHING for any
    /// generation-landed module — which is exactly how every store module on a
    /// generation-landing build came up skipped at boot while its bytes sat correctly on disk
    /// (#1949). The gate and the resolver must name ONE path or landing and activation never
    /// converge.</para>
    /// </summary>
    public static string LandedDllPath(string baseDirectory, ModuleActivationEntry entry) =>
        Path.Combine(
            ModuleLandingService.ModuleDirectoryFor(baseDirectory, entry.Name, entry),
            entry.Name + ".dll");

    /// <summary>
    /// The PRODUCTION existence check for a persisted (store-installed) entry — the landed DLL at
    /// <see cref="LandedDllPath"/>, and nothing else. 🚨 Deliberately NOT
    /// <c>MeshBuilder.ResolveModulePath</c>: that resolver falls back to the app's base directory,
    /// which is correct for appsettings-baseline entries (both locations are legitimate for them)
    /// but would let a sidecar entry whose landed folder is gone silently bind a same-named
    /// app-closure DLL instead of being skipped — a store-installed module never legitimately
    /// lives in the app closure (the landing service refuses that collision).
    /// </summary>
    public static bool LandedModuleDllExists(string baseDirectory, ModuleActivationEntry entry) =>
        File.Exists(LandedDllPath(baseDirectory, entry));

    /// <summary>
    /// The PREVIOUS generation of <paramref name="entry"/> as an entry of its own — the same name,
    /// with <see cref="ModuleActivationEntry.Directory"/>, <see cref="ModuleActivationEntry.Version"/>
    /// and <see cref="ModuleActivationEntry.FrameworkMvid"/> taken from the <c>Previous*</c>
    /// fields and no previous of its own — or null when the entry holds none (#3649).
    ///
    /// <para>One derivation, so every reader of the fallback (the boot loader pinning it, the GC
    /// referencing it, the landing carrying it forward, the existence check) resolves it through
    /// the SAME rule <see cref="LandedDllPath"/> applies to the head generation. Re-deriving the
    /// path per caller is how the gate and the loader came to disagree in #1949.</para>
    /// </summary>
    public static ModuleActivationEntry? PreviousGeneration(ModuleActivationEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (string.IsNullOrWhiteSpace(entry.PreviousDirectory)
            || string.Equals(entry.PreviousDirectory, entry.Directory, StringComparison.Ordinal))
            return null;
        return entry with
        {
            Directory = entry.PreviousDirectory,
            Version = entry.PreviousVersion,
            FrameworkMvid = entry.PreviousFrameworkMvid,
            PreviousDirectory = null,
            PreviousVersion = null,
            PreviousFrameworkMvid = null,
        };
    }

    // ── what the boot measured, written where the reconcile reads it (#3650) ─────────────

    /// <summary>
    /// The boot's verdict on one module's head generation: what the loader was handed
    /// (<paramref name="Generation"/>, the entry's <see cref="ModuleActivationEntry.Directory"/>
    /// as projected onto the mesh's set) and whether it loaded.
    /// </summary>
    /// <param name="Name">The module's simple name.</param>
    /// <param name="Generation">The generation the loader tried.</param>
    /// <param name="FrameworkMvid">That generation's recorded framework identity, or null.</param>
    /// <param name="Unloadable">True when the loader could not load it — it fell back to the
    /// previous generation (<see cref="FallbackModule"/>) or parked the module
    /// (<see cref="IncompatibleModule"/>).</param>
    /// <param name="Reason">Why, when unloadable.</param>
    public sealed record MeasuredLoadability(
        string Name, string Generation, string? FrameworkMvid, bool Unloadable, string? Reason);

    /// <summary>
    /// Reads the loader's records back onto the entries it was handed (#3650): for every enabled
    /// store entry in <paramref name="tried"/> — the union AFTER
    /// <see cref="ProjectOntoMeshSet"/>, i.e. with <see cref="ModuleActivationEntry.Directory"/>
    /// naming the generation the loader actually tried — a <see cref="FallbackModule"/> or an
    /// <see cref="IncompatibleModule"/> whose generation IS that directory says the head did not
    /// load; the absence of both says it did. A record naming another generation of the same
    /// module says nothing about this one. Pure.
    /// </summary>
    public static ImmutableList<MeasuredLoadability> MeasureLoadability(
        IEnumerable<ModuleActivationEntry> tried,
        IEnumerable<FallbackModule> fallbacks,
        IEnumerable<IncompatibleModule> incompatible)
    {
        ArgumentNullException.ThrowIfNull(tried);
        var fellBack = fallbacks.ToArray();
        var parked = incompatible.ToArray();
        var verdicts = ImmutableList.CreateBuilder<MeasuredLoadability>();
        foreach (var entry in tried)
        {
            if (entry is not { Enabled: true } || string.IsNullOrWhiteSpace(entry.Name)
                || string.IsNullOrWhiteSpace(entry.Directory))
                continue;
            var fallback = fellBack.FirstOrDefault(f =>
                string.Equals(f.Name, entry.Name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(f.Generation, entry.Directory, StringComparison.Ordinal));
            var refused = fallback is null
                ? parked.FirstOrDefault(m =>
                    string.Equals(m.Name, entry.Name, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(GenerationOf(m.Entry), entry.Directory, StringComparison.Ordinal))
                : null;
            verdicts.Add(new MeasuredLoadability(
                entry.Name, entry.Directory!, entry.FrameworkMvid,
                Unloadable: fallback is not null || refused is not null,
                Reason: fallback?.Reason ?? refused?.Error));
        }
        return verdicts.ToImmutable();
    }

    /// <summary>
    /// Writes the boot's measurement where the reconcile reads it: the marker
    /// (<see cref="ModuleActivationSidecar.SetUnloadable"/>) for every head generation that did
    /// not load, cleared (<see cref="ModuleActivationSidecar.ClearUnloadable"/>) for every one
    /// that did — touching only markers whose content CHANGES, so a steady state costs no writes
    /// and two replicas booting together write the same bytes or nothing. Best-effort per module:
    /// a marker that cannot be written is reported through <paramref name="onReport"/> and the
    /// next boot tries again; nothing here can fail a boot.
    /// </summary>
    /// <returns>The transitions this boot made — a marker written or cleared — for the log.</returns>
    public static ImmutableList<MeasuredLoadability> RecordMeasuredLoadability(
        string baseDirectory,
        IEnumerable<ModuleActivationEntry> tried,
        IEnumerable<FallbackModule> fallbacks,
        IEnumerable<IncompatibleModule> incompatible,
        Action<string>? onReport = null)
    {
        var transitions = ImmutableList.CreateBuilder<MeasuredLoadability>();
        foreach (var verdict in MeasureLoadability(tried, fallbacks, incompatible))
        {
            var current = ModuleActivationSidecar.ReadUnloadable(baseDirectory, verdict.Name);
            try
            {
                if (verdict.Unloadable)
                {
                    if (current is not null
                        && string.Equals(current.Generation, verdict.Generation, StringComparison.Ordinal)
                        && string.Equals(current.FrameworkMvid, verdict.FrameworkMvid, StringComparison.Ordinal))
                        continue;
                    ModuleActivationSidecar.SetUnloadable(
                        baseDirectory, verdict.Name, verdict.Generation, verdict.FrameworkMvid, verdict.Reason);
                    onReport?.Invoke(
                        $"module '{verdict.Name}': generation '{verdict.Generation}' (framework "
                        + $"{verdict.FrameworkMvid ?? "(unrecorded)"}) measured UNLOADABLE here — recorded, "
                        + "so the update reconcile re-examines every new build of its version: "
                        + verdict.Reason);
                }
                else
                {
                    if (current is null)
                        continue;
                    ModuleActivationSidecar.ClearUnloadable(baseDirectory, verdict.Name);
                    onReport?.Invoke(
                        $"module '{verdict.Name}': generation '{verdict.Generation}' loaded — the "
                        + $"unloadable marker (for '{current.Generation}') is cleared.");
                }
                transitions.Add(verdict);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                onReport?.Invoke(
                    $"module '{verdict.Name}': could not record its measured loadability "
                    + $"({ex.GetType().Name}: {ex.Message}) — the next boot measures again; until then the "
                    + "update reconcile compares identities without knowing the head did not load.");
            }
        }
        return transitions.ToImmutable();
    }

    /// <summary>The generation directory leaf of a loader's entry path — the same derivation
    /// <see cref="FallbackModule.Generation"/> uses, so the two agree by construction.</summary>
    private static string GenerationOf(string entry)
    {
        var directory = Path.GetDirectoryName(entry);
        return string.IsNullOrEmpty(directory) ? entry : Path.GetFileName(directory);
    }

    /// <summary>
    /// Whether the entry holds a previous generation whose entry DLL is on the volume — the
    /// generation boot can fall back to when the head one does not load here (#3649). False for
    /// an entry with no previous generation, and for one whose previous bytes are gone.
    /// </summary>
    public static bool PreviousLandedModuleDllExists(string baseDirectory, ModuleActivationEntry entry) =>
        PreviousGeneration(entry) is { } previous && LandedModuleDllExists(baseDirectory, previous);

    /// <summary>
    /// The file the boot loader loads for one <see cref="EffectiveModule"/> — the SAME resolution
    /// the existence gate applied, which is the whole point of it living here:
    ///
    /// <list type="bullet">
    ///   <item>a SIDECAR (store-landed) module resolves to <see cref="LandedDllPath"/> — its own
    ///     landed directory and nothing else. Never <c>ResolveModulePath</c>: its app-closure
    ///     fallback would bind a same-named platform binary for an entry the gate just decided
    ///     was present, which is the trap-door <see cref="ModuleLandingService"/> refuses at
    ///     landing.</item>
    ///   <item>a BASELINE entry keeps <c>MeshBuilder.ResolveModulePath</c>'s probes (landed root's
    ///     <c>modules/&lt;name&gt;/</c>, the image's own, then the classic BaseDirectory-relative
    ///     location) — all of them legitimate for the image's own closure.</item>
    /// </list>
    /// </summary>
    public static string ResolveLoadPath(string baseDirectory, EffectiveModule module) =>
        module.Landed is not null
            ? LandedDllPath(baseDirectory, module.Landed)
            : MeshBuilder.ResolveModulePath(module.Entry, baseDirectory);

    /// <summary>The entry's landed DLL as the deployment-relative path a skip report names —
    /// the generation directory when the entry carries one, else the legacy fixed folder.</summary>
    private static string RelativeLandedDll(ModuleActivationEntry entry) =>
        $"modules/{(string.IsNullOrWhiteSpace(entry.Directory) ? entry.Name : entry.Directory)}"
        + $"/{entry.Name}.dll";
}
