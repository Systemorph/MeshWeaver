using System.Collections.Concurrent;
using System.Collections.Immutable;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Hosting;

/// <summary>One NodeType whose content this process could not type, and how often.</summary>
/// <param name="NodeType">The node type (the key the content-type registry recovery ran under).</param>
/// <param name="Seam">The read seam that observed it first.</param>
/// <param name="Count">How many reads degraded.</param>
/// <param name="LastPath">The most recent node path.</param>
/// <param name="LastAt">When.</param>
public sealed record ContentDegradation(
    string NodeType, string Seam, int Count, string? LastPath, DateTimeOffset LastAt)
{
    /// <summary>
    /// The stored <c>$type</c>, when the degraded content carried one — the second key
    /// <see cref="ContentDegradationRegistry.Unresolved"/> re-asks the content-type registry under,
    /// for content whose NodeType is absent or unregistered.
    ///
    /// <para>🚨 An <c>init</c> PROPERTY, deliberately not a primary-constructor parameter. Adding a
    /// parameter — even with a default — REPLACES the record's constructor signature, so every
    /// assembly already compiled against the 5-parameter one calls a constructor this build no
    /// longer has. That is a binary break for module bundles built on a previous platform, and the
    /// repo's <c>Public surface (binary compatibility)</c> gate refuses it (it caught this exact
    /// change). A property leaves the arity untouched.</para>
    /// </summary>
    public string? Discriminator { get; init; }

    /// <summary>
    /// When the FIRST read counted in <see cref="Count"/> degraded — the opening of the window the
    /// count covers (Plugins#2812). <c>null</c> on an entry built by the 5-argument constructor
    /// (an assembly compiled before this property existed), where <see cref="WindowStart"/> falls
    /// back to <see cref="LastAt"/>. An <c>init</c> property for the same binary-compatibility
    /// reason as <see cref="Discriminator"/>.
    /// </summary>
    public DateTimeOffset? FirstAt { get; init; }

    /// <summary>The opening of the count's window: <see cref="FirstAt"/>, or <see cref="LastAt"/>
    /// when the entry carries no first-seen instant.</summary>
    public DateTimeOffset WindowStart => FirstAt ?? LastAt;

    /// <summary>
    /// The read seam of the MOST RECENT counted read — the seam that observed <see cref="LastPath"/>.
    /// <see cref="Seam"/> stays the first seam; a type degraded first through one seam and later
    /// through another must not be reported as the first seam having read the last path. <c>null</c>
    /// on an entry built by the 5-argument constructor; then <see cref="Seam"/> applies. An
    /// <c>init</c> property for the same binary-compatibility reason as <see cref="Discriminator"/>.
    /// </summary>
    public string? LastSeam { get; init; }
}

/// <summary>
/// 🚨 <b>What this replica could not read — kept, so it can be SEEN.</b> A node whose
/// <c>$type</c> resolves to no registered CLR type on the reading hub is degraded to an untyped
/// element and every view of it renders empty (<see cref="Mesh.MeshNodeContentDegradedException"/>).
/// That was a log line and nothing else: on 2026-09-08 both memex replicas served an empty
/// client page for hours while <c>/health</c> answered a bare <c>Degraded</c> that named no cause.
/// The stream cache records every degradation here; the host's <c>ContentTypeHealthCheck</c>
/// (Memex.Portal.ServiceDefaults) reports the set on <c>/health</c> with the node types named.
///
/// <para>A mesh-scoped instance singleton (registered beside the stream cache), never static —
/// its lifetime is the mesh's. Bounded: one entry per node type, counts only.</para>
/// </summary>
public sealed class ContentDegradationRegistry
{
    private readonly ConcurrentDictionary<string, ContentDegradation> byNodeType =
        new(StringComparer.Ordinal);

    // 1 while this replica's boot registration window is open — see DeferWarningsUntilRegistrationSettles.
    // Read and written ONLY under windowGate, together with the record it decides about.
    private int warningsDeferred;

