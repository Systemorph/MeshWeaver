using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// THE module set a mesh runs — one, mesh-wide, at a time (#3395).
///
/// <para>🚨 <b>What it fixes.</b> Landing moves each module's activation entry INDEPENDENTLY, the
/// moment that module's bytes are on disk (<see cref="ModuleActivationSidecar.WriteEntry"/>), and a
/// process pins whatever the entries said at ITS boot instant
/// (<see cref="ModuleGenerationPin"/>). Two consequences, both measured on memex-cloud
/// 2026-09-06: a replica booting DURING a landing wave pins a TORN mix (some modules of the new
/// wave, the rest of the old one — a combination no wave ever intended), and replicas booting on
/// either side of a wave pin two whole sets and hold them for their whole lives — <b>39 of 40
/// generations differed</b> across three pods of one ReplicaSet on one image. They share ONE
/// NodeType node, so each stamps the module set IT resolved and each then reads the other's stamp
/// as stale: the pair ping-pongs recompiles, and where one replica's set lacks a module the
/// sources need, a healthy NodeType FAILS with no source change.</para>
///
/// <para><b>The rule.</b> A landing wave does not move what the mesh RUNS. It stages bytes, and
/// when the WHOLE wave is done it PROPOSES one set — an immutable, sequenced record naming every
/// module's generation. Boot loads the mesh's newest proposal, never its own read of the moving
/// per-module entries. So every replica that boots between two wave completions loads the
/// IDENTICAL set, and no boot can ever observe a half-landed mix.</para>
///
/// <para><b>Immutable and monotone, never a mutable cell.</b> Each proposal is one write-once file
/// named for its sequence AND its content id, and adoption is a second write-once file beside it.
/// Nothing here is ever read-modify-written or renamed over a live file — the two defects that
/// #2090/#2189 removed from the activation record, which is why that record is one file per module
/// in the first place. Two replicas that propose the same sequence therefore CONFLICT visibly
/// instead of losing one another's work: both files exist, every reader resolves the conflict the
/// same way (see <see cref="ModuleSetIndex.Proposed"/>), and the next wave folds everything the
/// loser landed into sequence + 1, because the proposal is derived from the activation record and
/// never from the previous proposal.</para>
/// </summary>
/// <param name="Sequence">Monotonically increasing; the mesh's newest proposal is the highest one.
/// The first proposal a deployment ever writes is 1.</param>
/// <param name="Id">Content id — lowercase hex SHA-256 over the set's <c>name@generation</c> pairs,
/// ordinal-sorted and joined with <c>;</c>. Two sets with the same id activate the same bytes.</param>
/// <param name="Generations">Module simple name → the GENERATION directory leaf
/// (<c>&lt;name&gt;@&lt;id&gt;</c>) this set activates for it — the same string
/// <see cref="ModuleActivationEntry.Directory"/> records and
/// <see cref="ModuleActivationStatus.LoadedModuleGenerations()"/> reads back off a loaded
/// assembly.</param>
/// <param name="ProposedAtUtc">When the wave that produced this set finished.</param>
/// <param name="ProposedBy">Which process proposed it — diagnostics only, never a gate.</param>
public sealed record ModuleSet(
    long Sequence,
    string Id,
    ImmutableSortedDictionary<string, string> Generations,
    DateTime ProposedAtUtc,
    string? ProposedBy = null);

