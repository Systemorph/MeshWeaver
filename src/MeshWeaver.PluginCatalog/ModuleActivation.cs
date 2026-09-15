using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeshWeaver.Mesh;
using MeshWeaver.Plugin.Packaging;

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

    /// <summary>The producing repository's commit <see cref="PreviousDirectory"/> was built from —
    /// diagnostic, like <see cref="SourceCommit"/>. Recorded so a generation that is running as the
    /// FALLBACK can name its own build rather than borrowing the head's: the two generations are
    /// different bytes from different commits, and a line that printed the head's commit over the
    /// fallback's bytes would be the #4158 defect with a new field.</summary>
    public string? PreviousSourceCommit { get; init; }

    /// <summary>The framework MVID (MeshWeaver.Graph's ModuleVersionId) the landed assemblies
    /// were built against, as the producer recorded it. It names the exact build behind the bytes
    /// when something needs debugging — and since #4161 it is also the DISCRIMINATOR the boot
    /// union uses to choose between TWO copies of one module: a store copy stating a different
    /// identity from the booting platform's loses to the image's own copy of that same module
    /// (<see cref="ModuleActivationBoot.ComputeEffectiveModuleEntries(IReadOnlyList{string}, ModuleActivationList, Func{string, string}, Func{ModuleActivationEntry, bool}, Action{string, string}, Action{string, string}, string)"/>).
    ///
    /// <para>🚨 That is a CHOICE BETWEEN COPIES, never a gate on a module: a Store-only module
    /// loads whatever it was built against, because the alternative to a copy that may be wrong is
    /// no copy at all. Modules still bind by simple name across platform builds, and their
    /// declared contract is still <see cref="MinMeshVersion"/> — which decides nothing, by #3648.
    /// The strict MVID gate, where a mismatch REFUSES outright, remains bake semantics and belongs
    /// to the NodeType assembly lane.</para></summary>
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
    /// 🚨 The PRODUCING REPOSITORY'S COMMIT the landed bundle was built from, exactly as the
    /// producer recorded it in the bundle manifest (#4158) — the one fact that separates "this
    /// generation is the newest" from "its types are current".
    ///
    /// <para>Both of the other identity fields are properties of the FILE, not of the source behind
    /// it: <see cref="ModuleLoadReport"/>'s <c>mvid=</c> is read out of the PE and <c>written=</c>
    /// is its last-write time, so a bundle that is genuinely the newest on the volume prints
    /// "newest" for both while carrying types that predate two merged pull requests. That is
    /// measured, not hypothetical — memex.meshweaver.cloud 2026-09-10, MeshWeaver.Plugins#1585,
    /// where the bundle had never been ADOPTED and the previously adopted build kept serving under
    /// the same-MAJOR rule of #3844. Three RefreshModules and two restarts were spent on the
    /// reading that fell out of the two fields that WERE on the line.</para>
    ///
    /// <para>🚨 <b>DIAGNOSTIC, never a gate, and never inferred.</b> Nothing decides anything on it:
    /// landing is decided by the link probe and updating by
    /// <see cref="ModuleUpdateDecision"/>'s (version, framework identity) pair. Null means the
    /// producer recorded none — which prints as an explicit <c>(unrecorded)</c> and must NEVER be
    /// filled in from the version, the generation, the MVID or the path. A commit-shaped value that
    /// names no commit these bytes came from would restate the very defect the field exists to
    /// close.</para>
    /// </summary>
    public string? SourceCommit { get; init; }

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

    /// <summary>
    /// 🚨 Set on the per-module FILE by current images only (#4026, Copilot's review of #4427): the
    /// newest landing record or uninstall tombstone this file was projected from. Its presence is
    /// what tells a current image that the file is a PROJECTION — decided a moment before it was
    /// written, possibly overtaken by another replica's landing or uninstall since — and so never an
    /// event of its own. An image that predates the records does not know the field and never writes
    /// it (it ignores it on read), so a file WITHOUT it is that image's own install or uninstall,
    /// ordered at the file's write time. Never returned by
    /// <see cref="ModuleActivationSidecar.Read(string, Action{string}?)"/>.
    /// </summary>
    public string? ProjectionOf { get; init; }

    /// <summary>False = uninstalled (the record is kept for history/idempotence; the folder is
    /// deleted). Takes effect at the next restart, like every activation change.</summary>
    public bool Enabled { get; init; } = true;
}

/// <summary>
/// ONE landing's own facts, in its own file, never rewritten (#4026) — the unit a module's
/// activation head is DERIVED from, instead of the head pointer every replica used to replace.
///
/// <para>🚨 <b>Why a record per landing and not the per-module entry.</b> The per-module entry
/// (<see cref="ModuleActivationEntry"/>, <c>activation.d/&lt;Name&gt;.json</c>) is a DECISION — which
/// generation this deployment runs — and a landing used to read it, decide against what it read,
/// and replace it seconds later by an unconditional rename. Within one process that sequence is
/// serialised; across the REPLICAS that share <c>/data</c> it was a lost update: two publishes of
/// one module reaching two replicas a few seconds apart each decided against a head neither had
/// seen the other move, and the later rename silently won. #3996's version rule therefore held per
/// PROCESS only. Now each landing writes its facts to a file of its own, two replicas write
/// DISJOINT files, and <see cref="ModuleActivationSidecar.Read"/> derives the head and the #3649
/// fallback from every record present — so every replica folds the same files to the same answer,
/// whatever order the writes landed in. It is <a href="https://github.com/Systemorph/MeshWeaver/issues/2090">#2090</a>'s
/// move one level down: that change split one shared <c>activation.json</c> into a file per module;
/// this one splits a file per module into a file per landing.</para>
///
/// <para>🚨 <b>Every ADDRESSED field is a function of the bytes and the request, and must stay
/// one.</b> The file name is the content address of those fields
/// (<see cref="ModuleActivationSidecar.LandingRecordAddress"/>), so two replicas landing the same
/// bundle write the same name with the same bytes — a benign collision, the property #3656 already
/// relies on one level up — while two landing DIFFERENT content write different names and neither
/// is ever replaced. A field that depends on WHEN or WHERE the landing ran (a timestamp, a replica
/// id, a counter) would give identical re-landings different names and the record set would grow
/// for ever; adding one to the address is a defect. EVERY serialized field is addressed, so a name
/// holds exactly one content. Whether the bytes LOAD is a fact about the platform too, so it is not
/// here at all: each platform records its own measurement (<see cref="ModulePlatformVerdict"/>).
/// <see cref="RecordedAtUtc"/> is the file's own metadata, never serialized.</para>
///
/// <para>🚨 <b>No <c>Previous*</c> and no <c>Enabled</c>.</b> <c>Previous*</c> IS the fallback
/// decision, taken against whichever head one replica happened to observe — exactly what this shape
/// removes; the fallback is derived at read time. <c>Enabled</c> is per MODULE, not per landing:
/// an uninstall is an event of its own (<see cref="ModuleUninstallRecord"/>), ordered against the
/// landings by arrival.</para>
/// </summary>
public sealed record ModuleLandingRecord
{
    /// <summary>The module's DLL name without extension — the identity
    /// <see cref="ModuleActivationEntry.Name"/> carries.</summary>
    public required string Name { get; init; }

    /// <summary>One of <see cref="ModuleActivationSources"/>; the landing lane writes
    /// <see cref="ModuleActivationSources.Store"/>.</summary>
    public string Source { get; init; } = ModuleActivationSources.Store;

    /// <summary>The mesh path of the install record that asked for this landing, when recorded.</summary>
    public string? PackagePath { get; init; }

    /// <summary>The GENERATION directory this landing wrote (<c>&lt;name&gt;@&lt;content id&gt;</c>,
    /// #3656) — what <see cref="ModuleActivationEntry.Directory"/> names when this record is the head.</summary>
    public required string Directory { get; init; }

    /// <summary>The package version these bytes were served at — what a SHELF landing is ordered
    /// by (#3996).</summary>
    public string? Version { get; init; }

    /// <summary>The framework MVID the bytes were built against, as the producer recorded it.</summary>
    public string? FrameworkMvid { get; init; }

    /// <summary>The module's declared platform floor at landing — ADVISORY (#3648).</summary>
    public string? MinMeshVersion { get; init; }

    /// <summary>The producing repository's commit these bytes were built from (#4158) —
    /// diagnostic, never inferred.</summary>
    public string? SourceCommit { get; init; }

    /// <summary>
    /// 🚨 The LANE's head rule, recorded with the landing because the derivation replays it: true
    /// for a SHELF landing (the publish route, <see cref="ModuleLandingService.ShelveModule"/>),
    /// which never displaces a head it ranks strictly below while that head's bytes are present
    /// (#3996); false for an ADOPT landing (<see cref="ModuleLandingService.LandModule"/>), which
    /// always takes the head when it arrives — an older version there is an operator who asked for
    /// it, and the unattended lane refuses one upstream (<see cref="ModuleUpdateAction.SkipOlder"/>).
    /// A function of the REQUEST, so it is part of the address.
    /// </summary>
    public bool YieldsToNewerHead { get; init; }

    /// <summary>
    /// Null for a first arrival. For a RE-arrival — a landing whose record already exists and which
    /// would take the head if it arrived now (an adopt landing re-installing the generation it ran
    /// before, an equal-version rebuild coming back) — the address and arrival stamp of the latest
    /// record with the same facts, so the re-arrival is a NEW file with its own arrival stamp
    /// rather than a touch of an existing one. Two replicas re-landing the same bundle over the
    /// same state compute the same value and collide benignly, like a first arrival does.
    /// </summary>
    public string? ReArrivalOf { get; init; }

    /// <summary>
    /// When this record's file was written — the ARRIVAL order the head derivation replays (#3996's
    /// rule is stated against an arriving upload, and an equal or unorderable version is decided by
    /// arrival). Read from the file system and never serialized: putting it IN the record would
    /// make the address depend on the clock. Every reader reads the same files, so every reader
    /// orders them the same way. <see cref="DateTime.MinValue"/> on a record not read from disk.
    /// </summary>
    [JsonIgnore]
    public DateTime RecordedAtUtc { get; init; }
}

/// <summary>
/// An UNINSTALL, recorded as an event of its own in the module's record directory and never
/// rewritten (#4026, Copilot's review of #4427) — the tombstone that orders an uninstall against
/// the landings around it.
///
/// <para>🚨 <b>Why a file of its own and not just the disabled per-module entry.</b> The per-module
/// entry (<c>activation.d/&lt;Name&gt;.json</c>) is last-writer-wins, and every writer of it writes a
/// PROJECTION decided a moment earlier. A landing that derived "installed" before another replica's
/// uninstall can therefore write its projection AFTER that uninstall and bring the module back —
/// re-reading before writing is not a compare-and-swap, and the store offers none. So whether a
/// module is installed is decided the way its generation is: by the ORDER of immutable events, each
/// stamped by its own arrival. A tombstone newer than every landing record means uninstalled,
/// whatever the per-module file says; a landing record newer than the last tombstone means
/// installed, and only the landings after that tombstone count.</para>
///
/// <para>The content is addressed like a landing record's, so two replicas uninstalling over the
/// same state write one file, and a later uninstall — whose <see cref="After"/> names the event it
/// followed — is a new one.</para>
/// </summary>
public sealed record ModuleUninstallRecord
{
    /// <summary>The module's DLL name without extension.</summary>
    public required string Name { get; init; }

    /// <summary>The latest event (landing record or tombstone) the uninstall observed, as
    /// <c>&lt;file name&gt;@&lt;arrival ticks&gt;</c>, or null when it observed none — what makes a
    /// second uninstall of the same module a new file rather than a no-op.</summary>
    public string? After { get; init; }