    // 🚨 Makes "record this read, and is its warning deferred?" and "close the window, and which
    // records does the settle own?" ONE decision each: every read lands on exactly one side, so its
    // warning is written exactly once (review on #6405). A plain monitor around in-memory work —
    // never held across an await or a subscription; degradations are rare, so it is uncontended.
    private readonly object windowGate = new();

    /// <summary>
    /// 🚨 <b>Opens the boot registration window (Systemorph/MeshWeaver.Plugins#2799).</b> Until
    /// <see cref="SettleDeferredWarnings"/> is called, a read seam that degrades still RECORDS the
    /// degradation here — so <c>/health</c>'s <c>content-types</c> names it exactly as before — but
    /// does not log the "stayed an untyped JsonElement" warning at the read: that sentence is a
    /// verdict, and during the window it cannot be decided yet.
    ///
    /// <para>Measured on memex-cloud (pod <c>884964bb7-6gv59</c>, 2026-10-09): 132 of 134 such lines
    /// were written at 19:48:37.3, half a second BEFORE the pre-warmer even started (19:48:37.8),
    /// by boot-time readers (standing watches on <c>Ops/Status/*</c>, a <c>Posts</c> query) of
    /// dynamic types whose assemblies were all on the replica (<c>alreadyBaked=414</c>,
    /// <c>compiled=0</c>). Those types registered on this replica only when the registration-only
    /// pass reached them (<see cref="DynamicContentTypeRegistrar"/>). So the line asserted
    /// "consumers will fail" before that was decidable, and the burst was one per roll on every
    /// portal. The reads themselves are now typed by <see cref="ContentTypeOnDemandRegistration"/>
    /// before they answer; this window remains the diagnostic for whatever that route and the pass
    /// could not register.</para>
    ///
    /// <para>Called by <see cref="DynamicContentTypeRegistrationHostedService"/> — and only on a
    /// host that runs that pass: a host without it never opens the window, so every read there
    /// warns at the read, as it always did (every test host included).</para>
    /// </summary>
    public void DeferWarningsUntilRegistrationSettles()
    {
        lock (windowGate)
            warningsDeferred = 1;
    }

    /// <summary>Whether the boot registration window is open. Informational — a read seam decides
    /// with <see cref="RecordDeferringWarning"/>, which records and answers in one step.</summary>
    public bool WarningsDeferred
    {
        get
        {
            lock (windowGate)
                return warningsDeferred != 0;
        }
    }

    /// <summary>
    /// Records one degraded read (as <see cref="Record"/>) and answers, in the SAME step, whether
    /// its warning is deferred to <see cref="SettleDeferredWarnings"/> (<c>true</c>) or must be
    /// written by the caller now (<c>false</c>). Atomic with the settle: a read answered
    /// <c>true</c> is in the settle's snapshot, a read answered <c>false</c> is not — so a read that
    /// races the close is warned exactly once.
    /// </summary>
    /// <param name="nodeType">The node's NodeType.</param>
    /// <param name="nodePath">The node path.</param>
    /// <param name="seam">The read seam that observed it.</param>
    /// <param name="discriminator">The content's stored <c>$type</c>, when it carried one.</param>
    public bool RecordDeferringWarning(string? nodeType, string? nodePath, string seam, string? discriminator = null)
    {
        lock (windowGate)
        {
            Record(nodeType, nodePath, seam, discriminator);
            return warningsDeferred != 0;
        }
    }