/// <summary>
/// What one process recorded when it BOOTED onto a <see cref="ModuleSet"/> — the evidence that the
/// mesh's proposal is not merely declared but actually running somewhere (#3395).
///
/// <para>Written create-if-absent, once per set: the question it answers is "has ANY replica come
/// up on this set yet", which is exactly what bounds the convergence window. A proposal no replica
/// has adopted means the window is OPEN — the mesh has declared a set that nothing serves yet, and
/// the next restart closes it.</para>
/// </summary>
/// <param name="Sequence">The set's sequence.</param>
/// <param name="Id">The set's content id.</param>
/// <param name="AdoptedAtUtc">When the first process booted onto it.</param>
/// <param name="AdoptedBy">Which process — diagnostics only.</param>
public sealed record ModuleSetAdoption(
    long Sequence,
    string Id,
    DateTime AdoptedAtUtc,
    string? AdoptedBy = null)
{
    /// <summary>
    /// Module simple name → the generation the adopting replica ACTUALLY LOADED (#3649): the
    /// set's own generation for every module that loaded as proposed, and the PREVIOUS generation
    /// for every module that fell back because the set's one does not load on this platform. So
    /// a reader of the set records learns what the mesh RUNS, not only what it proposed — and the
    /// GC references what runs. Null on a record written before this field existed, which reads
    /// as "the set's generations, as far as anyone recorded". An init-only property, not a fifth
    /// positional parameter: replacing a public record's constructor signature is a binary break
    /// for a host compiled against the previous platform.
    /// </summary>
    public ImmutableSortedDictionary<string, string>? Generations { get; init; }
}

/// <summary>
/// Everything the <c>modules/sets/</c> directory says, read in one pass: which set the mesh has
/// PROPOSED (what the next boot loads) and which set it is ON (the newest proposal some replica has
/// actually booted onto).
/// </summary>
/// <param name="Proposed">The mesh's newest proposal, or null on a deployment that has never
/// completed a landing wave. 🚨 When two replicas proposed the same sequence, the ordinally
/// SMALLEST <see cref="ModuleSet.Id"/> wins — deterministic, so every reader picks the same one
/// without coordinating, and the loser's landings return in the next proposal.</param>
/// <param name="Current">The newest PROPOSED set that also carries an adoption record — "the mesh
/// is on generation N". Null when no proposal has been adopted yet.</param>
/// <param name="ConflictingSequences">Sequences that more than one replica proposed. Reported, not
/// repaired: the conflict is resolved deterministically and the next wave supersedes it.</param>
public sealed record ModuleSetIndex(
    ModuleSet? Proposed,
    ModuleSet? Current,
    ImmutableList<long> ConflictingSequences)
{
    /// <summary>An empty deployment's index — no proposal, no adoption, no conflict.</summary>
    public static readonly ModuleSetIndex Empty = new(null, null, []);

    /// <summary>
    /// Module simple name → the generation the replica that adopted <see cref="Current"/>
    /// actually loaded (#3649) — <see cref="ModuleSet.Generations"/> of the current set for every
    /// module that loaded as proposed, the previous generation for every module that fell back.
    /// Empty when no set is current, or when the adoption record predates the field. What the
    /// mesh RUNS, as opposed to what it proposed; an init-only property, for binary compatibility
    /// with hosts compiled against the three-argument record.
    /// </summary>
    public ImmutableSortedDictionary<string, string> RunningGenerations { get; init; } =
        ImmutableSortedDictionary<string, string>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The modules of <see cref="Current"/> that RUN A PREVIOUS GENERATION on the adopting replica
    /// (#3649): name → the generation loaded, for every module whose running generation differs
    /// from the set's. Empty when every module loaded as proposed.
    /// </summary>
    public ImmutableSortedDictionary<string, string> FallbackGenerations =>
        Current is null
            ? ImmutableSortedDictionary<string, string>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase)
            : RunningGenerations
                .Where(kv => Current.Generations.TryGetValue(kv.Key, out var proposed)
                             && !string.Equals(proposed, kv.Value, StringComparison.Ordinal))
                .ToImmutableSortedDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True while the mesh has declared a set that NO replica has booted onto yet — the
    /// convergence window, stated positively. It opens when a landing wave proposes and closes at
    /// the first restart; a window that stays open is a wave whose bytes nothing is serving.
    /// </summary>
    public bool ConvergencePending =>
        Proposed is not null && (Current is null || Current.Sequence < Proposed.Sequence);
}