    /// <summary>When this tombstone's file was written — its ARRIVAL. File metadata, never
    /// serialized.</summary>
    [JsonIgnore]
    public DateTime RecordedAtUtc { get; init; }
}

/// <summary>
/// One MEASUREMENT of whether a generation's bytes link on one PLATFORM build (#4026, Copilot's
/// review of #4427) — what the fallback rank reads as "loadable here", for the platform the reader
/// runs.
///
/// <para>🚨 <b>Per platform, because loadability is a fact about the bytes AND the image.</b> A
/// rolling update puts two images on one volume, and a generation one image cannot load may load on
/// the next. A verdict stored once per generation — by whichever replica recorded it first — would
/// keep every image ranking by that first image's answer, for the length of the roll or for good.
/// So each landing records its own image's measurement in a file of its own, addressed by the whole
/// content (generation, platform, verdict, and the verdict it supersedes), and a reader ranks by the
/// NEWEST measurement for its own platform. A generation no replica of the reader's platform has
/// measured ranks as UNKNOWN — between a measured "loads" and a measured "does not load" — never as
/// another image's answer.</para>
///
/// <para>The platform key is the live framework identity (the same one a producer records beside
/// its bytes), so two replicas of one image write the same measurement to the same name. A
/// re-measurement that DIFFERS from the newest one for its platform (a sibling module it references
/// landed since) names the verdict it supersedes, so it is a new file and the newest measurement
/// wins by arrival.</para>
/// </summary>
public sealed record ModulePlatformVerdict
{
    /// <summary>The module's DLL name without extension.</summary>
    public required string Name { get; init; }

    /// <summary>The generation directory measured.</summary>
    public required string Directory { get; init; }

    /// <summary>The platform build the measurement was taken on — the live framework identity.</summary>
    public required string Platform { get; init; }

    /// <summary>True when the link probe said the bytes load on <see cref="Platform"/>.</summary>
    public bool Linkable { get; init; }

    /// <summary>The address of the newest verdict for the same generation and platform that this
    /// one replaces, or null for the first measurement.</summary>
    public string? Supersedes { get; init; }

    /// <summary>When this verdict's file was written. File metadata, never serialized.</summary>
    [JsonIgnore]
    public DateTime RecordedAtUtc { get; init; }
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

    /// <summary>
    /// 🚨 The modules this instance's PLAN keeps it from installing (#4097) — one per
    /// <c>activation.d/&lt;Module&gt;.tier-refused</c> marker, written by the unattended default
    /// install from the registry's typed answer (<see cref="Mesh.Security.PlanTierRefusal"/>).
    /// Read from the markers, never from the aggregate file (<see cref="JsonIgnoreAttribute"/>),
    /// so a bulk <see cref="ModuleActivationSidecar.Write"/> neither persists nor resurrects one.
    ///
    /// <para>Carried on the activation list because the list is what every surface that says
    /// "not installed" already reads — <c>RequiredModuleStatus.Classify</c> on <c>/health</c>,
    /// the activation report on the package card — so the refusal reaches them with no new
    /// parameter on a host that was compiled against the previous platform.</para>
    /// </summary>
    [JsonIgnore]
    public ImmutableList<Mesh.Security.PlanTierRefusal> TierRefusals { get; init; } = [];

    /// <summary>The plan-tier refusal recorded for <paramref name="moduleName"/>, or null.</summary>
    public Mesh.Security.PlanTierRefusal? TierRefusalFor(string? moduleName) =>
        TierRefusals.FirstOrDefault(r => r.IsForModule(moduleName));
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
/// structurally impossible. Two landings of the SAME module do share that module's file, but can
/// no longer cost any OTHER module its entry. Within one process they are serialised, and
/// <see cref="ModuleLandingService"/> decides their semantic order before it writes: on the
/// registry shelf an older arrival cannot replace a newer head whose bytes are present (#3996).
/// Across REPLICAS a same-module pair used to be last-writer-wins on this file (#4026), so a
/// landing no longer DECIDES through it: each landing writes an immutable record of its own
/// (<see cref="ModuleLandingRecord"/>, <c>activation.d/&lt;Name&gt;/…</c>) and <see cref="Read"/>
/// DERIVES the head and fallback from the records present — and whether the module is installed at
/// all, from the order of its landing records and uninstall tombstones. The per-module file is still
/// written — it is what images that predate the records read — but a current image marks it as a
/// projection (<see cref="ModuleActivationEntry.ProjectionOf"/>) and, for a module that has records,
/// never takes a decision from it. A per-entry file
/// that cannot be read costs exactly that one entry, reported loudly, instead of the whole
/// deployment's module set.</para>
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
    /// file, the per-module file winning by name — and then, for every module that has landing
    /// records (<see cref="ModuleLandingRecord"/>, #4026), whether it is installed and which
    /// generation it runs, DERIVED from the ordered records and tombstones, with the fallback ranked
    /// by this process's own platform.
    ///
    /// <para>An ABSENT file — either kind — is the normal fresh-deployment state and contributes
    /// nothing, silently. An UNREADABLE one is reported through <paramref name="onCorrupt"/> and
    /// contributes nothing, so the skip is loud rather than silent — but 🚨 it no longer costs the
    /// OTHER entries: one bad file used to collapse the entire answer to the empty list, which is
    /// how a transient SMB read fault booted a pod with none of its store modules (#2189). The
    /// caller still gets everything that WAS readable, plus one report per file that was not.</para>
    ///
    /// <para>A module with no landing records and no tombstones answers exactly as it did before
    /// #4026, byte for byte — which is every module on a deployment until its first landing or
    /// uninstall on an image that writes them.</para>
    /// </summary>
    public static ModuleActivationList Read(string baseDirectory, Action<string>? onCorrupt = null)
        => ReadFor(baseDirectory, onCorrupt, LivePlatform);

    /// <summary><see cref="Read(string, Action{string}?)"/> for a stated platform — the one a
    /// landing service measures against, and the one a test stands a second image up with. A
    /// separate NAME, not an overload of <c>Read</c>: an overload would make every bare
    /// <c>cref</c> to <c>Read</c> ambiguous (CS0419) in this assembly and in every assembly that
    /// sees its internals.</summary>
    internal static ModuleActivationList ReadFor(
        string baseDirectory, Action<string>? onCorrupt, Func<string?> platform)
        => ApplyLandingRecords(baseDirectory, ReadStored(baseDirectory, onCorrupt), onCorrupt, platform);

    /// <summary>The platform this process runs: the live framework identity — the same key a
    /// producer records beside its bytes. Resolved only when a verdict is actually consulted.</summary>
    internal static string? LivePlatform() =>
        MeshWeaver.Graph.Configuration.PrebuiltAssemblySeeder.LiveFrameworkMvid;