    /// <summary>
    /// Closes the boot registration window and returns the degradations whose warning must now be
    /// written: every recorded one whose content type is STILL unresolvable
    /// (<see cref="Unresolved"/>). A type the pass registered is dropped — its readers were re-typed
    /// — and nothing that stayed untyped is: a deferred warning is re-timed to the moment the
    /// verdict is decidable, never dropped. Returns empty when the window was not open, so a second
    /// call cannot repeat the warnings.
    ///
    /// <para>Atomic with <see cref="RecordDeferringWarning"/>: the close and the snapshot happen
    /// under the same gate as a read's record-and-answer, so a read deferred before the close is in
    /// this snapshot and a read after it is answered "warn now" — never both, never neither.</para>
    /// </summary>
    /// <param name="contentTypes">The mesh-wide content-type registry, or <c>null</c> (then every
    /// recorded degradation is unresolved — see <see cref="Unresolved"/>).</param>
    public ImmutableList<ContentDegradation> SettleDeferredWarnings(IMeshContentTypeRegistry? contentTypes)
    {
        lock (windowGate)
        {
            if (warningsDeferred == 0)
                return ImmutableList<ContentDegradation>.Empty;
            warningsDeferred = 0;
            return Unresolved(contentTypes);
        }
    }

    /// <summary>Records one degraded read.</summary>
    /// <param name="nodeType">The node's NodeType.</param>
    /// <param name="nodePath">The node path.</param>
    /// <param name="seam">The read seam that observed it.</param>
    /// <param name="discriminator">The content's stored <c>$type</c>, when it carried one. Kept so
    /// <see cref="Unresolved"/> can re-ask the registry by NAME as well as by NodeType — the same
    /// two routes <c>TryRecoverForNodeType</c> takes.</param>
    public void Record(string? nodeType, string? nodePath, string seam, string? discriminator = null)
    {
        var key = string.IsNullOrEmpty(nodeType) ? "(no node type)" : nodeType;
        var now = DateTimeOffset.UtcNow;
        byNodeType.AddOrUpdate(
            key,
            _ => new ContentDegradation(key, seam, 1, nodePath, now)
            {
                Discriminator = discriminator, FirstAt = now, LastSeam = seam,
            },
            (_, existing) => existing with
            {
                Count = existing.Count + 1,
                LastPath = nodePath,
                LastSeam = seam,
                LastAt = now,
                // First-seen wins: a later read of the same NodeType whose element happens to carry
                // no $type must not erase the name the FIRST one gave us to re-ask under.
                Discriminator = existing.Discriminator ?? discriminator,
            });
    }

    /// <summary>Forgets a node type — called when a later read of it typed cleanly, so a
    /// degradation that a module load cured stops being reported.</summary>
    public void Clear(string? nodeType)
    {
        if (!string.IsNullOrEmpty(nodeType))
            byNodeType.TryRemove(nodeType, out _);
    }

    /// <summary>
    /// 🚨 <b>The verdict, as opposed to the event (#3645).</b> The entries whose content type is
    /// STILL unresolvable — re-asked against <paramref name="contentTypes"/> at the moment of the
    /// call, rather than believed from when the read happened.
    ///
    /// <para>A degradation is recorded at the INSTANT of a read, and at that instant nothing can
    /// know whether the type will register a moment later. That is the ordinary state during a
    /// portal boot — a NodeType's runtime compile lands after the first readers are served — and
    /// #2952 exists precisely to re-type those readers when the registration arrives. So a
    /// snapshot of what degraded answers <i>"content was unreadable at a read"</i>, while the
    /// question every consumer of this registry actually asks (<c>/health</c>, and the CI gate) is
    /// <i>"content is unreadable"</i>.</para>
    ///
    /// <para>Both routes <c>TryRecoverForNodeType</c> takes are re-asked, and nothing else: the
    /// EXACT route on the node's own NodeType, and the NAME route on the stored <c>$type</c>. Both
    /// are pure map lookups, so this needs no content and can be called on any thread, as often as
    /// a health probe likes.</para>
    ///
    /// <para>🚨 <b>A null registry means UNRESOLVED, never resolved.</b> There is no registry to
    /// clear an entry with, so the honest answer is the one that keeps reporting — a missing
    /// instrument may not read as a clean result.</para>
    /// </summary>
    /// <param name="contentTypes">The mesh-wide content-type registry, or <c>null</c>.</param>
    /// <returns>The still-unresolvable degradations, most recent first.</returns>
    public ImmutableList<ContentDegradation> Unresolved(IMeshContentTypeRegistry? contentTypes) =>
        Snapshot().Where(d => !IsResolvable(d, contentTypes)).ToImmutableList();