/// <summary>
/// The <c>modules/sets/</c> record store — plain, synchronous file IO, because BOOT is its first
/// caller and boot runs before the DI container, any storage provider or any hub exists (the same
/// reason <see cref="ModuleActivationSidecar"/> is a file). Runtime callers go through
/// <see cref="ModuleLandingService"/>, which runs these on its bounded IO pool.
///
/// <para>Layout, beside the module folders the sets name:</para>
/// <list type="bullet">
///   <item><c>modules/sets/&lt;sequence:D9&gt;-&lt;id8&gt;.proposed.json</c> — a
///     <see cref="ModuleSet"/>, written ONCE by the landing wave that completed it.</item>
///   <item><c>modules/sets/&lt;sequence:D9&gt;-&lt;id8&gt;.adopted.json</c> — a
///     <see cref="ModuleSetAdoption"/>, written create-if-absent by the first process that boots
///     onto that set.</item>
/// </list>
/// </summary>
public static class ModuleSetStore
{
    /// <summary>The directory inside <c>modules/</c> the set records live in.</summary>
    public const string DirectoryName = "sets";

    private const string ProposedSuffix = ".proposed.json";
    private const string AdoptedSuffix = ".adopted.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The set-record directory for a deployment rooted at
    /// <paramref name="baseDirectory"/> — <c>{baseDirectory}/modules/sets</c>.</summary>
    public static string SetsDirectory(string baseDirectory) =>
        Path.Combine(baseDirectory, "modules", DirectoryName);

    /// <summary>
    /// The set an activation list activates: every ENABLED entry that names a generation, as
    /// name → generation. Pure — the one derivation, so a proposal and the comparison a running
    /// process makes against it can never be computed two different ways.
    ///
    /// <para>An entry with no <see cref="ModuleActivationEntry.Directory"/> (the legacy fixed
    /// <c>modules/&lt;name&gt;/</c> folder) contributes nothing: it names no generation, so there
    /// is nothing for a set to pin, and it keeps resolving exactly as it does today.</para>
    /// </summary>
    public static ImmutableSortedDictionary<string, string> GenerationsOf(ModuleActivationList? landed) =>
        (landed?.Entries ?? [])
        .Where(e => e.Enabled
            && !string.IsNullOrWhiteSpace(e.Name)
            && !string.IsNullOrWhiteSpace(e.Directory))
        .GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
        // First ENABLED entry of a name wins — the same tie-break the boot union applies.
        .ToImmutableSortedDictionary(g => g.Key, g => g.First().Directory!, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The content id of a set: lowercase hex SHA-256 over the ordinal-sorted
    /// <c>name@generation</c> pairs joined with <c>;</c>. The empty set hashes to the empty string
    /// — stable, and distinguishable from "no set at all", which is <c>null</c>.
    /// </summary>
    public static string IdOf(ImmutableSortedDictionary<string, string> generations)
    {
        ArgumentNullException.ThrowIfNull(generations);
        if (generations.Count == 0)
            return string.Empty;
        var payload = string.Join(";", generations
            .Select(kv => kv.Key + "@" + kv.Value)
            .OrderBy(x => x, StringComparer.Ordinal));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    /// <summary>
    /// Reads the whole set directory. An ABSENT directory is the normal state of a deployment that
    /// has never completed a landing wave and answers <see cref="ModuleSetIndex.Empty"/> silently
    /// — which is what keeps this change inert until the first wave proposes.
    ///
    /// <para>🚨 A record file that cannot be READ costs exactly that one record, loudly, and never
    /// the whole answer — the #2189 rule, for the same reason: boot calls this un-wrapped, and a
    /// transient SMB fault must not take a portal down or silently collapse the mesh's module set
    /// to "none". A proposal that is skipped simply leaves an older one current, which is a set
    /// that was correct a moment ago.</para>
    /// </summary>
    /// <param name="baseDirectory">The deployment root the <c>modules/</c> tree lives under.</param>
    /// <param name="onCorrupt">The loud channel — one call per record that could not be read.</param>
    public static ModuleSetIndex Read(string baseDirectory, Action<string>? onCorrupt = null)
    {
        var directory = SetsDirectory(baseDirectory);
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
                $"Module set records under '{directory}' could not be listed ({ex.GetType().Name}: "
                + $"{ex.Message}) — this process cannot tell which module set the mesh is on and "
                + "falls back to the activation record, which is what every replica did before "
                + "#3395. Restart once the volume is readable to rejoin the mesh's set.");
            return ModuleSetIndex.Empty;
        }

        var proposals = new List<ModuleSet>();
        var adopted = new Dictionary<string, ModuleSetAdoption>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            if (file.EndsWith(ProposedSuffix, StringComparison.Ordinal))
            {
                if (TryRead<ModuleSet>(file, onCorrupt) is { } set && set.Generations is not null)
                    // 🚨 The comparer does NOT survive the JSON round-trip — a deserialized
                    // ImmutableSortedDictionary carries the DEFAULT ordinal one. Module identity is
                    // case-insensitive everywhere else (the activation record, the loaded-assembly
                    // set, MeshBuilder's probe), and a set that alone compared case-sensitively
                    // would silently defer a module whose entry differs only in case — the same
                    // name reading as two.
                    proposals.Add(set with
                    {
                        Generations = set.Generations
                            .WithComparers(StringComparer.OrdinalIgnoreCase),
                    });
            }
            else if (file.EndsWith(AdoptedSuffix, StringComparison.Ordinal)
                     && TryRead<ModuleSetAdoption>(file, onCorrupt) is { } adoption)
            {
                // Create-if-absent means one record per set, so a second file for the same key
                // is the same bytes; first read wins and nothing is lost.
                adopted.TryAdd(Key(adoption.Sequence, adoption.Id), adoption with
                {
                    Generations = adoption.Generations?.WithComparers(StringComparer.OrdinalIgnoreCase),
                });
            }
        }