    /// <summary>
    /// The STORED layers only — the legacy aggregate and the per-module entry files, the latter
    /// winning by name — with no landing record applied and no boot marker attached. What an
    /// image that predates #4026 reads as its whole answer, and what the modules GC keeps
    /// referenced for exactly that reason (a rolled-back replica boots from it).
    /// </summary>
    internal static ModuleActivationList ReadStored(string baseDirectory, Action<string>? onCorrupt = null)
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
            Entries = [.. order.Select(name => byName[name])],
            // The marker is authoritative; the legacy flag is honoured once, for a deployment
            // upgrading with the flag still set. Boot clears both.
            PendingRestart = File.Exists(PendingRestartMarkerPath(baseDirectory)) || legacy.PendingRestart,
            // #4097 — what the registry said this instance's plan refuses, from the markers the
            // default install keeps in step with the registry's answer.
            TierRefusals = [.. ReadTierRefusals(baseDirectory).Values],
        };
    }

    /// <summary>
    /// Applies the landing records to the stored layers (#4026): every module that has landing
    /// records or tombstones gets the entry <see cref="DeriveEntry"/> computes from them, for
    /// <paramref name="platform"/>; a module with neither keeps its stored entry untouched; and the
    /// boot's unloadable measurement (#3650) rides along last, against the head as derived.
    /// </summary>
    internal static ModuleActivationList ApplyLandingRecords(
        string baseDirectory, ModuleActivationList stored, Action<string>? onCorrupt, Func<string?> platform)
    {
        var byName = new Dictionary<string, ModuleActivationEntry>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var entry in stored.Entries)
        {
            if (!byName.ContainsKey(entry.Name))
                order.Add(entry.Name);
            byName[entry.Name] = entry;
        }

        var present = GenerationPresence(baseDirectory);
        foreach (var moduleName in ModuleNamesWithLandingRecords(baseDirectory, onCorrupt))
        {
            var log = ReadModuleLog(baseDirectory, moduleName, onCorrupt);
            if (log.IsEmpty)
                continue;
            byName.TryGetValue(moduleName, out var entry);
            var name = entry?.Name ?? moduleName;
            var derived = DeriveEntry(name, entry, StoredAt(baseDirectory, name), log.Records, log.Uninstalls,
                generation => present(name, generation), log.LinkableOn(platform));
            if (derived is null)
                continue;
            if (!byName.ContainsKey(name))
                order.Add(name);
            byName[name] = derived;
        }

        return stored with
        {
            // #3650 — the boot's measurement of each head generation rides along, from the
            // per-module marker file, only while the head is still the generation it measured.
            // ProjectionOf is the per-module FILE's bookkeeping and never leaves this class.
            Entries = [.. order.Select(name =>
                WithUnloadableMarker(baseDirectory, byName[name] with { ProjectionOf = null }))],
        };
    }

    /// <summary>
    /// 🚨 Every generation SOME reader could run or fall back to — the modules GC's reference set
    /// (#4026). The fallback is ranked per PLATFORM (<see cref="ModulePlatformVerdict"/>), and a
    /// rolling update has two images live on one volume, so a GC pass on either must keep the
    /// fallback of EVERY platform it has a verdict for, plus the one a platform with no verdicts
    /// ranks — and the generations the stored entries name, which an image that predates the
    /// records boots from. A superset of what any reader reads, so no pass ever reclaims bytes one
    /// of them needs.
    /// </summary>
    internal static ImmutableHashSet<string> ReferencedGenerations(
        string baseDirectory, ModuleActivationList stored, Action<string>? onCorrupt)
    {
        var referenced = ImmutableHashSet.CreateBuilder<string>(StringComparer.OrdinalIgnoreCase);
        void Add(ModuleActivationEntry? entry)
        {
            if (!string.IsNullOrWhiteSpace(entry?.Directory))
                referenced.Add(entry.Directory!);
            if (!string.IsNullOrWhiteSpace(entry?.PreviousDirectory))
                referenced.Add(entry.PreviousDirectory!);
        }
        foreach (var entry in stored.Entries)
            Add(entry);
        var present = GenerationPresence(baseDirectory);
        foreach (var moduleName in ModuleNamesWithLandingRecords(baseDirectory, onCorrupt))
        {
            var log = ReadModuleLog(baseDirectory, moduleName, onCorrupt);
            if (log.IsEmpty)
                continue;
            var entry = stored.Entries.FirstOrDefault(e =>
                string.Equals(e.Name, moduleName, StringComparison.OrdinalIgnoreCase));
            var name = entry?.Name ?? moduleName;
            var storedAt = StoredAt(baseDirectory, name);
            foreach (var platform in log.Platforms)
                Add(DeriveEntry(name, entry, storedAt, log.Records, log.Uninstalls,
                    generation => present(name, generation), log.LinkableOn(() => platform)));
        }
        return referenced.ToImmutable();
    }

    // ── the per-landing records, and the head DERIVED from them (#4026) ──────────────────

    /// <summary>
    /// The directory ONE module's records live in: <c>modules/activation.d/&lt;Name&gt;/</c> —
    /// landing records (<c>*.json</c>), uninstall tombstones (<c>*.tombstone</c>) and platform
    /// verdicts (<c>*.verdict</c>). A subdirectory, deliberately: every image that predates the
    /// records enumerates <c>activation.d/*.json</c> at the TOP level only, so a record is invisible
    /// to it rather than misread as an entry.
    /// </summary>
    public static string LandingRecordsDirectory(string baseDirectory, string moduleName)
    {
        ValidateModuleName(moduleName);
        return Path.Combine(EntriesDirectory(baseDirectory), moduleName);
    }

    /// <summary>The suffix of an uninstall tombstone inside a module's record directory.</summary>
    public const string UninstallSuffix = ".tombstone";

    /// <summary>The suffix of a platform verdict inside a module's record directory.</summary>
    public const string VerdictSuffix = ".verdict";

    /// <summary>
    /// 🚨 The CONTENT ADDRESS of one landing record: the FULL SHA-256, in lowercase hex, over the
    /// record's fields in a fixed order, each length-prefixed so no two different field tuples can
    /// spell the same string. EVERY serialized field is addressed, so a name holds exactly one
    /// content — the property that makes a racing create benign.
    ///
    /// <para><b>The full digest, not the 16-hex truncation the generation leaf uses.</b> 64 bits
    /// cannot be called collision-free, and a collision HERE would let one replica's record stand
    /// for another's — the lost update this design removes, back in a smaller window. The generation
    /// leaf can afford the truncation because a collision there is two payloads sharing a
    /// directory, which the bytes themselves would betray; a record has no second check.</para>
    ///
    /// <para><b>A written-out canonical string, not the serialized JSON.</b> The JSON's property
    /// order is the CLR type's declaration order, so adding or reordering a property would
    /// silently re-address every record and two images in a rolling update would stop agreeing.
    /// The fields are listed here on purpose: extending the record means extending this list on
    /// purpose, and a field that is not a function of the bytes and the request never joins it.</para>
    /// </summary>
    public static string LandingRecordAddress(ModuleLandingRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return Address(
            "module-landing/1",
            record.Name,
            record.Source,
            record.PackagePath,
            record.Directory,
            record.Version,
            record.FrameworkMvid,
            record.MinMeshVersion,
            record.SourceCommit,
            record.YieldsToNewerHead ? "yields" : "takes",
            record.ReArrivalOf);
    }

    /// <summary>The content address of an uninstall tombstone — every serialized field.</summary>
    public static string UninstallAddress(ModuleUninstallRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return Address("module-uninstall/1", record.Name, record.After);
    }

    /// <summary>The content address of a platform verdict — every serialized field, the verdict
    /// itself included, so two different measurements can never share a name.</summary>
    public static string VerdictAddress(ModulePlatformVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(verdict);
        return Address(
            "module-verdict/1",
            verdict.Name,
            verdict.Directory,
            verdict.Platform,
            verdict.Linkable ? "links" : "does-not-link",
            verdict.Supersedes);
    }

    private static string Address(params string?[] fields)
    {
        static string Field(string? value) => value is null ? "~" : value.Length + ":" + value;
        var canonical = string.Join('\n', fields.Select(Field));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    /// <summary>The file ONE landing's record lives in:
    /// <c>modules/activation.d/&lt;Name&gt;/&lt;generation&gt;.&lt;record address&gt;.json</c>. The
    /// generation leads, so a reader sees which bytes a record is about from its name; the address
    /// follows, so two landings of ONE generation under different version LABELS — which the
    /// content-addressed generation deliberately collapses into one directory — still get a file
    /// each.</summary>
    public static string LandingRecordPath(string baseDirectory, ModuleLandingRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        ValidateGeneration(record.Directory);
        return Path.Combine(
            LandingRecordsDirectory(baseDirectory, record.Name),
            record.Directory + "." + LandingRecordAddress(record) + ".json");
    }

    private static string UninstallPath(string baseDirectory, ModuleUninstallRecord record) =>
        Path.Combine(LandingRecordsDirectory(baseDirectory, record.Name),
            "uninstalled." + UninstallAddress(record) + UninstallSuffix);

    private static string VerdictPath(string baseDirectory, ModulePlatformVerdict verdict)
    {
        ValidateGeneration(verdict.Directory);
        return Path.Combine(LandingRecordsDirectory(baseDirectory, verdict.Name),
            verdict.Directory + "." + VerdictAddress(verdict) + VerdictSuffix);
    }

    private static void ValidateGeneration(string? generation)
    {
        if (string.IsNullOrWhiteSpace(generation)
            || generation is "." or ".."
            || generation.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || generation.Contains('/') || generation.Contains('\\'))
            throw new ArgumentException(
                $"'{generation}' is not a valid generation leaf — a landing record is a file named "
                + "after its generation.", nameof(generation));
    }

    /// <summary>
    /// Records ONE landing. Never a replace of anything that differs: the name is the record's own
    /// content address, so a file already carrying it already carries this record, and the call
    /// writes nothing.
    ///
    /// <para>🚨 <b>That is the whole fix for <a href="https://github.com/Systemorph/MeshWeaver/issues/4026">#4026</a>.</b>
    /// Two replicas landing DIFFERENT content of one module write different names, so neither can
    /// lose the other's landing — there is no shared cell left to have a lost update on. Two
    /// writing the SAME record race for one name, and whichever wins wrote identical bytes. The
    /// create does not need to be atomic for that to hold, which matters: .NET's no-overwrite move
    /// is <c>link(2)</c> where the file system supports it and an existence check plus
    /// <c>rename(2)</c> where it does not (a CIFS mount), and the design survives both.</para>
    /// </summary>
    /// <returns>True when this call wrote the file; false when the record was already on the
    /// volume — an idempotent re-landing, never an error.</returns>
    public static bool WriteLanding(string baseDirectory, ModuleLandingRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return WriteOnce(baseDirectory, record.Name, LandingRecordPath(baseDirectory, record),
            JsonSerializer.Serialize(record with { RecordedAtUtc = default }, Json));
    }

    /// <summary>
    /// Records an UNINSTALL as a tombstone of its own (<see cref="ModuleUninstallRecord"/>): an
    /// event every current reader orders against the landing records by arrival, which a stale
    /// per-module projection cannot overwrite.
    /// </summary>
    /// <returns>The tombstone's file name.</returns>
    public static string WriteUninstall(string baseDirectory, string moduleName, string? after)
    {
        var record = new ModuleUninstallRecord { Name = moduleName, After = after };
        var path = UninstallPath(baseDirectory, record);
        WriteOnce(baseDirectory, moduleName, path, JsonSerializer.Serialize(record, Json));
        return Path.GetFileName(path);
    }

    /// <summary>
    /// Records what THIS platform measured about a generation's bytes
    /// (<see cref="ModulePlatformVerdict"/>). Writes nothing when the newest verdict for this
    /// generation and platform already says the same; otherwise a new file superseding it, so a
    /// later measurement on the same platform wins by arrival and a measurement on ANOTHER platform
    /// never touches this one's.
    /// </summary>
    /// <returns>True when a verdict file was written.</returns>
    public static bool WriteVerdict(
        string baseDirectory, string moduleName, string generation, string platform, bool linkable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(platform);
        var newest = NewestVerdict(
            ReadVerdictFiles(baseDirectory, moduleName, onCorrupt: null).Select(f => f.Record),
            generation, platform);
        if (newest is not null && newest.Linkable == linkable)
            return false;
        var verdict = new ModulePlatformVerdict
        {
            Name = moduleName,
            Directory = generation,
            Platform = platform,
            Linkable = linkable,
            Supersedes = newest is null ? null : VerdictAddress(newest),
        };
        return WriteOnce(baseDirectory, moduleName, VerdictPath(baseDirectory, verdict),
            JsonSerializer.Serialize(verdict, Json));
    }

    /// <summary>
    /// The one immutable create every record kind goes through: skip when the name exists (it
    /// already holds this content), otherwise write a temp file and move it into place without
    /// overwriting. The temp is written into <c>activation.d/</c> itself, not into the module's
    /// record directory, so the rename moves <c>activation.d/</c>'s last-write time — the
    /// fingerprint <see cref="PendingModuleActivations"/> memoises the activation read behind.
    /// </summary>
    private static bool WriteOnce(string baseDirectory, string moduleName, string path, string content)
    {
        if (File.Exists(path))
            return false;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = Path.Combine(
            EntriesDirectory(baseDirectory),
            "." + moduleName + "." + Guid.NewGuid().ToString("N") + LandingTempSuffix);
        File.WriteAllText(temp, content);
        try
        {
            File.Move(temp, path, overwrite: false);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            DeleteTemp(temp);
            if (File.Exists(path))
                return false; // another replica wrote this very content first
            throw;
        }
    }

    /// <summary>The suffix of a record's temp file inside <c>activation.d/</c> — matched by none
    /// of the enumerations (<c>*.json</c>, the marker suffixes), so a temp orphaned by a crash is
    /// never read, and the modules GC removes it once it is older than its grace.</summary>
    public const string LandingTempSuffix = ".landing.tmp";

    /// <summary>
    /// Every landing record of ONE module, each stamped with its file's write time as its arrival
    /// (<see cref="ModuleLandingRecord.RecordedAtUtc"/>). An unreadable or inconsistent record —
    /// one whose content does not hash to its own name — costs exactly itself, reported through
    /// <paramref name="onCorrupt"/>: the per-file rule of #2189, for the same reason.
    /// </summary>
    public static ImmutableList<ModuleLandingRecord> ReadLandings(
        string baseDirectory, string moduleName, Action<string>? onCorrupt = null) =>
        [.. ReadLandingFiles(baseDirectory, moduleName, onCorrupt).Select(x => x.Record)];

    private static ImmutableList<(string Path, ModuleLandingRecord Record)> ReadLandingFiles(
        string baseDirectory, string moduleName, Action<string>? onCorrupt) =>
        ReadRecordFiles<ModuleLandingRecord>(baseDirectory, moduleName, "*.json", onCorrupt,
            record => record.Directory + "." + LandingRecordAddress(record) + ".json",
            (record, at) => record with { RecordedAtUtc = at },
            record => record.Name);

    private static ImmutableList<(string Path, ModuleUninstallRecord Record)> ReadUninstallFiles(
        string baseDirectory, string moduleName, Action<string>? onCorrupt) =>
        ReadRecordFiles<ModuleUninstallRecord>(baseDirectory, moduleName, "*" + UninstallSuffix, onCorrupt,
            record => "uninstalled." + UninstallAddress(record) + UninstallSuffix,
            (record, at) => record with { RecordedAtUtc = at },
            record => record.Name);

    private static ImmutableList<(string Path, ModulePlatformVerdict Record)> ReadVerdictFiles(
        string baseDirectory, string moduleName, Action<string>? onCorrupt) =>
        ReadRecordFiles<ModulePlatformVerdict>(baseDirectory, moduleName, "*" + VerdictSuffix, onCorrupt,
            record => record.Directory + "." + VerdictAddress(record) + VerdictSuffix,
            (record, at) => record with { RecordedAtUtc = at },
            record => record.Name);

    private static ImmutableList<(string Path, T Record)> ReadRecordFiles<T>(
        string baseDirectory, string moduleName, string pattern, Action<string>? onCorrupt,
        Func<T, string> expectedName, Func<T, DateTime, T> stamp, Func<T, string> nameOf)
        where T : class
    {
        if (!IsValidModuleName(moduleName))
            return [];
        var directory = Path.Combine(EntriesDirectory(baseDirectory), moduleName);
        FileInfo[] files;
        try
        {
            files = Directory.Exists(directory)
                ? [.. new DirectoryInfo(directory).EnumerateFiles(pattern)
                    .OrderBy(f => f.Name, StringComparer.Ordinal)]
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            onCorrupt?.Invoke(
                $"Module records under '{directory}' could not be listed "
                + $"({ex.GetType().Name}: {ex.Message}) — module '{moduleName}' is read from its "
                + "stored entry alone until the volume is readable again.");
            return [];
        }

        var builder = ImmutableList.CreateBuilder<(string, T)>();
        foreach (var file in files)
        {
            try
            {
                var text = TryReadAllText(file.FullName);
                if (text is null)
                    // Vanished between the listing and the read — the modules GC retiring a record
                    // the derivation no longer needs. Absence, not corruption.
                    continue;
                var record = JsonSerializer.Deserialize<T>(text, Json);
                if (record is null
                    || !string.Equals(nameOf(record), moduleName, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(file.Name, expectedName(record), StringComparison.Ordinal))
                {
                    onCorrupt?.Invoke(
                        $"Module record '{file.FullName}' does not match its own name — its content "
                        + "is not the record the name addresses, so it is skipped; every other record "
                        + $"of '{moduleName}' is unaffected.");
                    continue;
                }
                builder.Add((file.FullName, stamp(record, file.LastWriteTimeUtc)));
            }
            // EVERY failure, not only a parse error — an SMB sharing violation arrives as
            // IOException, and letting it escape would fail the whole activation read, which is
            // #2189 restored. It costs the one record it names, loudly.
            catch (Exception ex)
            {
                onCorrupt?.Invoke(
                    $"Module record '{file.FullName}' could not be read ({ex.GetType().Name}: "
                    + $"{ex.Message}) — that ONE record is skipped; every other record of "
                    + $"'{moduleName}' is unaffected.");
            }
        }
        return builder.ToImmutable();
    }

    /// <summary>Every module name that has a record directory on this volume.</summary>
    public static ImmutableList<string> ModuleNamesWithLandingRecords(
        string baseDirectory, Action<string>? onCorrupt = null)
    {
        var directory = EntriesDirectory(baseDirectory);
        try
        {
            return Directory.Exists(directory)
                ? [.. Directory.EnumerateDirectories(directory)
                    .Select(Path.GetFileName)
                    .Where(IsValidModuleName)
                    .Select(name => name!)
                    .OrderBy(name => name, StringComparer.Ordinal)]
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            onCorrupt?.Invoke(
                $"Module record directories under '{directory}' could not be listed "
                + $"({ex.GetType().Name}: {ex.Message}) — modules are read from their stored "
                + "entries alone until the volume is readable again.");
            return [];
        }
    }

    /// <summary>One module's whole record directory, read once: its landing records, its uninstall
    /// tombstones and its platform verdicts.</summary>
    internal sealed record ModuleLog(
        ImmutableList<ModuleLandingRecord> Records,
        ImmutableList<ModuleUninstallRecord> Uninstalls,
        ImmutableList<ModulePlatformVerdict> Verdicts)
    {
        /// <summary>No landing and no uninstall: the directory decides nothing, and the stored
        /// entry answers as it did before #4026.</summary>
        public bool IsEmpty => Records.IsEmpty && Uninstalls.IsEmpty;

        /// <summary>Every platform a verdict was recorded for, plus null — the platform nobody
        /// measured, which ranks every generation as unknown.</summary>
        public IEnumerable<string?> Platforms =>
            Verdicts.Select(v => (string?)v.Platform).Distinct(StringComparer.Ordinal).Append(null);

        /// <summary>The newest verdict for a generation on the platform <paramref name="platform"/>
        /// names — resolved only if a verdict exists at all — or null when that platform never
        /// measured it.</summary>
        public Func<string, bool?> LinkableOn(Func<string?> platform)
        {
            if (Verdicts.IsEmpty)
                return _ => null;
            string? resolved = null;
            var asked = false;
            return generation =>
            {
                if (!asked)
                {
                    resolved = platform();
                    asked = true;
                }
                return string.IsNullOrWhiteSpace(resolved)
                    ? null
                    : NewestVerdict(Verdicts, generation, resolved)?.Linkable;
            };
        }
    }

    private static ModulePlatformVerdict? NewestVerdict(
        IEnumerable<ModulePlatformVerdict> verdicts, string generation, string platform) =>
        verdicts
            .Where(v => SameGeneration(v.Directory, generation)
                && string.Equals(v.Platform, platform, StringComparison.Ordinal))
            .OrderByDescending(v => v.RecordedAtUtc)
            .ThenByDescending(VerdictAddress, StringComparer.Ordinal)
            .FirstOrDefault();

    private static ModuleLog ReadModuleLog(string baseDirectory, string moduleName, Action<string>? onCorrupt) =>
        new([.. ReadLandingFiles(baseDirectory, moduleName, onCorrupt).Select(f => f.Record)],
            [.. ReadUninstallFiles(baseDirectory, moduleName, onCorrupt).Select(f => f.Record)],
            [.. ReadVerdictFiles(baseDirectory, moduleName, onCorrupt).Select(f => f.Record)]);

    /// <summary>When the stored entry for a module was last written: its per-module file's write
    /// time, or the legacy aggregate's when only that carries it; null when neither exists.</summary>
    private static DateTime? StoredAt(string baseDirectory, string moduleName)
    {
        var perModule = EntryPath(baseDirectory, moduleName);
        if (File.Exists(perModule))
            return File.GetLastWriteTimeUtc(perModule);
        var aggregate = SidecarPath(baseDirectory);
        return File.Exists(aggregate) ? File.GetLastWriteTimeUtc(aggregate) : null;
    }

    /// <summary>
    /// One module's activation state as the landing lane needs it: the STORED entry (what images
    /// that predate the records read) and when it was written, the module's record directory, and
    /// the entry DERIVED from all of it for this process's platform.
    /// </summary>
    internal sealed record ModuleHeadState(
        ModuleActivationEntry? Stored,
        DateTime? StoredAtUtc,
        ModuleLog Log,
        ModuleActivationEntry? Derived);

    /// <summary>Reads <see cref="ModuleHeadState"/> for one module.</summary>
    internal static ModuleHeadState ReadModuleHead(
        string baseDirectory, string moduleName, Action<string>? onCorrupt, Func<string?> platform)
    {
        var stored = ReadStored(baseDirectory, onCorrupt).Entries.FirstOrDefault(e =>
            string.Equals(e.Name, moduleName, StringComparison.OrdinalIgnoreCase));
        var name = stored?.Name ?? moduleName;
        var storedAt = StoredAt(baseDirectory, name);
        var log = ReadModuleLog(baseDirectory, moduleName, onCorrupt);
        var present = GenerationPresence(baseDirectory);
        return new ModuleHeadState(stored, storedAt, log,
            DeriveEntry(name, stored, storedAt, log.Records, log.Uninstalls,
                generation => present(name, generation), log.LinkableOn(platform)));
    }

    /// <summary>The newest event in a module's record directory — landing or tombstone — as
    /// <c>&lt;file name&gt;@&lt;arrival ticks&gt;</c>, or null when there is none. What a projection
    /// states it was derived from, and what an uninstall states it followed.</summary>
    internal static string? LatestEventName(ModuleHeadState state) =>
        state.Log.Records
            .Select(r => (At: r.RecordedAtUtc, Name: r.Directory + "." + LandingRecordAddress(r) + ".json"))
            .Concat(state.Log.Uninstalls.Select(u =>
                (At: u.RecordedAtUtc, Name: "uninstalled." + UninstallAddress(u) + UninstallSuffix)))
            .OrderByDescending(e => e.At)
            .ThenByDescending(e => e.Name, StringComparer.Ordinal)
            .Select(e => e.Name + "@" + e.At.Ticks)
            .FirstOrDefault();

    /// <summary>
    /// The record a RE-arrival must write, or null when this landing needs none (#4026). Called
    /// when <see cref="WriteLanding"/> found the landing's record already on the volume: the same
    /// bundle landed before. That is a no-op whenever the answer would not change — an identical
    /// re-publish of the head, an older shelf upload that stays shelf-only — and a genuine event
    /// when it would: an adopt landing re-installing the generation this deployment ran before
    /// (the Store's rollback), or any landing after an uninstall that followed its first arrival.
    /// Such a landing gets a NEW record, whose <see cref="ModuleLandingRecord.ReArrivalOf"/> names
    /// the latest record with the same facts, so the re-arrival carries its own arrival stamp
    /// without rewriting an existing file.
    /// </summary>
    internal static ModuleLandingRecord? ReArrival(
        string baseDirectory, ModuleHeadState state, ModuleLandingRecord incoming, DateTime nowUtc,
        Func<string?> platform)
    {
        if (state.Derived is { Enabled: true } head
            && SameGeneration(head.Directory, incoming.Directory)
            && string.Equals(head.Version, incoming.Version, StringComparison.Ordinal))
            return null;
        var facts = incoming with { ReArrivalOf = null, RecordedAtUtc = default };
        var latest = state.Log.Records
            .Where(r => r with { ReArrivalOf = null, RecordedAtUtc = default } == facts)
            .OrderByDescending(r => r.RecordedAtUtc)
            .ThenBy(LandingRecordAddress, StringComparer.Ordinal)
            .FirstOrDefault();
        if (latest is null)
            return null;
        var candidate = incoming with
        {
            ReArrivalOf = LandingRecordAddress(latest) + "@" + latest.RecordedAtUtc.Ticks,
            RecordedAtUtc = nowUtc,
        };
        var present = GenerationPresence(baseDirectory);
        var hypothetical = DeriveEntry(incoming.Name, state.Stored, state.StoredAtUtc,
            state.Log.Records.Add(candidate), state.Log.Uninstalls,
            generation => present(incoming.Name, generation), state.Log.LinkableOn(platform));
        // Only a re-arrival that CHANGES the answer, and changes it to this landing, is written: a
        // lower label of the head's own bytes re-published folds to the entry already derived, and
        // writing a record for it would grow the set by a file per publish for nothing.
        return hypothetical is { Enabled: true }
               && !hypothetical.Equals(state.Derived)
               && SameGeneration(hypothetical.Directory, incoming.Directory)
            ? candidate
            : null;
    }

    /// <summary>
    /// 🚨 THE DERIVATION (#4026): whether one module is installed, and if so its head generation
    /// and its #3649 fallback — computed from the module's ordered events, never read from a
    /// pointer some replica may have replaced.
    ///
    /// <para><b>Installed or not is the ORDER of events.</b> The events are the landing records and
    /// the uninstall tombstones, each at its own arrival, plus the stored entry when an image that
    /// predates the records wrote it (it carries no <see cref="ModuleActivationEntry.ProjectionOf"/>):
    /// an older image's uninstall is then an uninstall at the file's write time, and its landing a
    /// landing of the head it names. A stored entry a CURRENT image wrote is only a projection and
    /// is never an event — which is what keeps a stale projection, written after an uninstall it
    /// did not see, from bringing the module back. The newest uninstall ends everything before it:
    /// the module is uninstalled when nothing landed after it, and only the landings after it
    /// count when something did.</para>
    ///
    /// <para><b>The head is a REPLAY, not a sort.</b> #3996's rule — <i>a shelf upload never
    /// displaces a head it ranks strictly below while that head's bytes are present</i> — is stated
    /// against an ARRIVING upload and is deliberately not a total order: an unversioned or
    /// non-SemVer label is absence of evidence and moves the head, an equal version moves it (a
    /// rebuild), and an adopt landing always moves it. So the live landings are folded in ARRIVAL
    /// order through exactly that predicate, which reproduces what a serialised sequence of the
    /// same landings would have produced — and, because every replica folds the same files with the
    /// same stamps, every replica reaches the same head, whatever image it runs.</para>
    ///
    /// <para><b>The fallback is a RANK</b> over every other live generation: bytes present first,
    /// then loadable on the READER's platform (<paramref name="linkable"/> — measured "loads" above
    /// "never measured here" above measured "does not load"), then the higher version, ties keeping
    /// the earlier arrival. The only platform-dependent part of the answer, by design.</para>
    ///
    /// <para><b>Generations a projection names that no record does</b> (a pre-records head, carried
    /// forward) join as the EARLIEST arrivals, so the first landing on a new image keeps the
    /// fallback the deployment had and no migration pass rewrites anything.</para>
    /// </summary>
    /// <param name="name">The module name.</param>
    /// <param name="stored">The stored entry, enabled or not.</param>
    /// <param name="storedAtUtc">When the stored entry's file was written.</param>
    /// <param name="records">The module's landing records.</param>
    /// <param name="uninstalls">The module's uninstall tombstones.</param>
    /// <param name="present">Whether a generation's entry DLL is on the volume.</param>
    /// <param name="linkable">The reader's platform's newest verdict for a generation; null when
    /// never measured there.</param>
    /// <returns>The derived entry, or null when the record directory decides nothing (no landing
    /// and no uninstall), in which case the stored entry stands.</returns>
    internal static ModuleActivationEntry? DeriveEntry(
        string name, ModuleActivationEntry? stored, DateTime? storedAtUtc,
        IReadOnlyList<ModuleLandingRecord> records, IReadOnlyList<ModuleUninstallRecord> uninstalls,
        Func<string, bool> present, Func<string, bool?> linkable)
    {
        if (records.Count == 0 && uninstalls.Count == 0)
            return null;

        // An entry an image that predates the records wrote is an EVENT at the file's write time;
        // one a current image wrote is a projection of events and decides nothing on its own.
        var olderImageAt = stored is not null && stored.ProjectionOf is null ? storedAtUtc : null;
        DateTime? lastUninstall = uninstalls.Count == 0 ? null : uninstalls.Max(u => u.RecordedAtUtc);
        if (stored is { Enabled: false } && olderImageAt is { } uninstalledAt
            && (lastUninstall is null || uninstalledAt > lastUninstall))
            lastUninstall = uninstalledAt;
        bool Live(DateTime arrival) => lastUninstall is null || arrival > lastUninstall;

        var candidates = ImmutableList.CreateBuilder<ModuleLandingRecord>();
        candidates.AddRange(records.Where(r => Live(r.RecordedAtUtc)));
        foreach (var carried in StoredCandidates(stored, olderImageAt, records))
            if (Live(carried.RecordedAtUtc))
                candidates.Add(carried);

        if (candidates.Count == 0)
        {
            if (lastUninstall is null)
                return null;
            // UNINSTALLED: nothing landed after the newest uninstall. No generation is held, so the
            // modules GC is free to reclaim them, exactly as the pre-#4026 uninstall entry read.
            var latest = records.OrderByDescending(r => r.RecordedAtUtc).FirstOrDefault();
            return (stored ?? new ModuleActivationEntry
            {
                Name = name,
                Source = latest?.Source ?? ModuleActivationSources.Store,
                PackagePath = latest?.PackagePath,
                Version = latest?.Version,
                FrameworkMvid = latest?.FrameworkMvid,
            }) with
            {
                Name = name,
                Enabled = false,
                Directory = null,
                PreviousDirectory = null,
                PreviousVersion = null,
                PreviousFrameworkMvid = null,
                PreviousSourceCommit = null,
                ProjectionOf = null,
                UnloadableFrameworkMvid = null,
            };
        }

        // ONE candidate per GENERATION. The leaf is a CONTENT address (#3656), so a rebuild of
        // unchanged source republished under another label is one directory and two records —
        // deliberately, since a label is not content. The higher label is the one the head keeps
        // ("the head keeps its (higher) label", #4031 review); an equal label keeps the later
        // arrival, which is what a re-arrival is.
        var arrival = candidates
            .GroupBy(r => r.Directory, StringComparer.OrdinalIgnoreCase)
            .Select(g => g
                .OrderByDescending(r => r.Version, VersionRank.Instance)
                .ThenByDescending(r => r.RecordedAtUtc)
                .ThenBy(LandingRecordAddress, StringComparer.Ordinal)
                .First())
            // Two landings in one clock tick is the normal shape here, not a rarity, and every
            // replica must fold to the same head — so a tie is broken by the generation, which
            // every reader reads identically, never left to enumeration order.
            .OrderBy(r => r.RecordedAtUtc)
            .ThenBy(r => r.Directory, StringComparer.Ordinal)
            .ToImmutableList();

        var head = arrival[0];
        foreach (var incoming in arrival.Skip(1))
            if (!KeepsHead(head, incoming, present))
                head = incoming;

        var fallback = arrival
            .Where(r => !SameGeneration(r.Directory, head.Directory))
            .OrderByDescending(r => present(r.Directory))
            .ThenByDescending(r => linkable(r.Directory) switch { true => 2, null => 1, false => 0 })
            .ThenByDescending(r => r.Version, VersionRank.Instance)
            .ThenBy(r => r.RecordedAtUtc)
            .ThenBy(r => r.Directory, StringComparer.Ordinal)
            .FirstOrDefault();

        return new ModuleActivationEntry
        {
            Name = name,
            Source = head.Source,
            PackagePath = head.PackagePath ?? stored?.PackagePath,
            Directory = head.Directory,
            Version = head.Version,
            FrameworkMvid = head.FrameworkMvid,
            MinMeshVersion = head.MinMeshVersion,
            SourceCommit = head.SourceCommit,
            PreviousDirectory = fallback?.Directory,
            PreviousVersion = fallback?.Version,
            PreviousFrameworkMvid = fallback?.FrameworkMvid,
            PreviousSourceCommit = fallback?.SourceCommit,
            Enabled = true,
        };
    }

    /// <summary>
    /// #3996, as the replay applies it: the head STAYS when the arriving landing is a shelf landing
    /// ranking strictly below it and the head's bytes are PRESENT. Both versions must be orderable
    /// — an unknown or non-SemVer label is absence of evidence, not evidence of olderness (rule R2
    /// of the module adoption policy). A head whose bytes are GONE is displaced regardless:
    /// protecting it would make the rule a self-sealing outage.
    /// </summary>
    private static bool KeepsHead(
        ModuleLandingRecord current, ModuleLandingRecord incoming, Func<string, bool> present) =>
        incoming.YieldsToNewerHead
        && IsOrderableVersion(incoming.Version)
        && IsOrderableVersion(current.Version)
        && NuGetVersionComparer.Instance.Compare(incoming.Version, current.Version) < 0
        && present(current.Directory);

    /// <summary>
    /// The generations an ENABLED stored entry names, as candidates. From an entry an image that
    /// predates the records wrote (<paramref name="olderImageAt"/> set), its head is a LANDING at
    /// the file's write time — that image's install, ordered against every other event — and its
    /// fallback an earliest arrival. From a current image's projection, only the generations no
    /// record names, both as the earliest arrivals and neither yielding, so among themselves they
    /// reproduce the stored head and they never outrank a record.
    /// </summary>
    private static IEnumerable<ModuleLandingRecord> StoredCandidates(
        ModuleActivationEntry? stored, DateTime? olderImageAt, IReadOnlyList<ModuleLandingRecord> records)
    {
        if (stored is not { Enabled: true })
            yield break;
        bool Unrecorded(string generation) => !records.Any(r => SameGeneration(r.Directory, generation));
        if (ModuleActivationBoot.PreviousGeneration(stored) is { Directory.Length: > 0 } previous
            && Unrecorded(previous.Directory!))
            yield return new ModuleLandingRecord
            {
                Name = stored.Name,
                Source = stored.Source,
                PackagePath = stored.PackagePath,
                Directory = previous.Directory!,
                Version = previous.Version,
                FrameworkMvid = previous.FrameworkMvid,
                MinMeshVersion = stored.MinMeshVersion,
                SourceCommit = previous.SourceCommit,
                RecordedAtUtc = DateTime.MinValue,
            };
        if (!string.IsNullOrWhiteSpace(stored.Directory)
            && (olderImageAt is not null || Unrecorded(stored.Directory!)))
            yield return new ModuleLandingRecord
            {
                Name = stored.Name,
                Source = stored.Source,
                PackagePath = stored.PackagePath,
                Directory = stored.Directory!,
                Version = stored.Version,
                FrameworkMvid = stored.FrameworkMvid,
                MinMeshVersion = stored.MinMeshVersion,
                SourceCommit = stored.SourceCommit,
                RecordedAtUtc = olderImageAt ?? DateTime.MinValue.AddTicks(1),
            };
    }

    private static bool SameGeneration(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a generation's entry DLL is on the volume, through
    /// <see cref="ModuleActivationBoot.LandedModuleDllExists"/> — the ONE resolution rule boot's
    /// own gate applies (#1949) — memoised for the life of one read, so a derivation asks the
    /// volume once per generation. A fresh function per read: nothing outlives the call.
    /// </summary>
    private static Func<string, string, bool> GenerationPresence(string baseDirectory)
    {
        var known = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        return (name, generation) =>
        {
            if (string.IsNullOrWhiteSpace(generation))
                return false;
            if (!known.TryGetValue(generation, out var exists))
                known[generation] = exists = ModuleActivationBoot.LandedModuleDllExists(
                    baseDirectory, new ModuleActivationEntry { Name = name, Directory = generation });
            return exists;
        };
    }

    /// <summary>Whether <paramref name="candidate"/> is a SemVer version
    /// <see cref="NuGetVersionComparer"/> orders meaningfully: a numeric dotted core of one to four
    /// parts, optionally a pre-release and build metadata. The comparer reads any unparseable part
    /// as 0, so without this check "nightly" would rank below 0.0.1 and be shelved for good. The ONE
    /// copy — the landing decision and the derivation must agree about it.</summary>
    internal static bool IsOrderableVersion(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return false;
        var plus = candidate.IndexOf('+');
        var withoutBuild = plus >= 0 ? candidate[..plus] : candidate;
        var dash = withoutBuild.IndexOf('-');
        var core = dash >= 0 ? withoutBuild[..dash] : withoutBuild;
        var coreParts = core.Split('.');
        return coreParts.Length is >= 1 and <= 4
               && coreParts.All(part => part.Length > 0 && part.All(char.IsAsciiDigit))
               && (dash < 0 || withoutBuild[(dash + 1)..].Split('.').All(id =>
                   id.Length > 0 && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')));
    }

    /// <summary>Orders version LABELS: an absent one lowest, every other by the comparer the head
    /// rule and <c>ModuleUpdateDecision</c> use — that identity is load-bearing rather than tidy.
    /// Stateless, so its single instance is a constant.</summary>
    private sealed class VersionRank : IComparer<string?>
    {
        internal static readonly VersionRank Instance = new();

        public int Compare(string? x, string? y)
        {
            var xKnown = !string.IsNullOrWhiteSpace(x);
            var yKnown = !string.IsNullOrWhiteSpace(y);
            if (!xKnown || !yKnown)
                return xKnown == yKnown ? 0 : xKnown ? 1 : -1;
            return NuGetVersionComparer.Instance.Compare(x, y);
        }
    }

    /// <summary>
    /// 🚨 RETENTION for the records — without it, deriving the answer would trade a race for
    /// unbounded growth (a record per publish per module for the life of a deployment). An event (a
    /// landing record or a tombstone) is removed ONLY when the entry derived without it is IDENTICAL
    /// to the entry derived with it, for EVERY platform a verdict exists for and for the platform
    /// with none: retention can never change what <see cref="Read"/> answers on any image, which is
    /// a rule that can be stated and tested instead of one that has to be trusted. A verdict goes
    /// once a newer one for its generation and platform supersedes it, or once no remaining record
    /// names its generation. On top of that it keeps everything younger than
    /// <paramref name="cutoffUtc"/> (a write on another replica this one may not see whole yet —
    /// #2303's window) and every record of a generation the STORED entry names (what an older image
    /// boots from).
    ///
    /// <para>🚨 It removes RECORDS, never generation directories. Reclaiming bytes stays with
    /// <c>ModuleLandingService.CollectGarbage</c> behind its grace period and its fail-closed
    /// reference set. A module with ANY unreadable record is left entirely alone — unreadable is
    /// never unneeded (#2509).</para>
    /// </summary>
    /// <returns>How many record files were removed.</returns>
    internal static int PruneLandingRecords(
        string baseDirectory, string moduleName, ModuleActivationEntry? stored, DateTime cutoffUtc,
        Action<string>? onRemoved = null)
    {
        var faults = 0;
        void Fault(string _) => faults++;
        var records = ReadLandingFiles(baseDirectory, moduleName, Fault);
        var uninstalls = ReadUninstallFiles(baseDirectory, moduleName, Fault);
        var verdicts = ReadVerdictFiles(baseDirectory, moduleName, Fault);
        if (faults > 0 || (records.IsEmpty && uninstalls.IsEmpty && verdicts.IsEmpty))
            return 0;

        var name = stored?.Name ?? moduleName;
        var storedAt = StoredAt(baseDirectory, name);
        var presence = GenerationPresence(baseDirectory);
        bool Present(string generation) => presence(name, generation);
        var storedGenerations = new[] { stored?.Directory, stored?.PreviousDirectory }
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
        var log = new ModuleLog(
            [.. records.Select(f => f.Record)], [.. uninstalls.Select(f => f.Record)],
            [.. verdicts.Select(f => f.Record)]);
        var platforms = log.Platforms.ToImmutableList();

        ImmutableList<ModuleActivationEntry?> DeriveAll(
            ImmutableList<ModuleLandingRecord> r, ImmutableList<ModuleUninstallRecord> u) =>
            [.. platforms.Select(platform =>
                DeriveEntry(name, stored, storedAt, r, u, Present, log.LinkableOn(() => platform)))];

        var keptRecords = log.Records;
        var keptUninstalls = log.Uninstalls;
        var target = DeriveAll(keptRecords, keptUninstalls);
        var removed = 0;

        bool Delete(string path, string what)
        {
            try
            {
                File.Delete(path);
                removed++;
                onRemoved?.Invoke($"module '{name}': retired {what} '{Path.GetFileName(path)}'.");
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Held open, or a read-only volume: the file stays, which changes nothing — the
                // derivation without it was proven identical — and a later pass retires it.
                onRemoved?.Invoke(
                    $"module '{name}': {what} '{Path.GetFileName(path)}' is no longer needed but "
                    + $"could not be removed ({e.Message}) — a later pass retires it.");
                return false;
            }
        }

        var events = records.Select(f => (f.Path, At: f.Record.RecordedAtUtc, Landing: (ModuleLandingRecord?)f.Record, Uninstall: (ModuleUninstallRecord?)null))
            .Concat(uninstalls.Select(f => (f.Path, At: f.Record.RecordedAtUtc, Landing: (ModuleLandingRecord?)null, Uninstall: (ModuleUninstallRecord?)f.Record)))
            .OrderBy(e => e.At)
            .ThenBy(e => e.Path, StringComparer.Ordinal)
            .ToImmutableList();
        foreach (var item in events)
        {
            if (item.At > cutoffUtc)
                continue;
            if (item.Landing is { } landing)
            {
                if (storedGenerations.Contains(landing.Directory))
                    continue;
                var without = keptRecords.Remove(landing);
                if (!DeriveAll(without, keptUninstalls).SequenceEqual(target) || !Delete(item.Path, "landing record"))
                    continue;
                keptRecords = without;
            }
            else if (item.Uninstall is { } uninstall)
            {
                var without = keptUninstalls.Remove(uninstall);
                if (!DeriveAll(keptRecords, without).SequenceEqual(target) || !Delete(item.Path, "uninstall tombstone"))
                    continue;
                keptUninstalls = without;
            }
        }

        foreach (var (path, verdict) in verdicts)
        {
            if (verdict.RecordedAtUtc > cutoffUtc)
                continue;
            var superseded = !Equals(NewestVerdict(log.Verdicts, verdict.Directory, verdict.Platform), verdict);
            var unnamed = !storedGenerations.Contains(verdict.Directory)
                && !keptRecords.Any(r => SameGeneration(r.Directory, verdict.Directory));
            if (superseded || unnamed)
                Delete(path, "platform verdict");
        }
        return removed;
    }

    /// <summary>Removes every temp file a crashed record write left in <c>activation.d/</c>, once
    /// older than <paramref name="cutoffUtc"/>. Nothing reads them and nothing renames one after
    /// its writer is gone, so an old one is pure residue.</summary>
    internal static int PruneLandingTemps(string baseDirectory, DateTime cutoffUtc)
    {
        var directory = EntriesDirectory(baseDirectory);
        if (!Directory.Exists(directory))
            return 0;
        var removed = 0;
        foreach (var temp in Directory.EnumerateFiles(directory, "*" + LandingTempSuffix).ToArray())
        {
            if (File.GetLastWriteTimeUtc(temp) > cutoffUtc)
                continue;
            if (DeleteTemp(temp))
                removed++;
        }
        return removed;
    }

    /// <summary>
    /// Removes one module's WHOLE record directory content — landing records, tombstones and
    /// verdicts. Only the bulk <see cref="Write"/> does this: it states the answer outright, so no
    /// event may outrank it. An uninstall never does — it writes a tombstone — because deleting
    /// events is not an order anything can rely on.
    /// </summary>
    public static void RemoveLandingRecords(string baseDirectory, string moduleName)
    {
        var directory = LandingRecordsDirectory(baseDirectory, moduleName);
        if (!Directory.Exists(directory))
            return;
        foreach (var path in Directory.EnumerateFiles(directory)
                     .Where(p => p.EndsWith(".json", StringComparison.Ordinal)
                                 || p.EndsWith(UninstallSuffix, StringComparison.Ordinal)
                                 || p.EndsWith(VerdictSuffix, StringComparison.Ordinal))
                     .ToArray())
            File.Delete(path);
    }

    private static bool DeleteTemp(string temp)
    {
        try
        {
            File.Delete(temp);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A temp file nothing reads: whatever failed the caller is what surfaces, and the
            // modules GC retires the residue once it is older than its grace.
            return false;
        }
    }

    // ── the plan-tier refusal marker (#4097) ──────────────────────────────────

    /// <summary>The per-module plan-tier refusal marker's file suffix inside
    /// <see cref="EntriesDirectoryName"/> — like <see cref="RefusedMarkerSuffix"/>, deliberately
    /// not <c>.json</c>, so entry enumeration never parses it and the bulk write never sweeps it.</summary>
    public const string TierRefusedMarkerSuffix = ".tier-refused";

    /// <summary>The marker recording that the registry refuses a module's package to this
    /// instance's PLAN: <c>modules/activation.d/&lt;Module&gt;.tier-refused</c>.</summary>
    public static string TierRefusedMarkerPath(string baseDirectory, string moduleName) =>
        Path.Combine(EntriesDirectory(baseDirectory), moduleName + TierRefusedMarkerSuffix);

    /// <summary>
    /// 🚨 Makes the marker set MATCH <paramref name="refusals"/> — one marker per refusal that
    /// names a module, every other <c>.tier-refused</c> marker removed. The default-install pass
    /// calls it with the registry's answer of THIS boot, so a plan upgrade (the registry stops
    /// refusing) clears the markers on the next pass and a downgrade writes them; a stale marker
    /// would otherwise say "refused by plan" on a plan that covers the package. A marker whose
    /// content is unchanged is not rewritten (the directory's write time is the report's cache
    /// fingerprint). Refusals for content-only packages carry no module and leave no marker — the
    /// package card renders them from the listing.
    /// </summary>
    /// <returns>The module names that carry a marker after the sync, sorted.</returns>
    public static ImmutableList<string> SyncTierRefusals(
        string baseDirectory, IEnumerable<Mesh.Security.PlanTierRefusal> refusals)
    {
        ArgumentNullException.ThrowIfNull(refusals);
        var wanted = new Dictionary<string, Mesh.Security.PlanTierRefusal>(StringComparer.OrdinalIgnoreCase);
        foreach (var refusal in refusals)
            // 🚨 The module name arrives from the REGISTRY and becomes a file name. One that is not
            // a valid module name (which no assembly simple name is) gets no marker rather than an
            // exception: the refusal is still on the ledger and the card, and one malformed entry
            // must not abort the pass that records every other one.
            if (IsValidModuleName(refusal.Module))
                wanted[refusal.Module!] = refusal;

        var current = ReadTierRefusals(baseDirectory);
        foreach (var stale in current.Keys.Where(name => !wanted.ContainsKey(name)))
            ClearTierRefused(baseDirectory, stale);
        foreach (var (module, refusal) in wanted)
        {
            if (current.TryGetValue(module, out var existing) && existing == refusal)
                continue;
            WriteAtomic(TierRefusedMarkerPath(baseDirectory, module), JsonSerializer.Serialize(refusal, Json));
        }
        return [.. wanted.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Removes one module's plan-tier marker. Tolerant of an absent file and a read-only
    /// volume, like <see cref="ClearRefused"/>.</summary>
    public static void ClearTierRefused(string baseDirectory, string moduleName)
    {
        ValidateModuleName(moduleName);
        try
        {
            File.Delete(TierRefusedMarkerPath(baseDirectory, moduleName));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Another replica clearing the same marker, or a read-only volume.
        }
    }

    /// <summary>Every plan-tier refusal marker under the deployment, module name → refusal, sorted
    /// by name. An unreadable or malformed marker contributes nothing.</summary>
    public static ImmutableSortedDictionary<string, Mesh.Security.PlanTierRefusal> ReadTierRefusals(string baseDirectory)
    {
        var builder = ImmutableSortedDictionary.CreateBuilder<string, Mesh.Security.PlanTierRefusal>(StringComparer.OrdinalIgnoreCase);
        var directory = EntriesDirectory(baseDirectory);
        if (!Directory.Exists(directory))
            return builder.ToImmutable();
        string[] files;
        try
        {
            files = Directory.EnumerateFiles(directory, "*" + TierRefusedMarkerSuffix).ToArray();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return builder.ToImmutable();
        }
        foreach (var file in files)
        {
            var name = Path.GetFileName(file)[..^TierRefusedMarkerSuffix.Length];
            try
            {
                var text = TryReadAllText(file);
                if (text is not null
                    && JsonSerializer.Deserialize<Mesh.Security.PlanTierRefusal>(text, Json) is { } refusal)
                    builder[name] = refusal;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
                // Unreadable or malformed: no verdict, never a wrong one.
            }
        }
        return builder.ToImmutable();
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

    // ── the refused-landing marker (#4083) ──────────────────────────────────

    /// <summary>
    /// 🚨 What a LANDING refused, and why — the marker that makes a refusal visible where a person
    /// looks (#4083). A refusal at landing writes no generation and no entry (that is the point:
    /// the previous generation keeps serving), so before this marker the only trace of "module X
    /// was not installed because its bytes cannot bind here" was one warning line in a pod log.
    /// The marker is the landing path's counterpart of <see cref="UnloadableGeneration"/> (which
    /// only boots write): written by the landing that refused, cleared by the next landing of the
    /// same module that lands anything, and by uninstall.
    /// </summary>
    /// <param name="Version">The package version whose bytes were refused, when known.</param>
    /// <param name="PackagePath">The mesh path of the install record that asked, when recorded —
    /// what the package card matches on.</param>
    /// <param name="Reason">The status-line sentence, e.g. <c>held: references YamlDotNet
    /// 18.1.0.0, platform provides 16.3.0.0</c> (<c>ModuleLinkVerdict.HoldSummary</c>).</param>
    /// <param name="RefusedAt">When the landing refused it.</param>
    /// <param name="Needs">What the module needs, as data for a localized line
    /// (<c>YamlDotNet 18.1.0.0</c>); null when not measured that way.</param>
    /// <param name="Provides">What this platform provides for it (<c>16.3.0.0</c>); null for a
    /// refusal that is not a binding conflict.</param>
    public sealed record RefusedLanding(
        string? Version, string? PackagePath, string Reason, DateTimeOffset RefusedAt,
        string? Needs = null, string? Provides = null);

    /// <summary>The per-module refusal marker's file suffix inside <see cref="EntriesDirectoryName"/>
    /// — like <see cref="UnloadableMarkerSuffix"/>, deliberately not <c>.json</c>.</summary>
    public const string RefusedMarkerSuffix = ".refused";

    /// <summary>The marker file recording that the last landing of a module was REFUSED here:
    /// <c>modules/activation.d/&lt;Name&gt;.refused</c>.</summary>
    public static string RefusedMarkerPath(string baseDirectory, string moduleName) =>
        Path.Combine(EntriesDirectory(baseDirectory), moduleName + RefusedMarkerSuffix);

    /// <summary>Records that a landing of <paramref name="moduleName"/> was refused — an atomic
    /// replace of that module's own marker. Idempotent.</summary>
    public static void SetRefused(string baseDirectory, string moduleName, RefusedLanding refusal)
    {
        ValidateModuleName(moduleName);
        ArgumentNullException.ThrowIfNull(refusal);
        Directory.CreateDirectory(EntriesDirectory(baseDirectory));
        WriteAtomic(RefusedMarkerPath(baseDirectory, moduleName), JsonSerializer.Serialize(refusal, Json));
    }

    /// <summary>Removes the module's refusal marker — a later landing landed something, or the
    /// module was uninstalled. Tolerant of an absent file and a read-only volume.</summary>
    public static void ClearRefused(string baseDirectory, string moduleName)
    {
        ValidateModuleName(moduleName);
        try
        {
            File.Delete(RefusedMarkerPath(baseDirectory, moduleName));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Another replica clearing the same marker, or a read-only volume.
        }
    }

    /// <summary>The module's refusal marker, or null when there is none or it cannot be read.</summary>
    public static RefusedLanding? ReadRefused(string baseDirectory, string moduleName)
    {
        if (string.IsNullOrWhiteSpace(moduleName))
            return null;
        try
        {
            var text = TryReadAllText(RefusedMarkerPath(baseDirectory, moduleName));
            return text is null ? null : JsonSerializer.Deserialize<RefusedLanding>(text, Json);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Every refusal marker under the deployment, module name → marker, sorted by name.
    /// A refused FIRST install has no activation entry at all, so the report enumerates the
    /// markers rather than the entries.</summary>
    public static ImmutableSortedDictionary<string, RefusedLanding> ReadRefusals(string baseDirectory)
    {
        var builder = ImmutableSortedDictionary.CreateBuilder<string, RefusedLanding>(StringComparer.OrdinalIgnoreCase);
        var directory = EntriesDirectory(baseDirectory);
        if (!Directory.Exists(directory))
            return builder.ToImmutable();
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(directory, "*" + RefusedMarkerSuffix).ToArray();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return builder.ToImmutable();
        }
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            name = name[..^RefusedMarkerSuffix.Length];
            if (ReadRefused(baseDirectory, name) is { } refusal)
                builder[name] = refusal;
        }
        return builder.ToImmutable();
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

    /// <summary>Whether <paramref name="moduleName"/> may become a file name under the entries
    /// directory — the predicate behind <see cref="ValidateModuleName"/>.</summary>
    private static bool IsValidModuleName(string? moduleName) =>
        !string.IsNullOrWhiteSpace(moduleName)
        && moduleName is not ("." or "..")
        && moduleName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && !moduleName.Contains('/') && !moduleName.Contains('\\');

    /// <summary>The same rule <see cref="WriteEntry"/> applies: the name BECOMES a path.</summary>
    private static void ValidateModuleName(string? moduleName)
    {
        if (!IsValidModuleName(moduleName))
            throw new ArgumentException(
                $"'{moduleName}' is not a valid module name — a marker is a file named after its module.",
                nameof(moduleName));
    }

    /// <summary>
    /// Records ONE module's STORED activation entry — the per-module file, which is why concurrent
    /// landings of different modules cannot contend or lose each other's work. Atomic: serialized
    /// to a temp file in the same directory, then renamed into place.
    ///
    /// <para>🚨 Since #4026 this is the answer for images that predate the landing records, and
    /// nothing a current image decides from. A landing writes its <see cref="ModuleLandingRecord"/>
    /// first (<see cref="WriteLanding"/>), an uninstall its tombstone (<see cref="WriteUninstall"/>),
    /// and each then writes this file as a PROJECTION of what it derived, marked with
    /// <see cref="ModuleActivationEntry.ProjectionOf"/>. Two replicas may still overwrite each
    /// other's projection — it is last-writer-wins, for the older images that read it — but a
    /// current image orders the records and tombstones themselves, so a stale projection moves
    /// nothing there.</para>
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

        // 🚨 Bulk means bulk, and since #4026 the landing records decide a module's generations.
        // Leaving them behind would make the list this call states differ from the one Read
        // answers — a module would come back at a generation the caller did not name.
        foreach (var moduleName in ModuleNamesWithLandingRecords(baseDirectory))
            RemoveLandingRecords(baseDirectory, moduleName);

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
public sealed record EffectiveModule(string Entry, ModuleActivationEntry? Landed)
{
    /// <summary>
    /// The raw <c>Modules:Assemblies</c> entry this landed module DISPLACED — set only when
    /// <see cref="Landed"/> is non-null and the image lists the same module (#3735). The boot
    /// hands it to the loader as the last step of the fallback order
    /// (<c>ModuleInstallCandidate.ImageBaseline</c>): when the landed generation does not load
    /// here and no previous generation does either, the image's own copy runs. Null for a
    /// baseline entry (it IS the image copy) and for a Store-only module (the image ships none).
    /// Init-only, for binary compatibility with hosts compiled against the two-argument record.
    /// </summary>
    public string? BaselineEntry { get; init; }

    /// <summary>
    /// 🚨 Resolve this BASELINE entry to the IMAGE's copy specifically, never to a landed one
    /// (#4161) — set only on a baseline entry whose same-named store copy was DECLINED as built
    /// for another platform, and never otherwise.
    ///
    /// <para><b>Without it the decline is a no-op for one layout.</b>
    /// <see cref="ModuleActivationBoot.ResolveLoadPath(string, EffectiveModule)"/> sends a baseline entry through
    /// <c>MeshBuilder.ResolveModulePath</c>, whose probes are <b>landed root → image → app
    /// closure</b>, and whose landed probe looks in the FIXED
    /// <c>modules/&lt;name&gt;/&lt;name&gt;.dll</c>. Landing writes GENERATIONS
    /// (<c>modules/&lt;name&gt;@&lt;gen&gt;/</c>), so that probe misses for every
    /// generation-landed module and the image copy wins by itself — but an entry from before
    /// generation landing carries no <see cref="ModuleActivationEntry.Directory"/> and its bytes
    /// sit in exactly that fixed folder, so the resolver would hand back the copy pass 1 had just
    /// declined, silently and with the decline line already printed.</para>
    ///
    /// <para>Init-only, for binary compatibility with hosts compiled against the earlier record.
    /// Default <c>false</c> = today's probe order, which is correct for every baseline entry that
    /// displaced nothing.</para>
    /// </summary>
    public bool PreferImageCopy { get; init; }
}

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
                    ? entry with
                    {
                        PreviousDirectory = null, PreviousVersion = null,
                        PreviousFrameworkMvid = null, PreviousSourceCommit = null,
                    }
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
    ///     deployment must boot. 🚨 Since #4161 there is a SECOND report on that channel, and it
    ///     exists only on the seven-argument overload: a store copy whose recorded
    ///     <see cref="ModuleActivationEntry.FrameworkMvid"/> differs from the identity the booting
    ///     platform states is DECLINED where the image ships a copy of the same module, so the
    ///     image's own copy runs. This overload states no identity and therefore declines nothing —
    ///     see that overload's <c>liveFrameworkIdentity</c> for the three bounds and the reason a
    ///     Store-only module and an unrecorded identity are both untouched.
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
        Action<string, string>? onAdvisory = null) =>
        ComputeEffectiveModuleEntries(baselineEntries, persisted, platformGate,
            landedModuleDllExists, onSkipped, onAdvisory, liveFrameworkIdentity: null);

    /// <summary>
    /// <see cref="ComputeEffectiveModuleEntries(IReadOnlyList{string}, ModuleActivationList, Func{string, string}, Func{ModuleActivationEntry, bool}, Action{string, string}, Action{string, string})"/>
    /// with the FRAMEWORK IDENTITY the booting platform reports stated explicitly (#4161) — the
    /// one rule the six-argument shape cannot express, and the discriminator between two copies of
    /// the same module. See <paramref name="liveFrameworkIdentity"/>.
    /// </summary>
    /// <param name="baselineEntries">The raw <c>Modules:Assemblies</c> values (may be null/empty).</param>
    /// <param name="persisted">The sidecar list (may be null).</param>
    /// <param name="platformGate">Words the declared-floor ADVISORY; skips nothing (#3648).</param>
    /// <param name="landedModuleDllExists">Whether a persisted module's LANDED entry DLL exists.</param>
    /// <param name="onSkipped">The loud channel, one call per persisted entry that does NOT become
    /// effective, with (module name, reason) — a missing DLL, or a DECLINE per
    /// <paramref name="liveFrameworkIdentity"/>.</param>
    /// <param name="onAdvisory">The advisory channel (#3648), for EFFECTIVE entries only.</param>
    /// <param name="liveFrameworkIdentity">
    /// The framework build identity THIS process runs — production passes
    /// <c>PrebuiltAssemblySeeder.LiveFrameworkMvid</c>, the same value every other consumer of the
    /// identity reads, so no two gates can disagree about what "the framework identity" is.
    ///
    /// <para>🚨 <b>When it is stated, it DECIDES between an image copy and a store copy of the same
    /// module</b> — and only then. A store entry whose recorded
    /// <see cref="ModuleActivationEntry.FrameworkMvid"/> differs from this value is DECLINED when
    /// (and only when) <paramref name="baselineEntries"/> ships a copy of that same module: the
    /// image's own copy is preferred and the store copy is reported on
    /// <paramref name="onSkipped"/>. Measured on memex-local 2026-09-08 (Plugins#1483): a
    /// 2026-08-27 store pack of <c>MeshWeaver.Blazor.Views</c> displaced the image's same-day
    /// source build, the link probe passed — linked types EXIST, which is a different property
    /// from "correct for this platform" — the module loaded, and its view registrations no longer
    /// matched the control types the platform emits, so the Subscribe panel's outermost control
    /// rendered as <c>StackControl { … }</c>: a whole-tree <c>ToString()</c>.</para>
    ///
    /// <para>🚨 <b>Null or blank on EITHER side states nothing and decides nothing</b> — rule R2 of
    /// <c>Doc/Architecture/ModuleAdoptionPolicy</c>: an unrecorded identity is absence of evidence,
    /// not evidence of difference, exactly as an unrecorded VERSION is to
    /// <see cref="ModuleLandingService"/>. This is deliberately NOT
    /// <c>PrebuiltAssemblySeeder.DeclineReason</c>, which declines an absent identity: declining is
    /// the safe answer there (the caller compiles either way) and the DAMAGING one here, where it
    /// would refuse every module landed before identities were recorded at all.</para>
    ///
    /// <para>🚨 <b>Nor does it touch a Store-only module</b> (one the image ships no copy of). There
    /// is nothing to prefer it TO, and declining would turn a module that works into one that is
    /// absent — the strictly worse outcome that the "an unusable one must not override" rule below
    /// exists to avoid.</para>
    ///
    /// <para>The decline is SELF-HEALING and needs no operator: the update reconcile lands a bundle
    /// whose served identity differs from the landed one at the same version
    /// (<see cref="ModuleUpdateDecision"/>, rule R3), so the moment the registry serves this module
    /// built against this platform it lands and wins again. And it cannot tear a replica set
    /// apart (#3395): every replica of a deployment runs one image, so every replica states one
    /// identity and reaches one verdict.</para>
    /// </param>
    public static ImmutableList<EffectiveModule> ComputeEffectiveModuleEntries(
        IReadOnlyList<string>? baselineEntries,
        ModuleActivationList? persisted,
        Func<string?, string?> platformGate,
        Func<ModuleActivationEntry, bool> landedModuleDllExists,
        Action<string, string>? onSkipped,
        Action<string, string>? onAdvisory,
        string? liveFrameworkIdentity)
    {
        var effective = ImmutableList.CreateBuilder<EffectiveModule>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // The module names the IMAGE ships. Pass 2 walks the same list for ORDER; this set answers
        // MEMBERSHIP, which Pass 1's identity discriminator needs before Pass 2 has run: an image
        // copy is the thing a declined store copy is preferred TO, and where the image ships none
        // there is nothing to decline in favour of.
        var imageShips = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var baseline in baselineEntries ?? [])
            if (!string.IsNullOrWhiteSpace(baseline))
                imageShips.Add(Path.GetFileNameWithoutExtension(baseline));

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
        // The names whose store copy was DECLINED below, so pass 2 can pin the baseline it emits to
        // the IMAGE's copy rather than letting the resolver's landed probe find the declined bytes
        // again (EffectiveModule.PreferImageCopy).
        var declined = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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

            // 🚨 #4161 — THE FRAMEWORK IDENTITY DECIDES BETWEEN TWO COPIES OF ONE MODULE, and the
            // DLL's existence does not. Where the image ships its own copy of this module and the
            // store copy states it was built against a DIFFERENT framework build, the image's copy
            // is the one that is correct for this platform BY CONSTRUCTION — it was compiled with
            // it — and the store copy is declined in its favour.
            //
            // What made this necessary: nothing upstream could see the difference. The link probe
            // in MeshBuilder.InstallAssemblies answers "do this module's linked types exist", which
            // is a DIFFERENT property from "is this module correct for this platform" — a view pack
            // links fine and still registers views against control types the platform no longer
            // emits. Measured on memex-local 2026-09-08 (Plugins#1483): a 2026-08-27 pack of
            // MeshWeaver.Blazor.Views displaced the image's same-day source build, linked, loaded,
            // and rendered the Subscribe panel's outermost control as `StackControl { … }` — a
            // whole-tree ToString(), which reads to a user as a broken page and to a log reader as
            // nothing at all.
            //
            // 🚨 Three bounds, each of which is a rule and not a caution:
            //   • UNRECORDED on either side states nothing (rule R2) — an unrecorded identity is
            //     absence of evidence, not evidence of difference, and reading it as a difference
            //     would decline every module landed before identities were recorded.
            //   • A STORE-ONLY module is never declined: with no image copy there is nothing to
            //     prefer it to, and a declined module is an ABSENT module — the strictly worse
            //     outcome the "an unusable one must not override" rule above exists to avoid.
            //   • It is the DECLARED floor's opposite number, not its return (#3648). The floor is
            //     a string the module's author WROTE about a platform they never saw, which is why
            //     it cannot gate; the identity is what the producing toolchain MEASURED about the
            //     bytes it emitted, and it is compared against what this process measures about
            //     itself. Re-arming the floor here would hold the fleet again; this cannot — it
            //     never removes a module, it only prefers the copy the image already ships.
            if (!string.IsNullOrWhiteSpace(liveFrameworkIdentity)
                && !string.IsNullOrWhiteSpace(module.FrameworkMvid)
                && !string.Equals(module.FrameworkMvid, liveFrameworkIdentity, StringComparison.Ordinal)
                && imageShips.Contains(module.Name))
            {
                onSkipped?.Invoke(module.Name,
                    $"declined: built for another platform (framework {module.FrameworkMvid}; this "
                    + $"deployment runs {liveFrameworkIdentity}) — the image's own copy runs "
                    + "instead. Its DLL exists and its types link, and neither of those says its "
                    + "registrations match the types this platform emits (#4161). No action is "
                    + "needed: publish this module built against this platform and the next "
                    + "reconcile lands it and it wins again.");
                declined.Add(module.Name);
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

            // 🚨 #3735 — the displaced baseline entry TRAVELS with the override. "An unusable one
            // must not [override]" above is decided on the DLL's existence alone; whether the
            // landed generation LOADS is measured later, by the link probe in
            // MeshBuilder.InstallModules — and when it does not, the image's copy is the one
            // thing that loads by construction. Dropping the entry here is how a refused store
            // generation came to shadow the working image copy (memex.systemorph.com, 2026-09-08).
            effective.Add(overrides.TryGetValue(name, out var winner)
                ? new EffectiveModule(winner.Name + ".dll", winner) { BaselineEntry = entry }
                : new EffectiveModule(entry, Landed: null) { PreferImageCopy = declined.Contains(name) });
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
    /// with <see cref="ModuleActivationEntry.Directory"/>, <see cref="ModuleActivationEntry.Version"/>,
    /// <see cref="ModuleActivationEntry.FrameworkMvid"/> and
    /// <see cref="ModuleActivationEntry.SourceCommit"/> taken from the <c>Previous*</c>
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
            SourceCommit = entry.PreviousSourceCommit,
            PreviousDirectory = null,
            PreviousVersion = null,
            PreviousFrameworkMvid = null,
            PreviousSourceCommit = null,
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
        string Name, string Generation, string? FrameworkMvid, bool Unloadable, string? Reason)
    {
        /// <summary>
        /// 🚨 <b>The third answer: this boot did not MEASURE these bytes at all</b> (MeshWeaver#3911).
        /// True when the loader fell back with <see cref="FallbackModule.RunsAlreadyLoadedCopy"/> —
        /// the default load context already held an assembly of that name, so
        /// <c>Assembly.LoadFrom</c> handed back that copy and the head generation never reached the
        /// loader. <see cref="Unloadable"/> is false, and that is NOT the same as "it loaded".
        ///
        /// <para><b>Why it cannot collapse into either.</b> Recorded as unloadable it would write
        /// the marker that makes the update reconcile permanently <c>SkipUnloadable</c> every
        /// rebuild of that version — a verdict on bytes nobody executed. Recorded as loaded it
        /// would CLEAR a marker an earlier boot wrote from a real measurement. So a boot that did
        /// not look writes nothing and clears nothing, and the fallback record — on stderr, in the
        /// log, and as a row on the activation report — is what says so out loud.</para>
        ///
        /// <para>An init property, not a fifth positional parameter: replacing a public record's
        /// constructor is a binary break for a host compiled against the previous platform, which
        /// is the incident <see cref="IncompatibleModule"/> exists for.</para>
        /// </summary>
        public bool HeadNotMeasured { get; init; }
    }

    /// <summary>
    /// Reads the loader's records back onto the entries it was handed (#3650): for every enabled
    /// store entry in <paramref name="tried"/> — the union AFTER
    /// <see cref="ProjectOntoMeshSet"/>, i.e. with <see cref="ModuleActivationEntry.Directory"/>
    /// naming the generation the loader actually tried — a <see cref="FallbackModule"/> or an
    /// <see cref="IncompatibleModule"/> whose generation IS that directory says the head did not
    /// load; the absence of both says it did. A record naming another generation of the same
    /// module says nothing about this one. Pure.
    ///
    /// <para>🚨 <b>THREE answers, never two.</b> A fallback carrying
    /// <see cref="FallbackModule.RunsAlreadyLoadedCopy"/> is the third: the head generation was
    /// never handed to the loader, because the default load context already held that name, so
    /// nothing about those bytes was measured. It comes back with
    /// <see cref="MeasuredLoadability.HeadNotMeasured"/> and <see cref="MeasuredLoadability.Unloadable"/>
    /// false, and <see cref="RecordMeasuredLoadability"/> neither writes nor clears its marker —
    /// see that property for why collapsing it into either of the other two is wrong in a
    /// different direction each way.</para>
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
            var notMeasured = fallback is { RunsAlreadyLoadedCopy: true };
            verdicts.Add(new MeasuredLoadability(
                entry.Name, entry.Directory!, entry.FrameworkMvid,
                Unloadable: !notMeasured && (fallback is not null || refused is not null),
                Reason: fallback?.Reason ?? refused?.Error)
            {
                HeadNotMeasured = notMeasured,
            });
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
            // 🚨 A boot that did not look writes nothing and clears nothing (#3911). The marker
            // answers "do these bytes load on this platform build"; this boot never asked, because
            // the load context already held the name. Writing it would permanently SkipUnloadable
            // every rebuild of a version nobody executed; clearing it would erase an earlier boot's
            // real measurement. The FallbackModule record is what makes the state visible.
            if (verdict.HeadNotMeasured)
            {
                onReport?.Invoke(
                    $"module '{verdict.Name}': generation '{verdict.Generation}' was NOT measured this "
                    + "boot — the load context already held that name, so its bytes never reached the "
                    + "loader. The unloadable marker is left exactly as it was: " + verdict.Reason);
                continue;
            }
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
            // 🚨 #4161 — a baseline that DISPLACED a declined store copy resolves with NO landed
            // root, so the probe order is image → app closure. See EffectiveModule.PreferImageCopy:
            // the landed probe's fixed modules/<name>/ folder is exactly where a pre-generation
            // landing's bytes sit, and handing those back would undo the decline in silence.
            : MeshBuilder.ResolveModulePath(
                module.Entry, module.PreferImageCopy ? null : baseDirectory);

    /// <summary>The entry's landed DLL as the deployment-relative path a skip report names —
    /// the generation directory when the entry carries one, else the legacy fixed folder.</summary>
    private static string RelativeLandedDll(ModuleActivationEntry entry) =>
        $"modules/{(string.IsNullOrWhiteSpace(entry.Directory) ? entry.Name : entry.Directory)}"
        + $"/{entry.Name}.dll";
}
