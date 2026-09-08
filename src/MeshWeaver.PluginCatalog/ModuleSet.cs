using System.Collections.Immutable;
using System.Globalization;
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
    /// <para>🚨 <b>It decides from the NAMES and reads only what the decision needs.</b> Every record
    /// this store writes is named <c>&lt;sequence:D9&gt;-&lt;id16&gt;.proposed|adopted.json</c>
    /// (<see cref="PathOf"/>), so the newest proposal and the newest adopted set are determined
    /// from one directory listing; only the proposal files of those two sequences and the one
    /// adoption record are opened. Measured on memex-cloud (2026-09-08): 687 records under
    /// <c>modules/sets</c> on Azure Files, and the previous read — every file, every call — took
    /// 10 s per call, inside a health check probed every 10 s with a 5 s timeout: no new pod could
    /// pass its startup probe, so a rollout stuck for hours on two ageing replicas. A record that
    /// no decision depends on is neither read nor reported — a corrupt superseded record is
    /// <see cref="Prune"/>'s to remove, not this reader's to announce on every probe; the
    /// conflict report likewise names only the sequences that were actually decided on.</para>
    /// </summary>
    /// <param name="baseDirectory">The deployment root the <c>modules/</c> tree lives under.</param>
    /// <param name="onCorrupt">The loud channel — one call per record that could not be read. This
    /// overload also routes the duplicate-proposal NOTICE here, which is what every caller saw
    /// before #3675; a caller that must tell the two apart passes <c>onNotice</c> on the
    /// three-argument overload.</param>
    public static ModuleSetIndex Read(string baseDirectory, Action<string>? onCorrupt = null) =>
        Read(baseDirectory, onCorrupt, onNotice: onCorrupt);

    /// <summary>
    /// <see cref="Read(string, Action{string})"/> with the NOTICE channel separate from the FAULT
    /// channel (#3675). "Sequence N was proposed by more than one replica" is a decided outcome —
    /// the ordinally smallest id wins, every reader picks the same one, nothing is lost — and not a
    /// record that could not be read. Delivering it through <paramref name="onCorrupt"/> made the
    /// modules GC count it as a read fault and fail closed on EVERY pass for as long as one such pair
    /// existed on the volume: measured on memex-cloud 2026-09-08 — 100 duplicate sequences, 687 set
    /// records and 843 generation directories that no pass ever reclaimed, and a <c>/health</c>
    /// probe reading all 687 records in 9–10 s on Azure Files (over its 5 s timeout), which is what
    /// kept every new pod from passing its startup probe.
    /// </summary>
    /// <param name="baseDirectory">The deployment root the <c>modules/</c> tree lives under.</param>
    /// <param name="onCorrupt">The loud channel — one call per record (or the directory) that could
    /// not be read. A caller that fails closed on incomplete knowledge counts THESE.</param>
    /// <param name="onNotice">The informational channel — one call per sequence that more than one
    /// replica proposed, naming the winner. Never a reason to distrust the index.</param>
    public static ModuleSetIndex Read(
        string baseDirectory, Action<string>? onCorrupt, Action<string>? onNotice)
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

        // ── 1. Index by NAME — one listing, no file opened ──
        // proposals: sequence → the proposal files written for it (one per proposing replica);
        // adoptions: "{sequence}:{id16}" → the adoption file. A name this store never wrote is
        // read as before (it cannot be placed on the index without its content).
        var proposalFiles = new Dictionary<long, List<string>>();
        var adoptionFiles = new Dictionary<string, string>(StringComparer.Ordinal);
        var unnamedProposals = new List<string>();
        var unnamedAdoptions = new List<string>();
        foreach (var file in files)
        {
            var leaf = Path.GetFileName(file);
            if (leaf.EndsWith(ProposedSuffix, StringComparison.Ordinal))
            {
                if (TryParseRecordName(leaf, ProposedSuffix, out var sequence, out _))
                    (proposalFiles.TryGetValue(sequence, out var list) ? list : proposalFiles[sequence] = []).Add(file);
                else
                    unnamedProposals.Add(file);
            }
            else if (leaf.EndsWith(AdoptedSuffix, StringComparison.Ordinal))
            {
                if (TryParseRecordName(leaf, AdoptedSuffix, out var sequence, out var shortId))
                    adoptionFiles.TryAdd(ShortKey(sequence, shortId), file);
                else
                    unnamedAdoptions.Add(file);
            }
        }

        // ── 2. Read the few records the decision needs ──
        var proposals = new Dictionary<long, List<ModuleSet>>();
        var read = new HashSet<long>();
        void ReadProposalsAt(long sequence)
        {
            if (!read.Add(sequence) || !proposalFiles.TryGetValue(sequence, out var atSequence))
                return;
            foreach (var file in atSequence)
                if (TryRead<ModuleSet>(file, onCorrupt) is { } set && set.Generations is not null)
                    Add(proposals, sequence, WithCaseInsensitiveGenerations(set));
        }
        // A hand-placed or foreign-named record keeps the old contract: read, then indexed by content.
        foreach (var file in unnamedProposals)
            if (TryRead<ModuleSet>(file, onCorrupt) is { } set && set.Generations is not null)
            {
                Add(proposals, set.Sequence, WithCaseInsensitiveGenerations(set));
                read.Add(set.Sequence);
            }
        var unnamedAdopted = new Dictionary<string, ModuleSetAdoption>(StringComparer.Ordinal);
        foreach (var file in unnamedAdoptions)
            if (TryRead<ModuleSetAdoption>(file, onCorrupt) is { } foreign)
                unnamedAdopted.TryAdd(Key(foreign.Sequence, foreign.Id), foreign with
                {
                    Generations = foreign.Generations?.WithComparers(StringComparer.OrdinalIgnoreCase),
                });

        // The newest proposal: the highest sequence whose records can be read. A sequence whose
        // every file is corrupt is reported (it was opened) and the next one down decides — exactly
        // what reading everything answered, minus the records nothing depended on.
        ModuleSet? newest = null;
        foreach (var sequence in proposalFiles.Keys.Concat(proposals.Keys).Distinct().OrderByDescending(s => s))
        {
            ReadProposalsAt(sequence);
            if (WinnerAt(proposals, sequence) is { } winner)
            {
                newest = winner;
                break;
            }
        }
        if (newest is null)
            return ModuleSetIndex.Empty;

        // The current set: the highest sequence that has BOTH a proposal and an adoption record
        // for the same id. The name index answers which sequences qualify; only their records are
        // read, highest first, until one reads consistently (a corrupt adoption falls through to the
        // next adopted sequence, as it always did).
        ModuleSet? current = null;
        ModuleSetAdoption? adoption = null;
        var adoptedSequences = proposalFiles
            .Where(kv => kv.Value.Any(file =>
                TryParseRecordName(Path.GetFileName(file), ProposedSuffix, out _, out var shortId)
                && adoptionFiles.ContainsKey(ShortKey(kv.Key, shortId))))
            .Select(kv => kv.Key)
            .Concat(unnamedAdopted.Values.Select(a => a.Sequence))
            .Distinct()
            .OrderByDescending(s => s);
        foreach (var sequence in adoptedSequences)
        {
            ReadProposalsAt(sequence);
            if (WinnerAt(proposals, sequence) is not { } winner)
                continue;
            var candidate = unnamedAdopted.GetValueOrDefault(Key(winner.Sequence, winner.Id));
            if (candidate is null
                && adoptionFiles.TryGetValue(ShortKey(winner.Sequence, ShortId(winner.Id)), out var adoptionFile)
                && TryRead<ModuleSetAdoption>(adoptionFile, onCorrupt) is { } readAdoption
                && string.Equals(readAdoption.Id, winner.Id, StringComparison.Ordinal))
                candidate = readAdoption with
                {
                    Generations = readAdoption.Generations?.WithComparers(StringComparer.OrdinalIgnoreCase),
                };
            if (candidate is null)
                continue;
            current = winner;
            adoption = candidate;
            break;
        }

        // 🚨 Deterministic conflict resolution, reported for the sequences this read decided on.
        // Two replicas can complete a wave at the same moment and each write sequence N+1; both
        // files survive (nothing is ever renamed over). Every reader picks the SAME one without
        // talking to anyone — the ordinally smallest content id — and the loser is not lost: the
        // proposal is derived from the activation record, so the next wave carries everything both
        // landed. A conflict on a sequence nothing decides on any more is history, not a finding.
        var conflicts = proposals
            .Where(kv => kv.Value.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(kv => kv.Key)
            .OrderBy(x => x)
            .ToImmutableList();
        foreach (var sequence in conflicts)
            onNotice?.Invoke(
                $"Module set sequence {sequence} was proposed by more than one replica — the "
                + $"ordinally smallest id wins ('{WinnerAt(proposals, sequence)!.Id}'), and the next landing "
                + "wave folds every landing back in. No module is lost; the mesh runs one set.");

        // #3649 — what the adopting replica RAN. An adoption written before the field existed
        // carries no generations, and then the set's own are the best anyone recorded.
        var running = current is null || adoption is null
            ? ImmutableSortedDictionary<string, string>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase)
            : adoption.Generations ?? current.Generations;
        return new ModuleSetIndex(newest, current, conflicts) { RunningGenerations = running };
    }

    /// <summary>Parses a record name this store wrote — <c>&lt;sequence:D9&gt;-&lt;id16&gt;&lt;suffix&gt;</c>.</summary>
    private static bool TryParseRecordName(string leaf, string suffix, out long sequence, out string shortId)
    {
        sequence = 0;
        shortId = string.Empty;
        if (!leaf.EndsWith(suffix, StringComparison.Ordinal))
            return false;
        var stem = leaf[..^suffix.Length];
        var dash = stem.IndexOf('-');
        if (dash <= 0 || dash == stem.Length - 1)
            return false;
        if (!long.TryParse(stem[..dash], NumberStyles.None, CultureInfo.InvariantCulture, out sequence))
            return false;
        shortId = stem[(dash + 1)..];
        return true;
    }

    private static string ShortKey(long sequence, string shortId) => $"{sequence}:{shortId}";

    private static void Add(Dictionary<long, List<ModuleSet>> proposals, long sequence, ModuleSet set)
        => (proposals.TryGetValue(sequence, out var list) ? list : proposals[sequence] = []).Add(set);

    /// <summary>The proposal that wins <paramref name="sequence"/>: the ordinally smallest id.</summary>
    private static ModuleSet? WinnerAt(Dictionary<long, List<ModuleSet>> proposals, long sequence)
        => proposals.TryGetValue(sequence, out var at) && at.Count > 0
            ? at.OrderBy(p => p.Id, StringComparer.Ordinal).First()
            : null;

    // 🚨 The comparer does NOT survive the JSON round-trip — a deserialized ImmutableSortedDictionary
    // carries the DEFAULT ordinal one. Module identity is case-insensitive everywhere else (the
    // activation record, the loaded-assembly set, MeshBuilder's probe), and a set that alone
    // compared case-sensitively would silently defer a module whose entry differs only in case —
    // the same name reading as two.
    private static ModuleSet WithCaseInsensitiveGenerations(ModuleSet set)
        => set with { Generations = set.Generations.WithComparers(StringComparer.OrdinalIgnoreCase) };

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
    /// <param name="index">The set index, as <see cref="Read(string, Action{string})"/> answered it.</param>
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
    /// hundreds, and <see cref="Read(string, Action{string})"/> is on the BOOT path.
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

    /// <summary>
    /// Retires the LOSERS of a duplicate that has been DECIDED (#3656) — at every sequence more
    /// than one replica proposed, every proposal record except the one every reader resolves to
    /// (the ordinally smallest <see cref="ModuleSet.Id"/>, exactly as
    /// <see cref="Read(string, Action{string}, Action{string})"/> resolves it), plus any adoption
    /// record written against a loser.
    ///
    /// <para>🚨 <b>Why the loser is housekeeping and not evidence.</b> A duplicate is decided the
    /// first time anything reads it: every reader picks the same winner without coordinating, and
    /// nothing is lost, because the next proposal is derived from the ACTIVATION RECORD and never
    /// from the previous proposal — so the loser's landings ride the following wave whether its
    /// record survives or not. What the record does do while it survives is make the conflict
    /// re-detectable: the two files sit at the mesh's newest sequence until some later wave
    /// supersedes them (<see cref="Prune"/> only reaches BELOW the current set), so every pod's
    /// sweep re-reads them and re-reports the same decided conflict on every boot, forever. That
    /// is issue #3656. Retiring the loser reports the conflict ONCE — when it is decided — instead
    /// of once per sweep per pod, and leaves one record at the sequence, which is what a decided
    /// conflict actually is.</para>
    ///
    /// <para>🚨 <b>Fail closed on a record it cannot read.</b> A record whose id is unknown makes
    /// the WINNER unknown, so nothing at that sequence is retired until it can be read — the
    /// #2509 rule, for the same reason: the one thing this must never do is delete the record the
    /// mesh resolves to.</para>
    /// </summary>
    /// <param name="baseDirectory">The deployment root the <c>modules/</c> tree lives under.</param>
    /// <param name="onRetired">One call per retired record, naming the sequence, the winner and
    /// the loser — the once-per-conflict report that replaces the once-per-sweep one.</param>
    /// <param name="onWarn">The loud channel for a record that could not be read or removed.</param>
    /// <returns>How many records were removed.</returns>
    public static int PruneDuplicateProposals(
        string baseDirectory, Action<string>? onRetired = null, Action<string>? onWarn = null)
    {
        var directory = SetsDirectory(baseDirectory);
        if (!Directory.Exists(directory))
            return 0;
        string[] files;
        try
        {
            files = [.. Directory.EnumerateFiles(directory, "*" + ProposedSuffix)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            onWarn?.Invoke(
                $"Module set records under '{directory}' could not be listed ({ex.GetType().Name}: "
                + $"{ex.Message}) — no decided duplicate was retired this pass.");
            return 0;
        }

        var bySequence = new Dictionary<long, List<string>>();
        foreach (var file in files)
            if (TryParseRecordName(Path.GetFileName(file), ProposedSuffix, out var sequence, out _))
                (bySequence.TryGetValue(sequence, out var list) ? list : bySequence[sequence] = [])
                    .Add(file);

        var removed = 0;
        foreach (var (sequence, atSequence) in bySequence.Where(kv => kv.Value.Count > 1))
        {
            var records = atSequence
                .Select(file => (File: file, Set: TryRead<ModuleSet>(file, onWarn)))
                .ToArray();
            if (Array.Exists(records, r => r.Set is null))
                continue;   // unknown id ⇒ unknown winner ⇒ nothing is retired here
            var winner = records.OrderBy(r => r.Set!.Id, StringComparer.Ordinal).First();
            foreach (var (file, set) in records)
            {
                if (string.Equals(set!.Id, winner.Set!.Id, StringComparison.Ordinal))
                    continue;
                if (!TryDelete(file, onWarn))
                    continue;
                removed++;
                // The loser's adoption record, if any replica wrote one before the winner's file
                // existed. Read() already ignores it (an adoption counts only against the winner's
                // id); leaving it behind would leave the conflict half-visible.
                if (TryDelete(PathOf(baseDirectory, sequence, set.Id, AdoptedSuffix), onWarn: null))
                    removed++;
                onRetired?.Invoke(
                    $"Module set sequence {sequence} was proposed by more than one replica and is "
                    + $"decided — the ordinally smallest id wins ('{winner.Set.Id}'), and the "
                    + $"superseded proposal '{set.Id}' has been retired. No module is lost: the "
                    + "next proposal is derived from the activation record, so every landing rides "
                    + "the following wave.");
            }
        }
        return removed;
    }

    /// <summary>Deletes one record; an absent file is success, and a refusal is reported to
    /// <paramref name="onWarn"/> and left for a later pass.</summary>
    private static bool TryDelete(string path, Action<string>? onWarn)
    {
        try
        {
            if (!File.Exists(path))
                return false;
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            onWarn?.Invoke(
                $"Module set record '{Path.GetFileName(path)}' could not be removed "
                + $"({ex.GetType().Name}: {ex.Message}) — a later pass collects it; nothing "
                + "resolves against it.");
            return false;
        }
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