        if (proposals.Count == 0)
            return ModuleSetIndex.Empty;

        // 🚨 Deterministic conflict resolution. Two replicas can complete a wave at the same
        // moment and each write sequence N+1; both files survive (nothing is ever renamed over).
        // Every reader must then pick the SAME one without talking to anyone, so the tie-break is
        // the ordinally smallest content id. The loser is not lost: the proposal is derived from
        // the activation record, so the next wave's sequence N+2 carries everything both landed.
        var bySequence = proposals
            .GroupBy(p => p.Sequence)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(p => p.Id, StringComparer.Ordinal).First());
        var conflicts = proposals
            .GroupBy(p => p.Sequence)
            .Where(g => g.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(g => g.Key)
            .OrderBy(x => x)
            .ToImmutableList();
        foreach (var sequence in conflicts)
            onCorrupt?.Invoke(
                $"Module set sequence {sequence} was proposed by more than one replica — the "
                + $"ordinally smallest id wins ('{bySequence[sequence].Id}'), and the next landing "
                + "wave folds every landing back in. No module is lost; the mesh runs one set.");

        var newest = bySequence.Values.OrderByDescending(p => p.Sequence).First();
        var current = bySequence.Values
            .Where(p => adopted.ContainsKey(Key(p.Sequence, p.Id)))
            .OrderByDescending(p => p.Sequence)
            .FirstOrDefault();
        // #3649 — what the adopting replica RAN. An adoption written before the field existed
        // carries no generations, and then the set's own are the best anyone recorded.
        var running = current is null
            ? ImmutableSortedDictionary<string, string>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase)
            : adopted[Key(current.Sequence, current.Id)].Generations ?? current.Generations;
        return new ModuleSetIndex(newest, current, conflicts) { RunningGenerations = running };
    }

    /// <summary>
    /// Proposes the set the deployment's activation record currently describes — the coordination
    /// step at the END of a landing wave (#3395), and the ONLY thing that moves what the mesh runs.
    ///
    /// <para>A NO-OP when the derived set is byte-identical to the newest proposal, which is the
    /// steady state: a reconcile pass that lands nothing proposes nothing, so sequences count
    /// waves that actually changed something rather than boots.</para>
    ///
    /// <para>🚨 Called only when the wave COMPLETED. A wave that dies half-landed must not propose
    /// — the mesh then keeps running the set it was on, no replica ever adopts a half-landed mix,
    /// and the modules it did land show up as landed-but-unproposed
    /// (<see cref="ModuleActivationBoot.ProjectOntoMeshSet"/>) rather than as silent drift.</para>
    /// </summary>
    /// <param name="baseDirectory">The deployment root the <c>modules/</c> tree lives under.</param>
    /// <param name="landed">The activation record as the wave left it.</param>
    /// <param name="proposedBy">Diagnostics — which process completed the wave.</param>
    /// <param name="onCorrupt">The loud channel for unreadable existing records.</param>
    /// <returns>The set that was written, or null when nothing changed.</returns>
    public static ModuleSet? Propose(
        string baseDirectory,
        ModuleActivationList landed,
        string? proposedBy = null,
        Action<string>? onCorrupt = null)
    {
        ArgumentNullException.ThrowIfNull(landed);
        var generations = GenerationsOf(landed);
        var id = IdOf(generations);
        var index = Read(baseDirectory, onCorrupt);
        if (index.Proposed is { } newest && string.Equals(newest.Id, id, StringComparison.Ordinal))
            return null;

        var set = new ModuleSet(
            (index.Proposed?.Sequence ?? 0) + 1,
            id,
            generations,
            DateTime.UtcNow,
            proposedBy);
        WriteOnce(PathOf(baseDirectory, set.Sequence, set.Id, ProposedSuffix),
            JsonSerializer.Serialize(set, Json));
        return set;
    }

    /// <summary>
    /// Records that THIS process booted onto <paramref name="set"/> — create-if-absent, so the
    /// second and every later replica to come up on it write nothing. Best effort: a read-only or
    /// full volume costs the mesh-level "somebody is serving this set" signal and nothing else,
    /// which is why it is never allowed to fail a boot.
    /// </summary>
    /// <param name="baseDirectory">The deployment root.</param>
    /// <param name="set">The set this process loaded.</param>
    /// <param name="adoptedBy">Diagnostics — which process.</param>
    /// <param name="onWarn">The loud channel for a failed record.</param>
    public static void RecordAdoption(
        string baseDirectory,
        ModuleSet set,
        string? adoptedBy = null,
        Action<string>? onWarn = null)
        => RecordAdoption(baseDirectory, set, runningGenerations: null, adoptedBy, onWarn);

    /// <summary>
    /// As the four-argument form, recording what this process ACTUALLY LOADED per module (#3649)
    /// — the set's generation where it loaded, the previous generation where boot fell back — so
    /// the mesh's records say what runs and the GC references it. An overload, not a new optional
    /// parameter: the four-argument signature is binary API for hosts compiled against it.
    /// </summary>
    /// <param name="baseDirectory">The deployment root.</param>
    /// <param name="set">The set this process loaded.</param>
    /// <param name="runningGenerations">Module simple name → the generation loaded, as
    /// <see cref="RunningGenerationsOf"/> derives it; null records the set's own generations.</param>
    /// <param name="adoptedBy">Diagnostics — which process.</param>
    /// <param name="onWarn">The loud channel for a failed record.</param>
    public static void RecordAdoption(
        string baseDirectory,
        ModuleSet set,
        IReadOnlyDictionary<string, string>? runningGenerations,
        string? adoptedBy = null,
        Action<string>? onWarn = null)
    {
        ArgumentNullException.ThrowIfNull(set);
        var path = PathOf(baseDirectory, set.Sequence, set.Id, AdoptedSuffix);
        try
        {
            if (File.Exists(path))
                return;
            var generations = (runningGenerations ?? set.Generations)
                .ToImmutableSortedDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
            WriteOnce(path, JsonSerializer.Serialize(
                new ModuleSetAdoption(set.Sequence, set.Id, DateTime.UtcNow, adoptedBy)
                {
                    Generations = generations,
                }, Json));
        }
        catch (Exception ex)
        {
            onWarn?.Invoke(
                $"could not record adoption of module set {set.Sequence} ('{set.Id}') "
                + $"({ex.GetType().Name}: {ex.Message}) — this process still runs that set; only "
                + "the mesh-level 'a replica is serving it' signal is missing.");
        }
    }

    /// <summary>
    /// Every generation directory leaf the retained set records reference — what the modules GC
    /// must treat as REFERENCED on top of the activation entries.
    ///
    /// <para>🚨 Without this the convergence rule would re-open the 2026-08-27 outage from the
    /// other side: the mesh's set deliberately pins an OLDER generation than the activation entry
    /// while a wave's landings wait to be proposed, so that generation is unreferenced by the
    /// entries alone — and GC would reclaim the very bytes every replica is running.</para>
    /// </summary>
    /// <param name="index">The set index, as <see cref="Read"/> answered it.</param>
    public static IReadOnlySet<string> ReferencedGenerations(ModuleSetIndex? index)
    {
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var set in new[] { index?.Proposed, index?.Current })
            foreach (var generation in set?.Generations.Values ?? Enumerable.Empty<string>())
                if (!string.IsNullOrWhiteSpace(generation))
                    referenced.Add(generation);
        // #3649 — and what the adopting replica actually RUNS, which for a module that fell back
        // is a generation neither retained set names.
        foreach (var generation in index?.RunningGenerations.Values ?? Enumerable.Empty<string>())
            if (!string.IsNullOrWhiteSpace(generation))
                referenced.Add(generation);
        return referenced;
    }

    /// <summary>
    /// What a process that installed <paramref name="set"/> actually runs (#3649): the set's own
    /// generation for every module, overridden by the generation that LOADED for every module in
    /// <paramref name="fallbacks"/> — the records <c>MeshBuilder.InstallModules</c> registered for
    /// the modules whose head generation did not load here. The one derivation, shared by the
    /// boot path that records the adoption and the tests that read it back.
    /// </summary>
    /// <param name="set">The set the process booted onto.</param>
    /// <param name="fallbacks">The fallback records the loader registered.</param>
    public static ImmutableSortedDictionary<string, string> RunningGenerationsOf(
        ModuleSet set, IEnumerable<MeshWeaver.Mesh.FallbackModule> fallbacks)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(fallbacks);
        var running = set.Generations.ToBuilder();
        running.KeyComparer = StringComparer.OrdinalIgnoreCase;
        foreach (var fallback in fallbacks)
            if (running.ContainsKey(fallback.Name)
                && !string.IsNullOrWhiteSpace(fallback.PreviousGeneration))
                running[fallback.Name] = fallback.PreviousGeneration;
        return running.ToImmutable();
    }

    /// <summary>
    /// Removes set records the mesh has moved past — everything below <paramref name="keepFrom"/>.
    /// One tiny file per record, but a deployment that lands weekly for a year accumulates
    /// hundreds, and <see cref="Read"/> is on the BOOT path.
    ///
    /// <para>🚨 A record below the CURRENT set references generations nothing the mesh runs
    /// resolves against — and a process still on one of them loaded from its own pinned copy
    /// (<see cref="ModuleGenerationPin"/>), which is exactly why that pin exists. The caller must
    /// pass the current set's sequence, never the proposed one: pruning the record of the set
    /// replicas are still on would take its generations out of the GC reference set.</para>
    /// </summary>
    /// <param name="baseDirectory">The deployment root.</param>
    /// <param name="keepFrom">The lowest sequence to keep — the mesh's CURRENT set.</param>
    /// <param name="onWarn">The loud channel for a record that could not be removed.</param>
    /// <returns>How many records were removed.</returns>
    public static int Prune(string baseDirectory, long keepFrom, Action<string>? onWarn = null)
    {
        var directory = SetsDirectory(baseDirectory);
        if (!Directory.Exists(directory))
            return 0;
        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*.json").ToArray())
        {
            var leaf = Path.GetFileName(file);
            var dash = leaf.IndexOf('-');
            if (dash <= 0 || !long.TryParse(leaf[..dash], out var sequence) || sequence >= keepFrom)
                continue;
            try
            {
                File.Delete(file);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                onWarn?.Invoke(
                    $"Module set record '{leaf}' could not be removed ({ex.GetType().Name}: "
                    + $"{ex.Message}) — a later pass collects it; nothing resolves against it.");
            }
        }
        return removed;
    }

    /// <summary>One human-readable line naming which set the mesh is on and whether a replica is
    /// serving it yet — shared by every surface so two of them can never report different sets.</summary>
    /// <param name="index">The set index.</param>
    public static string Describe(ModuleSetIndex? index) =>
        index?.Proposed is not { } proposed
            ? "the mesh has no proposed module set (no landing wave has completed here)"
            : index.ConvergencePending
                ? $"the mesh's module set is {proposed.Sequence} ('{proposed.Id}', "
                  + $"{proposed.Generations.Count} module(s)) and NO replica has booted onto it yet "
                  + $"— it was proposed at {proposed.ProposedAtUtc:O}"
                : $"the mesh is on module set {proposed.Sequence} ('{proposed.Id}', "
                  + $"{proposed.Generations.Count} module(s))"
                  + DescribeFallbacks(index.FallbackGenerations);

    /// <summary>The "; N module(s) run a previous generation: …" suffix, or nothing (#3649).</summary>
    private static string DescribeFallbacks(ImmutableSortedDictionary<string, string> fallbacks) =>
        fallbacks.Count == 0
            ? string.Empty
            : $"; {fallbacks.Count} module(s) run a PREVIOUS generation because the set's does not "
              + "load on this platform: "
              + string.Join(", ", fallbacks.Select(kv => $"{kv.Key} ({kv.Value})"));

    private static string Key(long sequence, string? id) => $"{sequence}:{id}";

    private static string PathOf(string baseDirectory, long sequence, string id, string suffix) =>
        Path.Combine(
            SetsDirectory(baseDirectory),
            sequence.ToString("D9") + "-" + ShortId(id) + suffix);

    /// <summary>The id fragment that goes into the file NAME — enough to keep two different sets at
    /// one sequence in two different files, while the file's own content carries the full id. The
    /// empty set (a deployment with no generation-landed modules) needs a stand-in that is still a
    /// legal file name.</summary>
    private static string ShortId(string id) =>
        string.IsNullOrEmpty(id) ? "empty" : id[..Math.Min(16, id.Length)];

    private static T? TryRead<T>(string file, Action<string>? onCorrupt) where T : class
    {
        try
        {
            var text = File.ReadAllText(file);
            return JsonSerializer.Deserialize<T>(text, Json);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Vanished between the listing and the read — a concurrent GC pass pruning superseded
            // records. Nothing to report: the record it removed is one nothing resolves against.
            return null;
        }
        catch (Exception ex)
        {
            onCorrupt?.Invoke(
                $"Module set record '{file}' could not be read ({ex.GetType().Name}: {ex.Message}) "
                + "— that ONE record is skipped; every other set record is unaffected.");
            return null;
        }
    }

    /// <summary>
    /// Writes a record that is never rewritten: a temp file in the same directory, then a rename
    /// that does NOT overwrite. Two replicas racing the same path both produce the same bytes, so
    /// the loser's failed rename is success — never a replace over a file a reader holds open,
    /// which is the SMB sharing violation of #2090.
    /// </summary>
    private static void WriteOnce(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, content);
        try
        {
            File.Move(temp, path, overwrite: false);
        }
        catch (IOException) when (File.Exists(path))
        {
            // Another replica wrote the identical record first. That IS the outcome we wanted.
            File.Delete(temp);
        }
    }
}