    /// <summary>Whether one recorded degradation's type resolves NOW. Pure given the registry.</summary>
    private static bool IsResolvable(ContentDegradation degradation, IMeshContentTypeRegistry? contentTypes)
    {
        if (contentTypes is null)
            return false;
        if (!string.IsNullOrEmpty(degradation.NodeType)
            && contentTypes.TryResolveByNodeType(degradation.NodeType, out _))
            return true;
        return !string.IsNullOrEmpty(degradation.Discriminator)
               && contentTypes.TryResolveByDiscriminator(degradation.Discriminator!, out _);
    }

    /// <summary>Every node type currently degraded, most recent first.</summary>
    public ImmutableList<ContentDegradation> Snapshot() =>
        byNodeType.Values.OrderByDescending(d => d.LastAt).ToImmutableList();

    /// <summary>True when nothing is degraded.</summary>
    public bool IsEmpty => byNodeType.IsEmpty;

    /// <summary>The check's name on <c>/health</c> (the host registers the check; the sentence
    /// is composed here so a test can pin it without a host).</summary>
    public const string HealthCheckName = "content-types";

    /// <summary>
    /// The one sentence an operator reads on <c>/health</c>. Pure.
    ///
    /// <para>🚨 <b>Every entry carries its WINDOW — when its first and its last counted read degraded
    /// (Plugins#2812).</b> An entry's PRESENCE is a live verdict (<see cref="Unresolved"/> re-asks the
    /// registry on every probe), but its <c>×count</c> is cumulative over the entry's lifetime. Printed
    /// bare, <c>Store/Tier ×377</c> could not tell "reads of this type are degrading now" from "377
    /// reads degraded in the two minutes after boot and nothing has read it since" — two readings an
    /// operator acts on differently, and the second was repeatedly taken for the first. With the
    /// window in the sentence one probe answers it; before, it took two probes and a diff of the
    /// counts.</para>
    /// </summary>
    public static string Describe(IReadOnlyList<ContentDegradation> degraded) =>
        degraded.Count == 0
            ? "every node content read on this replica typed"
            : $"{degraded.Count} node type(s) whose content this replica cannot type — the CLR type "
              + "is not registered in THIS process, so their pages render empty. Commonest cause, and "
              + "the one to check first: a dynamic NodeType registers its content type only when one "
              + "of its instances activates here, and a type this replica ADOPTED rather than compiled "
              + "is never activated by the bake — so a type with few instances, all activated on another "
              + "replica, is untypeable here with a perfectly usable assembly "
              + "(Doc/Architecture/DynamicContentTypeRegistration). Otherwise the module that declares "
              + "the type is not loaded here: its prebuilt bundle was declined, or its compiled assembly "
              + "is not on this replica. 🚨 bake-report DECIDES between them — if its no-usable-assembly "
              + "list does not name a type below, that type's assembly is present and the first cause is "
              + "the answer. Each type below is re-checked against the registry on every probe, so its "
              + "PRESENCE is current; its ×count is cumulative and its window says WHEN those reads "
              + "degraded — a window that closed long ago is a type still unregistered here that nothing "
              + "has read since, not reads failing now: "
              + string.Join("; ", degraded.Select(d =>
                  $"{d.NodeType} ×{d.Count} between {Stamp(d.WindowStart)} and {Stamp(d.LastAt)} "
                  + $"(last {d.LastPath})"));

    /// <summary>A UTC second-precision instant, culture-invariant (never the process culture).</summary>
    private static string Stamp(DateTimeOffset at) =>
        at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
}
